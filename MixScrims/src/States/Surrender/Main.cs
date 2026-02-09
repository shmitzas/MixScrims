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
    internal CancellationTokenSource? surrenderVoteTimer = null;
    internal Team surrenderVoteTeam = Team.None;

    /// <summary>
    /// Initiates a surrender vote for the specified team.
    /// </summary>
    internal void StartSurrenderVote(IPlayer caller, Team team)
    {
        // reset tallies
        surrenderVoteYesCount = 0;
        surrenderVoteNoCount = 0;
        surrenderVoteTimer?.Cancel();
        surrenderVoteTimer = null;
        surrenderVoteTeam = team;

        var players = GetPlayersInTeam(team);
        if (players.Count == 0)
        {
            logger.LogWarning("StartSurrenderVote: Surrender vote was called for {Team} team, but there are no players", team);
            return;
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
        foreach (var player in players)
        {
            if (IsBot(player))
            {
                surrenderVoteYesCount++;
                continue;
            }

            if (IsPlayerValid(player))
            {
                Core.MenusAPI.OpenMenuForPlayer(player, menu);
            }
        }

        var totalEligibleVotes = Math.Max(0, players.Count - 1);
        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, totalEligibleVotes]);

        surrenderVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => SurrenderVoteResult(team));
    }

    /// <summary>
    /// Handles a player's vote in a surrender voting process.
    /// </summary>
    internal void HandleSurrenderVote(IPlayer player, string choice)
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
            logger.LogError("HandleSurrenderVote: PlayerPawn is null for player {PlayerName}", player.Controller?.PlayerName);
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

        var team = (Team)player.PlayerPawn.TeamNum;
        var teamPlayers = GetPlayersInTeam(team);
        int totalEligibleVotes = Math.Max(0, teamPlayers.Count - 1);

        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, totalEligibleVotes]);

        if (surrenderVoteYesCount + surrenderVoteNoCount >= totalEligibleVotes)
        {
            surrenderVoteTimer?.Cancel();
            SurrenderVoteResult(team);
        }

        CloseMenuForPlayer(player);
    }

    /// <summary>
    /// Processes the result of a surrender vote for the specified team.
    /// Prints totals to team and broadcasts the final result to all players.
    /// </summary>
    internal void SurrenderVoteResult(Team team)
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

        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.total_team", surrenderVoteYesCount, surrenderVoteNoCount, requiredVotes]);

        if (surrenderVoteYesCount >= requiredVotes)
        {
            Surrender(team);
        }
        else
        {
            // Vote failed
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
        Core.Scheduler.DelayBySeconds(matchResetDelay - 5, () =>
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Match surrendered by team {Team}, resetting plugin state.", team);
            ResetPluginState();
        });
    }
}
