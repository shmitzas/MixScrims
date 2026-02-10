using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

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

        if (!cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"StartTeamPickingPhase: Current captains - CT: {captainCt?.Controller?.PlayerName ?? "null"}, T: {captainT?.Controller?.PlayerName ?? "null"}");

            PickCaptains();

            if (captainCt == null || captainT == null)
            {
                logger.LogError("StartTeamPickingPhase: One or both captains are null.");
                logger.LogError($"captainCt: {(captainCt != null ? "null" : "null")}");
                logger.LogError($"captainT: {(captainT != null ? "null" : "null")}");
                logger.LogError($"StartTeamPickingPhase: Valid players in the server: {GetPlayers().Count}");
                logger.LogError("StartTeamPickingPhase: Aborting team picking phase.");
                PrintMessageToAllPlayers(Core.Localizer["error.captain.selection_failed"]);
                ResetPluginState();
                return;
            }

            if (cfg.DetailedLogging)
            {
                logger.LogInformation($"StartTeamPickingPhase: Before validation - pickedCtPlayers count: {pickedCtPlayers.Count}, pickedTPlayers count: {pickedTPlayers.Count}");
                logger.LogInformation($"StartTeamPickingPhase: CT captain in picked list: {pickedCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID)}");
                logger.LogInformation($"StartTeamPickingPhase: T captain in picked list: {pickedTPlayers.Any(p => p.PlayerID == captainT.PlayerID)}");
            }

            // Ensure captains are in picked lists (handles captains set during Warmup state)
            if (captainCt != null && IsPlayerValid(captainCt))
            {
                if (!pickedCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"StartTeamPickingPhase: Adding CT Captain {captainCt.Controller.PlayerName} to pickedCtPlayers.");
                    pickedCtPlayers.Add(captainCt);
                }
            }

            if (captainT != null && IsPlayerValid(captainT))
            {
                if (!pickedTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"StartTeamPickingPhase: Adding T Captain {captainT.Controller.PlayerName} to pickedTPlayers.");
                    pickedTPlayers.Add(captainT);
                }
            }

            if (cfg.DetailedLogging)
            {
                logger.LogInformation($"StartTeamPickingPhase: After validation - pickedCtPlayers count: {pickedCtPlayers.Count}, pickedTPlayers count: {pickedTPlayers.Count}");
            }
        }

        if (cfg.SkipTeamPicking)
        {
            SkipTeamPickingPhase();
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Captains disabled, auto-assigning teams based on current positions.");
            SkipTeamPickingPhase();
            return;
        }

        mixScrimsService.SetMatchState(MatchState.PickingTeam);        

        PauseMatch();
        Core.Engine.ExecuteCommand("exec mixscrims/teampick.cfg");

        MovePlayersToDesignatedTeamsPrePick();

        SetTeamName(Team.CT, captainCt == null ? null : captainCt.Controller.PlayerName);
        SetTeamName(Team.T, captainT == null ? null :  captainT.Controller.PlayerName);

        Random random = new Random();
        int teamStarting = random.Next(2, 4);
        if (teamStarting == 3)
        {
            PromptTCaptainoPickPlayer(captainCt, Team.CT);
            return;
        }
        if (teamStarting == 2)
        {
            PromptTCaptainoPickPlayer(captainT, Team.T);
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

        Core.Engine.ExecuteCommand("exec mixscrims/teampick.cfg");
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
                        if (playingCtPlayers.Count == cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation($"SkipTeamPickingPhase: Disregarding overflow player {player.Controller!.PlayerName}");
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation($"SkipTeamPickingPhase: Adding {player.Controller!.PlayerName} to T picked players");
                    playingTPlayers.Add(player);
                }

                if ((Team)player.PlayerPawn.TeamNum == Team.CT && !playingCtPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.MoveOverflowPlayersToSpec)
                    {
                        if (playingCtPlayers.Count == cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation($"SkipTeamPickingPhase: Disregarding overflow player {player.Controller!.PlayerName}");
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation($"SkipTeamPickingPhase: Adding {player.Controller!.PlayerName} to CT picked players");
                    playingCtPlayers.Add(player);
                }
            }
        }

        MovePlayersToDesignatedTeamsPreMatch();
        if (!cfg.DisableCaptains && captainCt != null && captainT != null)
        {
            SetTeamName(Team.CT, captainCt.Controller!.PlayerName);
            SetTeamName(Team.T, captainT.Controller!.PlayerName);
        }
        StartKnifeRound();
    }

    /// <summary>
    /// Prompts the specified team captain to select a player for their team.
    /// </summary>
    internal void PromptTCaptainoPickPlayer(IPlayer? captain, Team team)
    {
        if (captain == null)
        {
            logger.LogError("PromptTCaptainoPickPlayer: Captain is null.");
            return;
        }

        var players = GetPlayers();
        players.RemoveAll(p => pickedCtPlayers.Contains(p) || pickedTPlayers.Contains(p) || p.PlayerID == captain.PlayerID);

        if (players.Count == 0)
        {
            logger.LogWarning("PromptTCaptainoPickPlayer: No players available to pick.");
            StartKnifeRound();
            return;
        }

        if (team == Team.CT)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.ct", captain.Controller!.PlayerName]);
        }

        if (team == Team.T)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.t", captain.Controller!.PlayerName]);
        }

        // Bot: auto-pick random player
        if (IsBot(captain))
        {
            var randomIndex = new Random().Next(players.Count);
            var selectedPlayer = players[randomIndex];
            if (team == Team.CT)
            {
                Core.Scheduler.NextTick(() => AssignPickedPlayerToTeamCt(captain, selectedPlayer.Controller!.PlayerName));
            }
            else
            {
                Core.Scheduler.NextTick(() => AssignPickedPlayerToTeamT(captain, selectedPlayer.Controller!.PlayerName));
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

        builder.DisableExit();

        foreach (var player in players)
        {
            var displayName = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
            var button = new ButtonMenuOption(displayName);
            if (team == Team.CT)
            {
                button.Click += async (sender, args) =>
                {
                    Core.Scheduler.NextTick(()=>AssignPickedPlayerToTeamCt(captain, displayName));
                    await ValueTask.CompletedTask;
                };
            }
            else
            {
                button.Click += async (sender, args) =>
                {
                    Core.Scheduler.NextTick(() => AssignPickedPlayerToTeamT(captain, displayName));
                    await ValueTask.CompletedTask;
                };
            }
            builder.AddOption(button);
        }

        var menu = builder.Build();
        if (IsPlayerValid(captain))
        {
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
                playingCtPlayers.Add(captainCt);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation($"PickCtCaptain: picked {captainCt.Controller!.PlayerName}");
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.ct", captainCt.Controller!.PlayerName]);
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
                playingTPlayers.Add(captainT);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation($"PickTCaptain: picked {captainT.Controller!.PlayerName}");
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.t", captainT.Controller!.PlayerName]);
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
        var random = new Random();
        var captainIndex = random.Next(players.Count);
        return players[captainIndex];
    }

    /// <summary>
    /// Assigns the player selected by the CT captain to the CT team.
    /// </summary>
    internal void AssignPickedPlayerToTeamCt(IPlayer captain, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamCt: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptTCaptainoPickPlayer(captain, Team.CT);
            return;
        }

        pickedCtPlayers.Add(player);

        if (IsBot(player))
        {
            player.SwitchTeam(Team.CT);
        }
        player.ChangeTeam(Team.CT);

        if (cfg.DetailedLogging)
            logger.LogInformation($"AssignPickedPlayerToTeamCt: {captain.Controller!.PlayerName} picked {player.Controller!.PlayerName} for CT team.");
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.ct", captain.Controller!.PlayerName, player.Controller!.PlayerName]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            StartKnifeRound();
            return;
        }

        PromptTCaptainoPickPlayer(captainT, Team.T);
        CloseMenuForPlayer(captain);
    }

    /// <summary>
    /// Assigns the player selected by the T captain to the T team.
    /// </summary>
    internal void AssignPickedPlayerToTeamT(IPlayer captain, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamT: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptTCaptainoPickPlayer(captain, Team.T);
            return;
        }

        pickedTPlayers.Add(player);

        if (IsBot(player))
        {
            player.SwitchTeam(Team.T);
        }
        player.ChangeTeam(Team.T);

        var currentMenu = Core.MenusAPI.GetCurrentMenu(captain);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(captain, currentMenu);
        }

        if (cfg.DetailedLogging)
            logger.LogInformation($"AssignPickedPlayerToTeamT: {captain.Controller!.PlayerName} picked {player.Controller!.PlayerName} for T team.");
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.t", captain.Controller!.PlayerName, player.Controller!.PlayerName]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            StartKnifeRound();
            return;
        }

        PromptTCaptainoPickPlayer(captainCt, Team.CT);
        CloseMenuForPlayer(captain);
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

        isMovingPlayersToTeams = true;

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"Player is a bot, skipping move to SPEC");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller!.PlayerName} to SPEC");
            player.ChangeTeam(Team.Spectator);
        }

        var pickedCtPlayerIds = new HashSet<int>(pickedCtPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!pickedCtPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller!.PlayerName} to CT");
            if (IsBot(player))
            {
                player.SwitchTeam(Team.CT);
            }
            player.ChangeTeam(Team.CT);
        }

        var pickedTPlayerIds = new HashSet<int>(pickedTPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!pickedTPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller!.PlayerName} to T");
            if (IsBot(player))
            {
                player.SwitchTeam(Team.T);
            }
            player.ChangeTeam(Team.T);
        }

        isMovingPlayersToTeams = false;
    }
}
