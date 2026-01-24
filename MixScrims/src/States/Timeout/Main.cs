using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    private int timeoutCountCt { get; set; } = 3;
    private int timeoutCountT { get; set; } = 3;

    private enum TimeoutPending
    {
        None,
        CT,
        T
    }

    private TimeoutPending timeoutPending = TimeoutPending.None;
    private Queue<Team> timeoutQueue = new Queue<Team>();
    private bool isTimeoutActive = false;
    private int timeoutVoteYesCount = 0;
    private int timeoutVoteNoCount = 0;
    private CancellationTokenSource? timeoutVoteTimer = null;

    private bool isFreezeTime = false;

    /// <summary>
    /// Starts a timeout for the specified team
    /// </summary>
    private void StartTimeout(Team team)
    {
        // If a timeout is already active, queue this one
        if (isTimeoutActive)
        {
            if (!timeoutQueue.Contains(team))
            {
                timeoutQueue.Enqueue(team);
                if (team == Team.CT)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutQueuedCt"]);
                }
                else if (team == Team.T)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutQueuedT"]);
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
            PrintMessageToAllPlayers(Core.Localizer["stateChanged.timeoutStartedCt"]);
            PrintMessageToTeam(Team.CT, Core.Localizer["timeout.remainingTimeouts", timeoutCountCt, cfg.Timeouts]);
        }

        if (team == Team.T)
        {
            timeoutCountT--;
            PrintMessageToAllPlayers(Core.Localizer["stateChanged.timeoutStartedT"]);
            PrintMessageToTeam(Team.T, Core.Localizer["timeout.remainingTimeouts", timeoutCountT, cfg.Timeouts]);
        }
        BroadcastRemainingTimeoutTime();
        Core.Scheduler.DelayBySeconds(cfg.TimeoutDurationSeconds, EndTimeout);
    }

    /// <summary>
    /// Ends timeout and starts the next one in queue if available
    /// </summary>
    private void EndTimeout()
    {
        PrintMessageToAllPlayers(Core.Localizer["stateChanged.timeoutEnded"]);
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
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutPendingCt"]);
                }
                else if (nextTeam == Team.T)
                {
                    PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutPendingT"]);
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
    private void StartTimeoutVote(IPlayer caller, Team team)
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
            .Design.SetMenuTitle(Core.Localizer["menu.timeoutVote"])
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
        PrintMessageToTeam(team, Core.Localizer["announcement.timeoutVoteProgress", timeoutVoteYesCount, timeoutVoteNoCount, totalEligibleVotes]);

        timeoutVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => TimeoutVoteResult(team));
    }

    /// <summary>
    /// Handles a player's vote in a timeout voting process.
    /// </summary>
    private void HandleTimeoutVote(IPlayer player, string choice)
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

        PrintMessageToTeam(team, Core.Localizer["announcement.timeoutVoteProgress", timeoutVoteYesCount, timeoutVoteNoCount, totalEligibleVotes]);

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
    private void TimeoutVoteResult(Team team)
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

        PrintMessageToTeam(team, Core.Localizer["announcement.timeoutVoteTotalTeam", timeoutVoteYesCount, timeoutVoteNoCount, requiredVotes]);

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
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutPendingCt"]);
            }
            else
            {
                PrintMessageToTeam(Team.CT, Core.Localizer["announcement.timeoutNotEnoughVotes"]);
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
                PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutPendingT"]);
            }
            else
            {
                PrintMessageToTeam(Team.T, Core.Localizer["announcement.timeoutNotEnoughVotes"]);
            }
        }
    }

    /// <summary>
    /// Broadcasts announcements to all players about the remaining timeout time at specific intervals.
    /// </summary>
    private void BroadcastRemainingTimeoutTime()
    {
        if (cfg.TimeoutDurationSeconds == 120)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 105]));
            Core.Scheduler.DelayBySeconds(30, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 90]));
            Core.Scheduler.DelayBySeconds(45, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 75]));
            Core.Scheduler.DelayBySeconds(60, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 60]));
            Core.Scheduler.DelayBySeconds(75, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 45]));
            Core.Scheduler.DelayBySeconds(90, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 30]));
            Core.Scheduler.DelayBySeconds(105, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 15]));
        }

        if (cfg.TimeoutDurationSeconds == 60)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 45]));
            Core.Scheduler.DelayBySeconds(30, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 30]));
            Core.Scheduler.DelayBySeconds(45, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 15]));
        }

        if (cfg.TimeoutDurationSeconds == 30)
        {
            Core.Scheduler.DelayBySeconds(15, () => PrintMessageToAllPlayers(Core.Localizer["announcement.timeoutRemainingTime", 45]));
        }
    }
}
