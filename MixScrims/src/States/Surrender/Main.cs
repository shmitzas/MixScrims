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
    // Who has already voted in the current surrender vote. See timeoutVoters for why.
    internal HashSet<ulong> surrenderVoters = new();

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
        // Seed with the caller so their implicit YES can't be cast a second time.
        surrenderVoters.Clear();
        { ulong seedSid = SafeSteamId(caller); if (seedSid != 0) surrenderVoters.Add(seedSid); }

        mixScrimsService.RaiseSurrenderVoteStarted(team);
        // Fire the caller's implicit YES so consumers don't have to seed from tally state.
        {
            ulong callerSid; try { callerSid = caller.SteamID; } catch { callerSid = 0; }
            if (callerSid != 0)
                mixScrimsService.RaiseSurrenderVoteCast(callerSid, true, team);
        }

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

        players.RemoveAll(p => p.SteamID == caller.SteamID);
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
                if (!suppressBuiltInMenus)
                    Core.MenusAPI.OpenMenuForPlayer(player, menu);
                menuOpenCount++;
            }
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: Opened menu for {MenuCount} players, {BotCount} bots auto-voted yes. Current votes: {Yes} yes, {No} no out of {Total}",
                menuOpenCount, botCount, surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes);
        }

        PrintMessageToTeam(team, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, SurrenderRequiredVotes()]);

        // Bots auto-vote yes, so the vote can already be settled before it opens.
        if (TryResolveSurrenderVoteEarly()) return;

        surrenderVoteTimer = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, () => SurrenderVoteResult(team));
        Core.Scheduler.StopOnMapChange(surrenderVoteTimer);

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("StartSurrenderVote: Vote timer scheduled for {Seconds} seconds", cfg.DefaultVoteTimeSeconds);
        }
    }

    /// <summary>
    /// Majority of the whole team. The caller's implicit yes is seeded into the yes
    /// count but excluded from the eligible count, so the team is eligible + 1.
    /// </summary>
    internal int SurrenderRequiredVotes() => (surrenderTotalEligibleVotes + 1) / 2 + 1;

    /// <summary>
    /// Resolves the vote the moment the outcome is settled, so players who never
    /// click can't hold the team for the rest of the timer.
    /// </summary>
    internal bool TryResolveSurrenderVoteEarly()
    {
        if (!isSurrenderVoteInProgress) return false;

        var required = SurrenderRequiredVotes();
        // Every voter who hasn't answered could still say yes; only no votes cap it.
        var maxReachableYes = surrenderTotalEligibleVotes + 1 - surrenderVoteNoCount;
        if (surrenderVoteYesCount < required && maxReachableYes >= required) return false;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("TryResolveSurrenderVoteEarly: settled at {Yes} yes / {No} no (required {Required}, max reachable {Max})",
                surrenderVoteYesCount, surrenderVoteNoCount, required, maxReachableYes);
        }

        surrenderVoteTimer?.Cancel();
        SurrenderVoteResult(surrenderVoteTeam);
        return true;
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

        var voterSteamId = SafeSteamId(player);
        if (voterSteamId != 0 && !surrenderVoters.Add(voterSteamId))
        {
            logger.LogWarning("HandleSurrenderVote: {Name} already voted, ignoring duplicate.", player.Name);
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

        {
            ulong voterSid; try { voterSid = player.SteamID; } catch { voterSid = 0; }
            var voteYes = string.Equals(choice, "Yes", StringComparison.OrdinalIgnoreCase);
            if (voterSid != 0)
                mixScrimsService.RaiseSurrenderVoteCast(voterSid, voteYes, surrenderVoteTeam);
        }

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleSurrenderVote: After vote - {Yes} yes, {No} no out of {Total}. Total voted: {TotalVoted}",
                surrenderVoteYesCount, surrenderVoteNoCount, surrenderTotalEligibleVotes, surrenderVoteYesCount + surrenderVoteNoCount);
        }

        PrintMessageToTeam(surrenderVoteTeam, Core.Localizer["announcement.surrender.vote.progress", surrenderVoteYesCount, surrenderVoteNoCount, SurrenderRequiredVotes()]);

        TryResolveSurrenderVoteEarly();

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
        int requiredVotes = SurrenderRequiredVotes();

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

        mixScrimsService.RaiseSurrenderVoteResult(team, votePassed);

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
            if (!suppressBuiltInCenterHtml)
                Core.PlayerManager.SendCenterHTMLAsync(Core.Localizer["announcement.surrender.success.ct", matchResetDelay], matchResetDelay * 1000);
            logger.LogInformation("SurrenderVoteResult: CT voted for surrender, terminating round");
        }
        else if (team == Team.T)
        {
            if (!suppressBuiltInCenterHtml)
                Core.PlayerManager.SendCenterHTMLAsync(Core.Localizer["announcement.surrender.success.t", matchResetDelay], matchResetDelay * 1000);
            logger.LogInformation("SurrenderVoteResult: T voted for surrender, terminating round");
        }

        // Trigger match canceled event
        if (!ForceSurrenderMatchEnd(team))
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

    /// <summary>
    /// Ends the match down CS2's own surrender path, so the win panel reads
    /// "CTs surrender" / "Terrorists surrender" and the round is credited to the
    /// opposing team rather than the match merely being paused and reset.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the round could not be terminated — the caller must fall
    /// back to pausing, or the surrendering team would just keep playing.
    /// </returns>
    internal bool ForceSurrenderMatchEnd(Team team)
    {
        const float roundEndDelay = 1.0f;

        if (team != Team.CT && team != Team.T)
        {
            logger.LogWarning("ForceSurrenderMatchEnd: unsupported team {Team} - skipping native match end.", team);
            return false;
        }

        var reason = team == Team.CT ? RoundEndReason.CTsSurrender : RoundEndReason.TerroristsSurrender;
        if (!RestartRoundManually("Surrender", reason, roundEndDelay))
            return false;

        // TerminateRound only settles the round; intermission is what ends the match.
        var intermissionToken = Core.Scheduler.DelayBySeconds(roundEndDelay + 1.0f, () =>
        {
            try
            {
                Core.Game.GoToIntermission();
                logger.LogInformation("ForceSurrenderMatchEnd: {Team} surrendered - match sent to intermission.", team);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ForceSurrenderMatchEnd: GoToIntermission failed after {Team} surrendered.", team);
            }
        });
        Core.Scheduler.StopOnMapChange(intermissionToken);
        return true;
    }
}
