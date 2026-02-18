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

    internal bool isFreezeTime = false;

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

        isTimeoutActive = true;
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
        BroadcastRemainingTimeoutTime();
        Core.Scheduler.DelayBySeconds(cfg.TimeoutDurationSeconds, EndTimeout);
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

        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.timeout.ended"]);
        isTimeoutActive = false;
        timeoutPending = TimeoutPending.None;

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
                caller.Controller?.PlayerName, team, isTimeoutVoteInProgress);
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
        timeoutVoteTimer?.Cancel();
        timeoutVoteTimer = null;

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

        players.Remove(caller);
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
                Core.MenusAPI.OpenMenuForPlayer(player, menu);
                menuOpenCount++;
            }
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Opened menu for {MenuCount} players, {BotCount} bots auto-voted yes. Current votes: {Yes} yes, {No} no out of {Total}",
                menuOpenCount, botCount, timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes);
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes]);

        timeoutVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => TimeoutVoteResult(team));

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartTimeoutVote: Vote timer scheduled for {Seconds} seconds", cfg.DefaultVoteTimeSeconds);
        }
    }

    /// <summary>
    /// Handles a player's vote in a timeout voting process.
    /// </summary>
    internal void HandleTimeoutVote(IPlayer player, string choice)
    {
        if (!IsPlayerValid(player))
            return;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleTimeoutVote: Player {Name} voted {Choice}. Current votes before: {Yes} yes, {No} no out of {Total}",
                player.Controller?.PlayerName, choice, timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes);
        }

        var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("HandleTimeoutVote: PlayerPawn is null for player {PlayerName}", player.Controller?.PlayerName);
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

        var team = (Team)player.PlayerPawn.TeamNum;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleTimeoutVote: After vote - {Yes} yes, {No} no out of {Total}. Total voted: {TotalVoted}",
                timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes, timeoutVoteYesCount + timeoutVoteNoCount);
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, timeoutTotalEligibleVotes]);

        // Check if all eligible players have voted
        if (timeoutVoteYesCount + timeoutVoteNoCount >= timeoutTotalEligibleVotes)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("HandleTimeoutVote: All eligible votes received ({Voted} >= {Total}), cancelling timer and processing result",
                    timeoutVoteYesCount + timeoutVoteNoCount, timeoutTotalEligibleVotes);
            }
            timeoutVoteTimer?.Cancel();
            TimeoutVoteResult(team);
        }

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
        int requiredVotes = timeoutTotalEligibleVotes;

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
    internal void BroadcastRemainingTimeoutTime()
    {
        int remainingSeconds = cfg.TimeoutDurationSeconds;
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("BroadcastRemainingTimeoutTime: Broadcasting CenterHTML for remaining timeout time: {Time}", remainingSeconds);
        }

        if (timeoutPending == TimeoutPending.CT)
        {
            var timer = Core.Scheduler.RepeatBySeconds(1, () =>
            {
                Core.PlayerManager.SendCenterHTML(Core.Localizer["info.center.timeout_remaining.ct", remainingSeconds], 1000);
                remainingSeconds--;
            });
            timer.CancelAfter(cfg.TimeoutDurationSeconds * 1000);
        }

        if (timeoutPending == TimeoutPending.T)
        {
            var timer = Core.Scheduler.RepeatBySeconds(1, () =>
            {
                Core.PlayerManager.SendCenterHTML(Core.Localizer["info.center.timeout_remaining.t", remainingSeconds], 1000);
                remainingSeconds--;
            });
            timer.CancelAfter(cfg.TimeoutDurationSeconds * 1000);
        }       
    }
}
