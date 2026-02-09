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
    internal CancellationTokenSource? timeoutVoteTimer = null;

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
        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.timeout.ended"]);
        isTimeoutActive = false;
        timeoutPending = TimeoutPending.None;

        // Check if there's a queued timeout
        if (timeoutQueue.Count > 0)
        {
            var nextTeam = timeoutQueue.Dequeue();
            
            // If we're in freeze time, start immediately
            if (isFreezeTime)
            {
                StartTimeout(nextTeam);
            }
            else
            {
                // Otherwise, set as pending for next freeze time
                timeoutPending = nextTeam == Team.CT ? TimeoutPending.CT : TimeoutPending.T;
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
            mixScrimsService.SetMatchState(MatchState.Match);
            UnpauseMatch();
        }
    }

    /// <summary>
    /// Initiates a timeout vote for the specified team.
    /// </summary>
    internal void StartTimeoutVote(IPlayer caller, Team team)
    {
        // reset tallies
        timeoutVoteYesCount = 0;
        timeoutVoteNoCount = 0;
        timeoutVoteTimer?.Cancel();
        timeoutVoteTimer = null;

        var players = GetPlayersInTeam(team);
        if (players.Count == 0)
        {
            logger.LogWarning("StartTimeoutVote: Vote timeout was called for {Team} team, but there are no players", team);
            return;
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
        foreach (var player in players)
        {
            if (IsBot(player))
            {
                timeoutVoteYesCount++;
                continue;
            }

            if (IsPlayerValid(player))
            {
                Core.MenusAPI.OpenMenuForPlayer(player, menu);
            }
        }

        var totalEligibleVotes = Math.Max(0, players.Count - 1);
        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, totalEligibleVotes]);

        timeoutVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => TimeoutVoteResult(team));
    }

    /// <summary>
    /// Handles a player's vote in a timeout voting process.
    /// </summary>
    internal void HandleTimeoutVote(IPlayer player, string choice)
    {
        if (!IsPlayerValid(player))
            return;

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
        var teamPlayers = GetPlayersInTeam(team);
        int totalEligibleVotes = Math.Max(0, teamPlayers.Count - 1);

        PrintMessageToTeam(team, Core.Localizer["announcement.timeout.vote.progress", timeoutVoteYesCount, timeoutVoteNoCount, totalEligibleVotes]);

        if (timeoutVoteYesCount + timeoutVoteNoCount >= totalEligibleVotes)
        {
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
        int requiredVotes = Math.Max(0, GetPlayersInTeam(team).Count - 1);

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

        if (team == Team.CT)
        {
            if (timeoutVoteYesCount >= requiredVotes)
            {
                timeoutPending = TimeoutPending.CT;
                if (isFreezeTime)
                {
                    StartTimeout(Team.CT);
                    return;
                }
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.ct"]);
            }
            else
            {
                PrintMessageToTeam(Team.CT, Core.Localizer["announcement.timeout.not_enough_votes"]);
            }
        }
        if (team == Team.T)
        {
            if (timeoutVoteYesCount >= requiredVotes)
            {
                timeoutPending = TimeoutPending.T;
                if (isFreezeTime)
                {
                    StartTimeout(Team.T);
                    return;
                }
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.pending.t"]);
            }
            else
            {
                PrintMessageToTeam(Team.T, Core.Localizer["announcement.timeout.not_enough_votes"]);
            }
        }
    }

    /// <summary>
    /// Broadcasts announcements to all players about the remaining timeout time at specific intervals.
    /// </summary>
    internal void BroadcastRemainingTimeoutTime()
    {
        if (cfg.TimeoutDurationSeconds == 120)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 105]));
            Core.Scheduler.DelayBySeconds(30, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 90]));
            Core.Scheduler.DelayBySeconds(45, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 75]));
            Core.Scheduler.DelayBySeconds(60, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 60]));
            Core.Scheduler.DelayBySeconds(75, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 45]));
            Core.Scheduler.DelayBySeconds(90, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 30]));
            Core.Scheduler.DelayBySeconds(105, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 15]));
        }

        if (cfg.TimeoutDurationSeconds == 60)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 45]));
            Core.Scheduler.DelayBySeconds(30, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 30]));
            Core.Scheduler.DelayBySeconds(45, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 15]));
        }

        if (cfg.TimeoutDurationSeconds == 30)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeout.remaining_time", 45]));
        }
    }
}
