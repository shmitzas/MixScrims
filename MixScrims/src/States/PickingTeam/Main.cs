using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Players;
using System.Numerics;

namespace MixScrims;

public partial class MixScrims
{
    internal List<IPlayer> pickedCtPlayers = [];
    internal List<IPlayer> pickedTPlayers = [];
    internal IPlayer? captainCt { get; set; }
    internal IPlayer? captainT { get; set; }

    /// <summary>
    /// Initiates the team-picking phase of the match, assigning captains to teams and prompting the first captain to
    /// pick a player.
    /// </summary>
    internal void StartTeamPickingPhase()
    {
        StopPreMatchAnnouncementTimers();

        RemoveReadyClanTagsFromAllPlayers();

        if (!cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Current captains - CT: {CT}, T: {T}", captainCt?.Name ?? "null", captainT?.Name ?? "null");

            PickCaptains();

            if (captainCt == null || captainT == null)
            {
                logger.LogError("StartTeamPickingPhase: One or both captains are null.");
                logger.LogError("captainCt: {Name}", captainCt != null ? captainCt.Name ?? "(no name)" : "null");
                logger.LogError("captainT: {Name}", captainT != null ? captainT.Name ?? "(no name)" : "null");
                logger.LogError("StartTeamPickingPhase: Valid players in the server: {Count}", GetPlayers().Count);
                logger.LogError("StartTeamPickingPhase: Aborting team picking phase.");
                PrintMessageToAllPlayers(Core.Localizer["error.captain.selection_failed"]);
                ResetPluginState();
                return;
            }

            if (cfg.DetailedLogging)
            {
                logger.LogInformation("StartTeamPickingPhase: Before validation - pickedCt: {CtCount}, pickedT: {TCount}", pickedCtPlayers.Count, pickedTPlayers.Count);
                logger.LogInformation("StartTeamPickingPhase: CT captain in picked list: {InList}", pickedCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID));
                logger.LogInformation("StartTeamPickingPhase: T captain in picked list: {InList}", pickedTPlayers.Any(p => p.PlayerID == captainT.PlayerID));
            }

            // Ensure captains are in picked lists (handles captains set during Warmup state)
            if (captainCt != null && IsPlayerValid(captainCt))
            {
                if (!pickedCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("StartTeamPickingPhase: Adding CT Captain {PlayerName} to pickedCtPlayers.", captainCt.Controller.PlayerName);
                    pickedCtPlayers.Add(captainCt);
                }
            }

            if (captainT != null && IsPlayerValid(captainT))
            {
                if (!pickedTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("StartTeamPickingPhase: Adding T Captain {PlayerName} to pickedTPlayers.", captainT.Controller.PlayerName);
                    pickedTPlayers.Add(captainT);
                }
            }

            if (cfg.DetailedLogging)
            {
                logger.LogInformation("StartTeamPickingPhase: After validation - pickedCt: {CtCount}, pickedT: {TCount}", pickedCtPlayers.Count, pickedTPlayers.Count);
            }
        }

