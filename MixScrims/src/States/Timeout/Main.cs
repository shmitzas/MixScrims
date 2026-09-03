using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal int timeoutCountCt { get; set; } = 3;
    internal int timeoutCountT { get; set; } = 3;

    internal enum TimeoutPending
    {
        None,
        CT,
        T
    }

    internal TimeoutPending timeoutPending = TimeoutPending.None;
    internal Queue<Team> timeoutQueue = new Queue<Team>();
    internal bool isTimeoutActive = false;
    internal int timeoutVoteYesCount = 0;
    internal int timeoutVoteNoCount = 0;
    internal int timeoutTotalEligibleVotes = 0;
    internal CancellationTokenSource? timeoutVoteTimer = null;
    internal bool isTimeoutVoteInProgress = false;
    internal Team timeoutVoteTeam = Team.None;
    // Who has already voted in the current timeout vote. The built-in menu closes after a
    // click so this never fires there; it exists because IMixScrims.CastTimeoutVote lets a
    // consumer call in repeatedly. Mirrors voteKickVotersCt/T.
    internal HashSet<ulong> timeoutVoters = new();

    internal bool isFreezeTime = false;

    // Snapshot fields for IMixScrims consumers (v2.0.0+). Set by StartTimeout /
    // BroadcastRemainingTimeoutTime; cleared by EndTimeout.
    internal Team? activeTimeoutTeam = null;
    internal int activeTimeoutRemainingSeconds = 0;

    /// <summary>
    /// Starts a timeout for the specified team
    /// </summary>
    internal void StartTimeout(Team team)
    {
        // If a timeout is already active, queue this one
        if (isTimeoutActive)
        {
            if (!timeoutQueue.Contains(team))
            {
                timeoutQueue.Enqueue(team);
                if (team == Team.CT)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.queued.ct"]);
                }
                else if (team == Team.T)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.queued.t"]);
                }
            }
            return;
        }

        // The request is being served now, so it is no longer pending. Without this
        // the next round_prestart still sees the latch and queues a second timeout.
        timeoutPending = TimeoutPending.None;

        isTimeoutActive = true;
        activeTimeoutTeam = team;
        activeTimeoutRemainingSeconds = cfg.TimeoutDurationSeconds;
        mixScrimsService.SetMatchState(MatchState.Timeout);
        PauseMatch();

        if (team == Team.CT)
        {
            timeoutCountCt--;
            PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.timeout.ct"]);
            PrintMessageToTeam(Team.CT, Core.Localizer["command.timeout.remaining_timeouts", timeoutCountCt, cfg.Timeouts]);
        }

        if (team == Team.T)
        {
            timeoutCountT--;
            PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.timeout.t"]);
            PrintMessageToTeam(Team.T, Core.Localizer["command.timeout.remaining_timeouts", timeoutCountT, cfg.Timeouts]);
        }
        mixScrimsService.RaiseTimeoutStarted(team, cfg.TimeoutDurationSeconds);
        BroadcastRemainingTimeoutTime(team);
        var endTimeoutToken = Core.Scheduler.DelayBySeconds(cfg.TimeoutDurationSeconds, EndTimeout);
        Core.Scheduler.StopOnMapChange(endTimeoutToken);
    }

    /// <summary>
    /// Ends timeout and starts the next one in queue if available
    /// </summary>
    internal void EndTimeout()
    {
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("EndTimeout: Called. Current state - isTimeoutActive: {IsActive}, timeoutPending: {Pending}, queueCount: {QueueCount}, isFreezeTime: {IsFreezeTime}",
                isTimeoutActive, timeoutPending, timeoutQueue.Count, isFreezeTime);
        }

        // Capture the outgoing team before clearing so TimeoutEnded gets fired with it.
        var endedTeam = activeTimeoutTeam;

        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.timeout.ended"]);
        isTimeoutActive = false;
        activeTimeoutTeam = null;
        activeTimeoutRemainingSeconds = 0;
        timeoutPending = TimeoutPending.None;

        if (endedTeam != null)
            mixScrimsService.RaiseTimeoutEnded(endedTeam.Value);

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("EndTimeout: Set isTimeoutActive=false, timeoutPending=None");
        }

        // Check if there's a queued timeout
        if (timeoutQueue.Count > 0)
        {
            var nextTeam = timeoutQueue.Dequeue();

            if (cfg.DetailedLogging)
            {
                logger.LogInformation("EndTimeout: Dequeued timeout for team {Team}. Remaining queue count: {Count}", nextTeam, timeoutQueue.Count);
            }

            // If we're in freeze time, start immediately
            if (isFreezeTime)
            {
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("EndTimeout: In freeze time, starting queued timeout immediately for team {Team}", nextTeam);
                }
                StartTimeout(nextTeam);
            }
            else
            {
                // Otherwise, set as pending for next freeze time
                timeoutPending = nextTeam == Team.CT ? TimeoutPending.CT : TimeoutPending.T;
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("EndTimeout: Not in freeze time, setting queued timeout as pending ({Pending}) for team {Team}", timeoutPending, nextTeam);
                }
                if (nextTeam == Team.CT)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.ct"]);
                }
                else if (nextTeam == Team.T)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.t"]);
                }
            }
        }
        else
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("EndTimeout: No queued timeouts, resuming match");
            }
            mixScrimsService.SetMatchState(MatchState.Match);
            UnpauseMatch();
        }
    }

    /// <summary>
    /// Initiates a timeout vote for the specified team.
    /// </summary>
    internal void StartTimeoutVote(IPlayer caller, Team team)
    {
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Called by {Caller} for team {Team}. isTimeoutVoteInProgress: {InProgress}",
                caller.Name, team, isTimeoutVoteInProgress);
        }

        // Prevent duplicate vote processing
        if (isTimeoutVoteInProgress)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogWarning("StartTimeoutVote: Vote already in progress, ignoring duplicate call");
            }
            return;
        }

        // reset tallies
        timeoutVoteYesCount = 1; // Caller's automatic yes vote
        timeoutVoteNoCount = 0;
        timeoutTotalEligibleVotes = 0;
        isTimeoutVoteInProgress = true;
        timeoutVoteTeam = team;
        timeoutVoteTimer?.Cancel();
        timeoutVoteTimer = null;
        // Seed with the caller so their implicit YES can't be cast a second time.
        timeoutVoters.Clear();
        { ulong seedSid = SafeSteamId(caller); if (seedSid != 0) timeoutVoters.Add(seedSid); }

        mixScrimsService.RaiseTimeoutVoteStarted(team);
        // Fire the caller's implicit YES vote through the same event surface so consumers
        // don't have to special-case a "vote started with N=1 caller yes" seed.
        {
            ulong callerSid; try { callerSid = caller.SteamID; } catch { callerSid = 0; }
            if (callerSid != 0)
                mixScrimsService.RaiseTimeoutVoteCast(callerSid, true, team);
        }

        var players = GetPlayersInTeam(team);
        if (players.Count == 0)
        {
            logger.LogWarning("StartTimeoutVote: Vote timeout was called for {Team} team, but there are no players", team);
            isTimeoutVoteInProgress = false;
            return;
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Total players in team: {Count}. Caller will be removed from voting list", players.Count);
        }

        // If team has 2 or fewer players, auto-pass the vote without showing menus
        if (players.Count <= 2)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("StartTimeoutVote: Team has {Count} players, auto-passing vote", players.Count);
            }
            timeoutPending = team == Team.CT ? TimeoutPending.CT : TimeoutPending.T;
            isTimeoutVoteInProgress = false;
            if (isFreezeTime)
            {
                StartTimeout(team);
                return;
            }
            if (team == Team.CT)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.ct"]);
            }
            else if (team == Team.T)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.t"]);
            }
            return;
        }

        players.RemoveAll(p => p.SteamID == caller.SteamID);
        timeoutTotalEligibleVotes = players.Count; // Store for consistent use across methods

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: After removing caller, {Count} players need to vote", timeoutTotalEligibleVotes);
        }

        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.timeout_vote"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var yesBtn = new ButtonMenuOption("Yes");
        yesBtn.Click += async (sender, args) =>
        {
            HandleTimeoutVote(args.Player, "Yes");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(yesBtn);

        var noBtn = new ButtonMenuOption("No");
        noBtn.Click += async (sender, args) =>
        {
            HandleTimeoutVote(args.Player, "No");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(noBtn);

        var menu = builder.Build();

        // Open menu for eligible players; bots auto-vote yes
        int botCount = 0;
        int menuOpenCount = 0;
        foreach (var player in players)
        {
            if (IsBot(player))
            {
                timeoutVoteYesCount++;
                botCount++;
                continue;
            }

            if (IsPlayerValid(player))
            {
                if (!suppressBuiltInMenus)
                    Core.MenusAPI.OpenMenuForPlayer(player, menu);
                menuOpenCount++;
            }
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Opened menu for {MenuCount} players, {BotCount} bots auto-voted yes. Current votes: {Yes} yes, {No} no out of {Total}",
                menuOpenCount, botCount, timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes);
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, TimeoutRequiredVotes()]);

        // Bots auto-vote yes, so the vote can already be settled before it opens.
        if (TryResolveTimeoutVoteEarly()) return;

        timeoutVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => TimeoutVoteResult(team));
        Core.Scheduler.StopOnMapChange(timeoutVoteTimer);

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Vote timer scheduled for {Seconds} seconds", cfg.DefaultVoteTimeSeconds);
        }
    }

    /// <summary>
    /// Majority of the whole team. The caller's implicit yes is seeded into the yes
    /// count but excluded from the eligible count, so the team is eligible + 1.
    /// </summary>
    internal int TimeoutRequiredVotes() => (timeoutTotalEligibleVotes + 1) / 2 + 1;

    /// <summary>
    /// Resolves the vote the moment the outcome is settled, so players who never
    /// click can't hold the team for the rest of the timer.
    /// </summary>
    internal bool TryResolveTimeoutVoteEarly()
    {
        if (!isTimeoutVoteInProgress) return false;

        var required = TimeoutRequiredVotes();
        // Every voter who hasn't answered could still say yes; only no votes cap it.
        var maxReachableYes = timeoutTotalEligibleVotes + 1 - timeoutVoteNoCount;
        if (timeoutVoteYesCount < required && maxReachableYes >= required) return false;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("TryResolveTimeoutVoteEarly: settled at {Yes} yes / {No} no (required {Required}, max reachable {Max})",
                timeoutVoteYesCount, timeoutVoteNoCount, required, maxReachableYes);
        }

        timeoutVoteTimer?.Cancel();
        TimeoutVoteResult(timeoutVoteTeam);
        return true;
    }

    /// <summary>
    /// Handles a player's vote in a timeout voting process.
    /// </summary>
    internal void HandleTimeoutVote(IPlayer player, string choice)
    {
        if (!IsPlayerValid(player))
        {
            logger.LogWarning("HandleTimeoutVote: ignoring vote from invalid/disconnected player {Slot}.", player?.Slot);
            return;
        }

        var voterSteamId = SafeSteamId(player);
        if (voterSteamId != 0 && !timeoutVoters.Add(voterSteamId))
        {
            logger.LogWarning("HandleTimeoutVote: {Name} already voted, ignoring duplicate.", player.Name);
            return;
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleTimeoutVote: Player {Name} voted {Choice}. Current votes before: {Yes} yes, {No} no out of {Total}",
                player.Name, choice, timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes);
        }

        var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("HandleTimeoutVote: PlayerPawn is null for player {PlayerName}", player.Name);
            return;
        }

        if (string.Equals(choice, "Yes", StringComparison.OrdinalIgnoreCase))
        {
            timeoutVoteYesCount++;
        }
        else if (string.Equals(choice, "No", StringComparison.OrdinalIgnoreCase))
        {
            timeoutVoteNoCount++;
        }

        {
            ulong voterSid; try { voterSid = player.SteamID; } catch { voterSid = 0; }
            var voteYes = string.Equals(choice, "Yes", StringComparison.OrdinalIgnoreCase);
            if (voterSid != 0)
                mixScrimsService.RaiseTimeoutVoteCast(voterSid, voteYes, timeoutVoteTeam);
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleTimeoutVote: After vote - {Yes} yes, {No} no out of {Total}. Total voted: {TotalVoted}",
                timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes, timeoutVoteYesCount + timeoutVoteNoCount);
        }

        PrintMessageToTeam(timeoutVoteTeam, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, TimeoutRequiredVotes()]);

        TryResolveTimeoutVoteEarly();

        CloseMenuForPlayer(player);
    }

    /// <summary>
    /// Processes the result of a timeout vote for the specified team.
    /// Prints totals to team and broadcasts the final result to all players.
    /// </summary>
    internal void TimeoutVoteResult(Team team)
    {
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("TimeoutVoteResult: Called for team {Team}. isTimeoutVoteInProgress: {InProgress}. Votes: {Yes} yes, {No} no out of {Total}",
                team, isTimeoutVoteInProgress, timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes);
        }

        // Prevent duplicate processing
        if (!isTimeoutVoteInProgress)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogWarning("TimeoutVoteResult: No vote in progress, ignoring duplicate call");
            }
            return;
        }

        isTimeoutVoteInProgress = false;
        int requiredVotes = TimeoutRequiredVotes();

        var players = GetPlayersInTeam(team);
        foreach (var player in players)
        {
            if (!IsPlayerValid(player) || IsBot(player))
                continue;

            var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
            if (currentMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
            }
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.total_team", timeoutVoteYesCount, timeoutVoteNoCount, requiredVotes]);

        bool votePassed = timeoutVoteYesCount >= requiredVotes;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("TimeoutVoteResult: Vote {Result} for team {Team}. {Yes} >= {Required}? {Passed}",
                votePassed ? "PASSED" : "FAILED", team, timeoutVoteYesCount, requiredVotes, votePassed);
        }

        mixScrimsService.RaiseTimeoutVoteResult(team, votePassed);

        if (team == Team.CT)
        {
            if (votePassed)
            {
                timeoutPending = TimeoutPending.CT;
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("TimeoutVoteResult: CT vote passed. isFreezeTime: {IsFreezeTime}", isFreezeTime);
                }
                if (isFreezeTime)
                {
                    StartTimeout(Team.CT);
                    return;
                }
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.ct"]);
            }
            else
            {
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("TimeoutVoteResult: CT vote failed - not enough votes");
                }
                PrintMessageToTeam(Team.CT, Core.Localizer["announcement.timeout.not_enough_votes"]);
            }
        }
        if (team == Team.T)
        {
            if (votePassed)
            {
                timeoutPending = TimeoutPending.T;
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("TimeoutVoteResult: T vote passed. isFreezeTime: {IsFreezeTime}", isFreezeTime);
                }
                if (isFreezeTime)
                {
                    StartTimeout(Team.T);
                    return;
                }
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.t"]);
            }
            else
            {
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("TimeoutVoteResult: T vote failed - not enough votes");
                }
                PrintMessageToTeam(Team.T, Core.Localizer["announcement.timeout.not_enough_votes"]);
            }
        }
    }

    /// <summary>
    /// Broadcasts announcements to all players about the remaining timeout time at specific intervals.
    /// </summary>
    internal void BroadcastRemainingTimeoutTime(Team team)
    {
        int remainingSeconds = cfg.TimeoutDurationSeconds;
        activeTimeoutRemainingSeconds = remainingSeconds;
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("BroadcastRemainingTimeoutTime: Broadcasting CenterHTML for remaining timeout time: {Time}, team: {Team}", remainingSeconds, team);
        }

        var locKey = team == Team.CT ? "info.center.timeout_remaining.ct" : "info.center.timeout_remaining.t";
        var timer = Core.Scheduler.RepeatBySeconds(1, () =>
        {
            // Fire TimeoutTick every second WHILE the timeout is still active. Guard against
            // stray ticks after EndTimeout has cleared state (timer may fire once more before
            // the CancelAfter kicks in).
            if (mixScrimsService.GetCurrentMatchState() == MatchState.Timeout && isTimeoutActive)
            {
                activeTimeoutRemainingSeconds = remainingSeconds;
                mixScrimsService.RaiseTimeoutTick(remainingSeconds);
            }
            if (!suppressBuiltInCenterHtml)
                Core.PlayerManager.SendCenterHTML(Core.Localizer[locKey, remainingSeconds], 1000);
            remainingSeconds--;
        });
        timer.CancelAfter(cfg.TimeoutDurationSeconds * 1000);
    }
}
