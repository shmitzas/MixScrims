using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using System.Numerics;

namespace MixScrims;

public partial class MixScrims
{
    internal int surrenderVoteYesCount = 0;
    internal int surrenderVoteNoCount = 0;
    internal int surrenderTotalEligibleVotes = 0;
    internal CancellationTokenSource? surrenderVoteTimer = null;
    internal Team surrenderVoteTeam = Team.None;
    internal bool isSurrenderVoteInProgress = false;

    /// <summary>
    /// Initiates a surrender vote for the specified team.
    /// </summary>
    internal void StartSurrenderVote(IPlayer caller, Team team)
    {
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: Called by {Caller} for team {Team}. isSurrenderVoteInProgress: {InProgress}",
                caller.Name, team, isSurrenderVoteInProgress);
        }

        // Prevent duplicate vote processing
        if (isSurrenderVoteInProgress)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogWarning("StartSurrenderVote: Vote already in progress, ignoring duplicate call");
            }
            return;
        }

        // reset tallies
        surrenderVoteYesCount = 1; // Caller's automatic yes vote
        surrenderVoteNoCount = 0;
        surrenderTotalEligibleVotes = 0;
        isSurrenderVoteInProgress = true;
        surrenderVoteTimer?.Cancel();
        surrenderVoteTimer = null;
        surrenderVoteTeam = team;

        var players = GetPlayersInTeam(team);
        if (players.Count == 0)
        {
            logger.LogWarning("StartSurrenderVote: Surrender vote was called for {Team} team, but there are no players", team);
            isSurrenderVoteInProgress = false;
            return;
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: Total players in team: {Count}. Caller will be removed from voting list", players.Count);
        }

        // If team has 2 or fewer players, auto-pass the vote without showing menus
        if (players.Count <= 2)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("StartSurrenderVote: Team has {Count} players, auto-passing vote", players.Count);
            }
            isSurrenderVoteInProgress = false;
            Surrender(team);
            return;
        }

        players.RemoveAll(p => p.PlayerID == caller.PlayerID);
        surrenderTotalEligibleVotes = players.Count; // Store for consistent use across methods

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: After removing caller, {Count} players need to vote", surrenderTotalEligibleVotes);
        }

        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.surrender_vote"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var yesBtn = new ButtonMenuOption("Yes");
        yesBtn.Click += async (sender, args) =>
        {
            HandleSurrenderVote(args.Player, "Yes");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(yesBtn);

        var noBtn = new ButtonMenuOption("No");
        noBtn.Click += async (sender, args) =>
        {
            HandleSurrenderVote(args.Player, "No");
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
                surrenderVoteYesCount++;
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
            logger.LogInformation("StartSurrenderVote: Opened menu for {MenuCount} players, {BotCount} bots auto-voted yes. Current votes: {Yes} yes, {No} no out of {Total}",
                menuOpenCount, botCount, surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes);
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes]);

        surrenderVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => SurrenderVoteResult(team));
        Core.Scheduler.StopOnMapChange(surrenderVoteTimer);

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: Vote timer scheduled for {Seconds} seconds", cfg.DefaultVoteTimeSeconds);
        }
    }

    /// <summary>
    /// Handles a player's vote in a surrender voting process.
    /// </summary>
    internal void HandleSurrenderVote(IPlayer player, string choice)
    {
        if (!IsPlayerValid(player))
        {
            logger.LogWarning("HandleSurrenderVote: ignoring vote from invalid/disconnected player {Slot}.", player?.Slot);
            return;
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleSurrenderVote: Player {Name} voted {Choice}. Current votes before: {Yes} yes, {No} no out of {Total}",
                player.Name, choice, surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes);
        }

        var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("HandleSurrenderVote: PlayerPawn is null for player {PlayerName}", player.Name);
            return;
        }

        if (string.Equals(choice, "Yes", StringComparison.OrdinalIgnoreCase))
        {
            surrenderVoteYesCount++;
        }
        else if (string.Equals(choice, "No", StringComparison.OrdinalIgnoreCase))
        {
            surrenderVoteNoCount++;
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleSurrenderVote: After vote - {Yes} yes, {No} no out of {Total}. Total voted: {TotalVoted}",
                surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes, surrenderVoteYesCount + surrenderVoteNoCount);
        }

        PrintMessageToTeam(surrenderVoteTeam, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes]);

        // Check if all eligible players have voted
        if (surrenderVoteYesCount + surrenderVoteNoCount >= surrenderTotalEligibleVotes)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("HandleSurrenderVote: All eligible votes received ({Voted} >= {Total}), cancelling timer and processing result",
                    surrenderVoteYesCount + surrenderVoteNoCount, surrenderTotalEligibleVotes);
            }
            surrenderVoteTimer?.Cancel();
            SurrenderVoteResult(surrenderVoteTeam);
        }

        CloseMenuForPlayer(player);
    }

    /// <summary>
    /// Processes the result of a surrender vote for the specified team.
    /// Prints totals to team and broadcasts the final result to all players.
    /// </summary>
    internal void SurrenderVoteResult(Team team)
    {
        if (cfg.DetailedLogging)
        {
            logger.LogInformation("SurrenderVoteResult: Called for team {Team}. isSurrenderVoteInProgress: {InProgress}. Votes: {Yes} yes, {No} no out of {Total}",
                team, isSurrenderVoteInProgress, surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes);
        }

        // Prevent duplicate processing
        if (!isSurrenderVoteInProgress)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogWarning("SurrenderVoteResult: No vote in progress, ignoring duplicate call");
            }
            return;
        }

        isSurrenderVoteInProgress = false;
        int requiredVotes = surrenderTotalEligibleVotes;

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

        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.total_team", surrenderVoteYesCount, surrenderVoteNoCount, requiredVotes]);

        bool votePassed = surrenderVoteYesCount >= requiredVotes;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("SurrenderVoteResult: Vote {Result} for team {Team}. {Yes} >= {Required}? {Passed}",
                votePassed ? "PASSED" : "FAILED", team, surrenderVoteYesCount, requiredVotes, votePassed);
        }

        if (votePassed)
        {
            Surrender(team);
        }
        else
        {
            // Vote failed
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("SurrenderVoteResult: {Team} vote failed - not enough votes", team);
            }
            if (team == Team.CT)
            {
                PrintMessageToTeam(Team.CT, Core.Localizer["announcement.surrender.failed"]);
            }
            else if (team == Team.T)
            {
                PrintMessageToTeam(Team.T, Core.Localizer["announcement.surrender.failed"]);
            }
        }
    }

    internal void Surrender(Team team)
    {
        int matchResetDelay = 10;

        if (team == Team.CT)
        {
            Core.PlayerManager.SendCenterHTMLAsync(Core.Localizer["announcement.surrender.success.ct", matchResetDelay], matchResetDelay * 1000);
            logger.LogInformation("SurrenderVoteResult: CT voted for surrender, terminating round");
        }
        else if (team == Team.T)
        {
            Core.PlayerManager.SendCenterHTMLAsync(Core.Localizer["announcement.surrender.success.t", matchResetDelay], matchResetDelay * 1000);
            logger.LogInformation("SurrenderVoteResult: T voted for surrender, terminating round");
        }

        // Trigger match canceled event
        PauseMatch();

        // Schedule reset
        var resetToken = Core.Scheduler.DelayBySeconds(matchResetDelay - 5, () =>
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Match surrendered by team {Team}, resetting plugin state.", team);
            ResetPluginState();
        });
        Core.Scheduler.StopOnMapChange(resetToken);
    }
}