        if (cfg.SkipTeamPicking)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Team picking is disabled in configuration.");
            SkipTeamPickingPhase();
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Captains is disabled in configuration, auto-assigning teams based on current positions.");
            SkipTeamPickingPhase();
            return;
        }

        mixScrimsService.SetMatchState(MatchState.PickingTeam);        

        PauseMatch();
        if (Core.Engine is { } pickEngine)
            pickEngine.ExecuteCommand("exec mixscrims/teampick.cfg");
        else
            logger.LogWarning("StartTeamPickingPhase: Core.Engine unavailable; skipping teampick.cfg.");

        MovePlayersToDesignatedTeamsPrePick();

        SetTeamName(Team.CT, captainCt == null ? null : captainCt.Controller.PlayerName);
        SetTeamName(Team.T, captainT == null ? null :  captainT.Controller.PlayerName);

        int teamStarting = Random.Shared.Next(2, 4);
        if (teamStarting == 3)
        {
            PromptCaptainToPickPlayer(captainCt, Team.CT);
            return;
        }
        if (teamStarting == 2)
        {
            PromptCaptainToPickPlayer(captainT, Team.T);
            return;
        }
    }

    /// <summary>
    /// Skips the team picking phase and automatically assigns players to teams based on their current state and
    /// configuration settings.
    /// </summary>
    internal void SkipTeamPickingPhase()
    {
        mixScrimsService.SetMatchState(MatchState.PickingTeam);

        if (Core.Engine is { } skipPickEngine)
            skipPickEngine.ExecuteCommand("exec mixscrims/teampick.cfg");
        else
            logger.LogWarning("SkipTeamPickingPhase: Core.Engine unavailable; skipping teampick.cfg.");
        PauseMatch();

        var players = GetPlayingPlayers();

        if (!cfg.DisableCaptains)
        {
            if (captainCt != null && captainCt.IsValid)
            {
                players.RemoveAll(p => p.PlayerID == captainCt.PlayerID);
                playingCtPlayers.Add(captainCt);
            }

            if (captainT != null && captainT.IsValid)
            {
                players.RemoveAll(p => p.PlayerID == captainT.PlayerID);
                playingTPlayers.Add(captainT);
            }
        }

        foreach (var player in players)
        {
            if (player != null
                && player.IsValid
                && player.PlayerPawn != null)
            {
                if ((Team)player.PlayerPawn.TeamNum == Team.T && !playingTPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.MoveOverflowPlayersToSpec)
                    {
                        if (playingTPlayers.Count >= cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("SkipTeamPickingPhase: Disregarding overflow T player {PlayerName}", player.Name);
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation("SkipTeamPickingPhase: Adding {PlayerName} to T picked players", player.Name);
                    playingTPlayers.Add(player);
                }

                if ((Team)player.PlayerPawn.TeamNum == Team.CT && !playingCtPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.MoveOverflowPlayersToSpec)
                    {
                        if (playingCtPlayers.Count >= cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("SkipTeamPickingPhase: Disregarding overflow CT player {PlayerName}", player.Name);
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation("SkipTeamPickingPhase: Adding {PlayerName} to CT picked players", player.Name);
                    playingCtPlayers.Add(player);
                }
            }
        }

        MovePlayersToDesignatedTeamsPreMatch();
        if (!cfg.DisableCaptains && captainCt != null && captainT != null)
        {
            SetTeamName(Team.CT, captainCt.Name);
            SetTeamName(Team.T, captainT.Name);
        }
        StartKnifeRound();
    }

    /// <summary>
    /// Prompts the specified team captain to select a player for their team.
    /// </summary>
    internal void PromptCaptainToPickPlayer(IPlayer? captain, Team team)
    {
        if (captain == null)
        {
            logger.LogError("PromptCaptainToPickPlayer: Captain is null.");
            return;
        }

        var players = GetPlayers();
        players.RemoveAll(p => pickedCtPlayers.Any(pp => pp.PlayerID == p.PlayerID) || pickedTPlayers.Any(pp => pp.PlayerID == p.PlayerID) || p.PlayerID == captain.PlayerID);

        if (players.Count == 0)
        {
            logger.LogWarning("PromptCaptainToPickPlayer: No players available to pick.");
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        if (team == Team.CT)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.ct", captain.Name]);
        }

        if (team == Team.T)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.t", captain.Name]);
        }

        // Bot: auto-pick random player
        if (IsBot(captain))
        {
            var randomIndex = Random.Shared.Next(players.Count);
            var selectedPlayer = players[randomIndex];
            var selectedPlayerName = selectedPlayer.Name;
            if (team == Team.CT)
            {
                AssignPickedPlayerToTeamCt(captain, selectedPlayerName);
            }
            else
            {
                AssignPickedPlayerToTeamT(captain, selectedPlayerName);
            }
            return;
        }

        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.team_picking", team == Team.CT ? "CT" : "T"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .DisableExit()
            .SetAutoCloseDelay(0);

        foreach (var player in players)
        {
            var displayName = player.Name ?? $"#{player.PlayerID}";
            var button = new ButtonMenuOption(displayName);
            if (team == Team.CT)
            {
                button.Click += async (sender, args) =>
                {
                    AssignPickedPlayerToTeamCt(captain, displayName);
                };
            }
            else
            {
                button.Click += async (sender, args) =>
                {
                    AssignPickedPlayerToTeamT(captain, displayName);
                };
            }
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptCaptainToPickPlayer: Added option {PlayerName} for {CaptainName} ({Team})", displayName, captain.Name, team == Team.CT ? "CT" : "T");
            builder.AddOption(button);
        }

        var menu = builder.Build();
        if (IsPlayerValid(captain))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptCaptainToPickPlayer: Displaying picking menu to {CaptainName} for team {Team}", captain.Name, team == Team.CT ? "CT" : "T");
            Core.MenusAPI.OpenMenuForPlayer(captain, menu);
        }
    }

    /// <summary>
    /// Assigns captains for the teams if they have not already been selected.
    /// </summary>
    internal void PickCaptains()
    {
        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: Captains are disabled in configuration.");
            return;
        }

        if (captainCt == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: CT captain is null, picking now.");
            PickCtCaptain(null);
        }
        if (captainT == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: T captain is null, picking now.");
            PickTCaptain(null);
        }

    }

    /// <summary>
    /// Assigns a Counter-Terrorist team captain.
    /// </summary>
    internal void PickCtCaptain(IPlayer? player)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (captainCt != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                if (pickedCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
                {
                    pickedCtPlayers.Remove(captainCt);
                }
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (playingCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
                {
                    playingCtPlayers.Remove(captainCt);
                }
            }
        }

        captainCt = player;

        if (captainCt == null || !IsPlayerValid(captainCt))
        {
            logger.LogError("PickCtCaptain: player is invalid, picking random captain for CT team.");
            captainCt = PickRandomCaptain(Team.CT);
        }

        if (captainCt != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                pickedCtPlayers.Add(captainCt);
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (!playingCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
                    playingCtPlayers.Add(captainCt);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("PickCtCaptain: picked {PlayerName}", captainCt.Name);
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.ct", captainCt.Name]);
        }
        else
        {
            logger.LogError("PickCtCaptain: Failed to pick a CT captain.");
        }
    }

    /// <summary>
    /// Assigns a Counter-Terrorist team captain.
    /// </summary>
    internal void PickTCaptain(IPlayer? player)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (captainT != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                if (pickedTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
                {
                    pickedTPlayers.Remove(captainT);
                }
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (playingTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
                {
                    playingTPlayers.Remove(captainT);
                }
            }
        }

        captainT = player;

        if (captainT == null || !IsPlayerValid(captainT))
        {
            logger.LogError("PickTCaptain: player is invalid, picking random captain for T team.");
            captainT = PickRandomCaptain(Team.T);
        }

        if (captainT != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                pickedTPlayers.Add(captainT);
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (!playingTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
                    playingTPlayers.Add(captainT);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("PickTCaptain: picked {PlayerName}", captainT.Name);
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.t", captainT.Name]);
        }
        else
        {
            logger.LogError("PickTCaptain: Failed to pick a T captain.");
        }

    }

    /// <summary>
    /// Selects a random player to serve as a captain from the list of currently playing players.
    /// </summary>
    internal IPlayer? PickRandomCaptain(Team? team = null)
    {
        List<IPlayer> players = new();

        if (team != null)
        {
            players = GetPlayersInTeam(team.Value);
        }

        if (captainCt != null)
            players.RemoveAll(p => p.PlayerID == captainCt.PlayerID);
        if (captainT != null)
            players.RemoveAll(p => p.PlayerID == captainT.PlayerID);

        if (players.Count == 0)
        {
            logger.LogWarning("PickRandomCaptain: No players available to pick a captain.");
            return null;
        }
        var captainIndex = Random.Shared.Next(players.Count);
        return players[captainIndex];
    }

    /// <summary>
    /// Assigns the player selected by the CT captain to the CT team.
    /// </summary>
    internal void AssignPickedPlayerToTeamCt(IPlayer captain, string pickedPlayerName)
    {
        CloseMenuForPlayer(captain);
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamCt: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptCaptainToPickPlayer(captain, Team.CT);
            return;
        }

        pickedCtPlayers.Add(player);

        if (IsBot(player))
            player.SwitchTeamAsync(Team.CT);
        else
            player.ChangeTeamAsync(Team.CT);

        if (cfg.DetailedLogging)
            logger.LogInformation("AssignPickedPlayerToTeamCt: {CaptainName} picked {PlayerName} for CT team.", captain.Name, player.Name);
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.ct", captain.Name, player.Name]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        PromptCaptainToPickPlayer(captainT, Team.T);
    }

    /// <summary>
    /// Assigns the player selected by the T captain to the T team.
    /// </summary>
    internal void AssignPickedPlayerToTeamT(IPlayer captain, string pickedPlayerName)
    {
        CloseMenuForPlayer(captain);
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamT: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptCaptainToPickPlayer(captain, Team.T);
            return;
        }

        pickedTPlayers.Add(player);

        if (IsBot(player))
            player.SwitchTeamAsync(Team.T);
        else
            player.ChangeTeamAsync(Team.T);

        var currentMenu = Core.MenusAPI.GetCurrentMenu(captain);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(captain, currentMenu);
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("AssignPickedPlayerToTeamT: {CaptainName} picked {PlayerName} for T team.", captain.Name, player.Name);
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.t", captain.Name, player.Name]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        PromptCaptainToPickPlayer(captainCt, Team.CT);
    }

    /// <summary>
    /// Moves players to their designated teams before the picking phase begins.
    /// </summary>
    internal void MovePlayersToDesignatedTeamsPrePick()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("MovePlayersToDesignatedTeamsPrePick");

        var players = GetPlayingPlayers();
        var pickedPlayerIds = new HashSet<int>(pickedCtPlayers.Select(p => p.PlayerID).Concat(pickedTPlayers.Select(p => p.PlayerID)));
        players.RemoveAll(p => pickedPlayerIds.Contains(p.PlayerID));

        if (!cfg.MovePlayersToSpecDuringTeamPicking)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("MovePlayersToDesignatedTeamsPrePick: Moving players to spec during team picking is disabled in configuration.");
            return;
        }

        isMovingPlayersToTeams = true;

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Player is a bot, skipping move to SPEC");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to SPEC", player.Name);
            player.ChangeTeamAsync(Team.Spectator);
        }

        var pickedCtPlayerIds = new HashSet<int>(pickedCtPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!pickedCtPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to CT", player.Name);
            if (IsBot(player))
                player.SwitchTeamAsync(Team.CT);
            else
                player.ChangeTeamAsync(Team.CT);
        }

        var pickedTPlayerIds = new HashSet<int>(pickedTPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!pickedTPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to T", player.Name);
            if (IsBot(player))
                player.SwitchTeamAsync(Team.T);
            else
                player.ChangeTeamAsync(Team.T);
        }

        isMovingPlayersToTeams = false;
    }
}
