using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace MixScrims;

partial class MixScrims
{
    /// <summary>
    /// Registers event listeners that operate independently of the current server state.
    /// </summary>
    internal void RegisterStateAgnosticListeners()
    {
        Core.Event.OnClientPutInServer += HandleClientPutInServer;
        Core.Event.OnClientDisconnected += OnPlayerDisconnect;
        Core.Event.OnMapLoad += HandleStateAgnosticMapLoad;
    }

    /// <summary>
    /// Clears auto-reset state on every map load so that a stale
    /// <c>resetMixOnFirstJoin</c> flag (set when all players left during an active
    /// match) cannot trigger an unintended reset on the new map, and so that any
    /// running grace-period timer is cancelled before it can fire in a new context.
    /// </summary>
    internal void HandleStateAgnosticMapLoad(IOnMapLoadEvent @event)
    {
        if (resetMixOnFirstJoin)
        {
            logger.LogInformation("HandleStateAgnosticMapLoad: Clearing resetMixOnFirstJoin flag — map changed while flag was set.");
            resetMixOnFirstJoin = false;
        }
        CancelAutoResetOnLeaveTimer(announce: false);
    }

    /// <summary>
    /// Handles the event when a client is put into the server.
    /// </summary>
    internal void HandleClientPutInServer(IOnClientPutInServerEvent clientKind)
    {
        var playerSlot = clientKind.PlayerId;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleClientPutInServer: Slot {Slot}", playerSlot);

        if (freshlyJoinedPlayers.Add(playerSlot) && cfg.DetailedLogging)
            logger.LogInformation("HandleClientPutInServer: Added player slot {Slot} to freshlyJoinedPlayers.", playerSlot);

        if (resetMixOnFirstJoin)
        {
            logger.LogInformation("HandleClientPutInServer: resetMixOnFirstJoin flag is set, resetting match.");
            resetMixOnFirstJoin = false;
            ResetPluginState();
            return;
        }

        try
        {
            var player = Core.PlayerManager.GetPlayer(playerSlot);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleClientPutInServer: Retrieved player from slot {Slot}.", playerSlot);
            if (player != null && player.IsValid)
            {
                if (MatchState != MatchState.Warmup && MatchState != MatchState.MapVoting && MatchState != MatchState.MapChosen && MatchState != MatchState.PickingTeam)
                {
                    if (preventNotPickedPlayersFromJoiningOngoingMatch)
                    {
                        if (playingCtPlayers.Any(p => p.PlayerID == player.PlayerID) || playingTPlayers.Any(p => p.PlayerID == player.PlayerID) || pickedCtPlayers.Any(p => p.PlayerID == player.PlayerID) || pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandlePlayerJoinTeam: Player {PlayerName} is already in a team, allowing.", player.Controller.PlayerName);
                        }
                        else
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandlePlayerJoinTeam: Player {PlayerName} attempted to join a team but is not in playing/picked lists, kicking.", player.Controller.PlayerName);
                            KickPlayer(player.SteamID, Core.Localizer["info.kick_reason.not_picked"]);
                        }
                    }
                }

                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleClientPutInServer: Moving player slot {Slot} to Spectator team.", playerSlot);
                
                Core.Scheduler.DelayBySeconds(2, () =>
                {
                    var delayedPlayer = Core.PlayerManager.GetPlayer(playerSlot);
                    if (delayedPlayer != null && delayedPlayer.IsValid)
                    {
                        var currentState = mixScrimsService.GetCurrentMatchState();
                        if (currentState == MatchState.Warmup || currentState == MatchState.MapVoting || currentState == MatchState.MapChosen)
                            HandlePlayerChangeTeam(delayedPlayer, 0);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleClientPutInServer: Error moving player slot {Slot} to Spectator team.", playerSlot);
        }
    }

    [ClientCommandHookHandler]
    public HookResult OnClientCommand(int playerId, string commandLine)
    {
        var player = Core.PlayerManager.GetPlayer(playerId);
        if (!commandLine.StartsWith("jointeam"))
             return HookResult.Continue;
        
        if (player == null)
        {
            logger.LogError("HandleJointeamListener: player is null");
            return HookResult.Stop;
        }

        int teamTojoin = 9;

        var parts = commandLine.Split(' ');
        if (parts.Length > 1)
        {
            int.TryParse(parts[1], out teamTojoin);
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("OnClientCommand: {PlayerName} executing jointeam command with team {Team}", player.Controller.PlayerName, teamTojoin);

        if (teamTojoin == 9)
        {
            logger.LogError("HandleJointeamListener: {PlayerName} tried to join, but selected team was not found in command: {Command}", player.Controller.PlayerName, commandLine);
            return HookResult.Stop;
        }

        return HandlePlayerChangeTeam(player, teamTojoin);
    }

    /// <summary>
    /// Assigns a newly joined player to a team based on current team balance and respawns the player.
    /// </summary>
    internal void HandlePlayerChangeTeamOnJoin(IPlayer player)
    {
        int playerSlot = player.Slot;
        if (!freshlyJoinedPlayers.Contains(playerSlot))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeamOnJoin: Player {PlayerName} is not in freshlyJoinedPlayers, ignoring.", player.Controller.PlayerName);
            return;
        }

        freshlyJoinedPlayers.Remove(playerSlot);

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerChangeTeamOnJoin: Player {PlayerName} has joined the server.", player.Controller.PlayerName);

        if (player.IsValid && !IsBot(player))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeamOnJoin: Player {PlayerName} is valid.", player.Controller.PlayerName);

            var ctPlayers = GetPlayersInTeam(Team.CT);
            var tPlayers = GetPlayersInTeam(Team.T);

            if (ctPlayers.Count > tPlayers.Count)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeamOnJoin: joining Terrorists");
                previousAutoJoinedTeam = Team.T;
                Core.Scheduler.DelayBySeconds(2, async () =>
                {
                    await player.SwitchTeamAsync(Team.T);
                    Core.Scheduler.NextTick(() => RespawnPlayer(player));
                });
                return;
            }

            if (ctPlayers.Count < tPlayers.Count)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeamOnJoin: joining CounterTerrorists");
                previousAutoJoinedTeam = Team.CT;
                Core.Scheduler.DelayBySeconds(2, async () =>
                {
                    await player.SwitchTeamAsync(Team.CT);
                    Core.Scheduler.NextTick(() => RespawnPlayer(player));
                });
                return;
            }

            if (ctPlayers.Count == tPlayers.Count)
            {
                if (previousAutoJoinedTeam == Team.T)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerChangeTeamOnJoin: both teams equal, joining CounterTerrorists");
                    previousAutoJoinedTeam = Team.CT;
                    Core.Scheduler.DelayBySeconds(2, async () =>
                    {
                        await player.SwitchTeamAsync(Team.CT);
                        Core.Scheduler.NextTick(() => RespawnPlayer(player));
                    });
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerChangeTeamOnJoin: both teams equal, joining Terrorists");
                    previousAutoJoinedTeam = Team.T;
                    Core.Scheduler.DelayBySeconds(2, async () =>
                    {
                        await player.SwitchTeamAsync(Team.T);
                        Core.Scheduler.NextTick(() => RespawnPlayer(player));
                    });
                }
            }
        }
        CancelAutoResetOnLeaveTimer();
    }

    /// <summary>
    /// Handles the player disconnect event, allowing custom logic to be executed when a player leaves the game.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandlePlayerDisconnect(EventPlayerDisconnect @event)
    {
        var player = @event.UserIdPlayer;
        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnect");
        HandleDisconnectedPlayer(player);
        return HookResult.Continue;
    }

    /// <summary>
    /// Handles the disconnection of a player from the server when a client disconnect event is received.
    /// </summary>
    public void OnPlayerDisconnect(IOnClientDisconnectedEvent @event)
    {

        int slot = @event.PlayerId;
        if (cfg.DetailedLogging)
            logger.LogInformation("OnPlayerDisconnect: slot {Slot}", slot);
        HandleDisconnectedPlayer(Core.PlayerManager.GetPlayer(slot));
    }

    /// <summary>
    /// Handles the removal and cleanup of a player who has disconnected from the match.
    /// </summary>
    internal void HandleDisconnectedPlayer(IPlayer? player)
    {
        if (player == null)
        {
            logger.LogError("HandleDisconnectedPlayer: player is null, ignoring");
            return;
        }

        if (recentlyDisconnectedPlayers.Contains(player.Slot))
            return;
        recentlyDisconnectedPlayers.Add(player.Slot);
        var disconnectingPlayerSlot = player.Slot;
        Core.Scheduler.DelayBySeconds(1, () => recentlyDisconnectedPlayers.Remove(disconnectingPlayerSlot));

        // Cache player name for logging since Controller might become invalid during disconnect
        var playerName = IsPlayerValid(player) ? player.Controller.PlayerName : $"Player {player.PlayerID}";

        freshlyJoinedPlayers.Remove(player.Slot);

        if (pickedCtPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            pickedCtPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from pickedCtPlayers.", playerName);
        }

        if (pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            pickedTPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from pickedTPlayers.", playerName);
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        bool isActivMatchState = matchState == MatchState.KnifeRound ||
                                 matchState == MatchState.Match ||
                                 matchState == MatchState.PickingStartingSide ||
                                 matchState == MatchState.Timeout;

        // During active match states, slot reservation behavior depends on config
        // If PreventNotPickedPlayersFromJoiningOngoingMatch is true: reserve slot (keep in list)
        // If false: remove immediately to allow new players without exceeding limit
        bool shouldReserveSlot = isActivMatchState && preventNotPickedPlayersFromJoiningOngoingMatch;
        
        if (playingCtPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            if (!shouldReserveSlot)
            {
                playingCtPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from playingCtPlayers.", playerName);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: {PlayerName} disconnected from CT - keeping slot reserved.", playerName);
            }
        }

        if (playingTPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            if (!shouldReserveSlot)
            {
                playingTPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from playingTPlayers.", playerName);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: {PlayerName} disconnected from T - keeping slot reserved.", playerName);
            }
        }

        playerColors.Remove(player.PlayerID);

        matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.PickingTeam)
        {
            if (!cfg.DisableCaptains)
            {
                if (player.PlayerID == captainCt?.PlayerID)
                {
                    captainCt = null;
                    StartTeamPickingPhase();
                }
                if (player.PlayerID == captainT?.PlayerID)
                {
                    captainT = null;
                    StartTeamPickingPhase();
                }
            }
        }

        if (matchState == MatchState.KnifeRound
            || matchState == MatchState.MapChosen
            || matchState == MatchState.Timeout)
        {
            if (!cfg.DisableCaptains)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: MatchState is {MatchState}", matchState);
                if (player.PlayerID == captainCt?.PlayerID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is CT captain");

                    captainCt = null;
                    var newCaptain = playingCtPlayers.Where(p => p.PlayerID != player.PlayerID).FirstOrDefault();

                    if (cfg.DetailedLogging)
                    {
                        var newCaptainName = newCaptain != null && IsPlayerValid(newCaptain) 
                            ? newCaptain.Controller.PlayerName 
                            : "None";
                        logger.LogInformation("HandleDisconnectedPlayer: New CT captain is {NewCaptain}", newCaptainName);
                    }

                    PickCtCaptain(newCaptain);
                }
                if (player.PlayerID == captainT?.PlayerID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is T captain");

                    captainT = null;
                    var newCaptain = playingTPlayers.Where(p => p.PlayerID != player.PlayerID).FirstOrDefault();

                    if (cfg.DetailedLogging)
                    {
                        var newCaptainName = newCaptain != null && IsPlayerValid(newCaptain) 
                            ? newCaptain.Controller.PlayerName 
                            : "None";
                        logger.LogInformation("HandleDisconnectedPlayer: New T captain is {NewCaptain}", newCaptainName);
                    }
                    PickTCaptain(newCaptain);
                }
            }
        }

        if (matchState == MatchState.PickingStartingSide)
        {
            if (!cfg.DisableCaptains && player.PlayerID == winnerCaptain?.PlayerID)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is winner captain");
                StayStartingSides(winnerCaptain);
            }
        }

        if (readyPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removing {PlayerName} from readyPlayers.", playerName);
            RemovePlayerFromReadyList(player, true);
        }

        PunishOnLeave(player);

        try
        {
            if (IsPlayerValid(player))
            {
                Core.MenusAPI.CloseActiveMenu(player);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleDisconnectedPlayer: Error closing active menu for player {PlayerName}", playerName);
        }

        if (matchState == MatchState.Warmup || matchState == MatchState.MapChosen)
        {
            CheckReadyPlayersToStart();
        }

        CheckAutoResetOnLeave();
    }

    /// <summary>
    /// Handles the <see cref="EventPlayerTeam"/> game event by processing a player's team change request.
    /// </summary>

    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleEventPlayerTeam(EventPlayerTeam @event)
    {
        var player = @event.UserIdPlayer;

        if (player == null)
        {
            logger.LogError("HandleEventPlayerTeam: player is null");
            return HookResult.Stop;
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("HandleEventPlayerTeam: player's PlayerPawn is null");
            return HookResult.Stop;
        }

        int teamTojoin = @event.Team;

        return HandlePlayerChangeTeam(player, teamTojoin);
    }

    /// <summary>
    /// Handles a player's request to change teams during a match, enforcing team selection rules based on the current
    /// match state.
    /// </summary>
    public HookResult HandlePlayerChangeTeam(IPlayer? player, int teamTojoin)
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerChangeTeam: Called for player {PlayerName} (slot {Slot}), teamTojoin={Team}", player?.Controller.PlayerName, player?.Slot, teamTojoin);


        if (player == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("HandlePlayerChangeTeam: player is null");
            return HookResult.Stop;
        }

        if (player.IsFakeClient)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: {PlayerName} is a fake client, allowing", player.Controller.PlayerName);
            return HookResult.Continue;
        }

        if (!player.IsValid)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("HandlePlayerChangeTeam: player is not valid");
            return HookResult.Stop;
        }

        if (player.PlayerPawn == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("HandlePlayerChangeTeam: player PlayerPawn is null");
            return HookResult.Stop;
        }

        if (IsBot(player))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: {PlayerName} is a bot, allowing", player.Controller.PlayerName);
            return HookResult.Continue;
        }

        // Skip validation during programmatic team moves (switch/stay sides, etc.)
        if (isMovingPlayersToTeams)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: Skipping validation during team move for {PlayerName}", player.Controller.PlayerName);
            return HookResult.Continue;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerChangeTeam: Current match state is {MatchState}", matchState);

        if (matchState == MatchState.Warmup ||
            matchState == MatchState.MapVoting ||
            matchState == MatchState.MapChosen)
        {
            bool isInFreshlyJoined = freshlyJoinedPlayers.Contains(player.Slot);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: Player {PlayerName} in freshlyJoinedPlayers: {InList}", player.Controller.PlayerName, isInFreshlyJoined);

            if (isInFreshlyJoined)
            {
                HandlePlayerChangeTeamOnJoin(player);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: {MatchState}. {PlayerName} joined team {Team}", matchState, player.Controller.PlayerName, teamTojoin);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: {MatchState}. {PlayerName} not freshly joined, allowing change to {Team}", matchState, player.Controller.PlayerName, teamTojoin);
            }

            if (cfg.ShowReadyStatusInScoreboard)
                SetPlayerReadyStatusInScoreboard(player, false);
            return HookResult.Continue;
        }

        if (matchState == MatchState.KnifeRound)
        {
            if (!cfg.DisableCaptains && (player.PlayerID == captainCt?.PlayerID || player.PlayerID == captainT?.PlayerID))
            {
                PrintMessageToPlayer(player, Core.Localizer["error.captain.cannot_change_team"]);
                return HookResult.Stop;
            }
        }

        if (matchState == MatchState.PickingTeam)
        {
            if (teamTojoin == 3)
            {
                if (pickedCtPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} re-joined CT team.", player.Controller.PlayerName);
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} attempted to join CT without being picked.", player.Controller.PlayerName);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.join_denied.ct"]);
                    return HookResult.Stop;
                }
            }
            if (teamTojoin == 2)
            {
                if (pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} re-joined T team.", player.Controller.PlayerName);
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} attempted to join T without being picked.", player.Controller.PlayerName);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.join_denied.t"]);
                    return HookResult.Stop;
                }
            }
        }

        if (matchState == MatchState.KnifeRound ||
            matchState == MatchState.Match ||
            matchState == MatchState.PickingStartingSide ||
            matchState == MatchState.Timeout)
        {
            // Handle auto-select (Team.None = 0) by converting to explicit team selection
            // This prevents players from bypassing team limit checks
            if (teamTojoin == 0)
            {
                // Determine which team the player should be on based on playing lists
                bool isInCtTeam = playingCtPlayers.Any(p => p.PlayerID == player.PlayerID);
                bool isInTTeam = playingTPlayers.Any(p => p.PlayerID == player.PlayerID);

                if (isInCtTeam)
                {
                    // Player is in CT team, treat auto-select as CT team selection
                    teamTojoin = 3;
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select, converting to CT (3).", player.Controller.PlayerName);
                }
                else if (isInTTeam)
                {
                    // Player is in T team, treat auto-select as T team selection
                    teamTojoin = 2;
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select, converting to T (2).", player.Controller.PlayerName);
                }
                else
                {
                    // Player is not in any team list - block the attempt
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select but is not in any team list. Blocking.", player.Controller.PlayerName);
                    PrintMessageToPlayer(player, Core.Localizer["error.tried_to_bypass_team_check"]);
                    return HookResult.Stop;
                }
            }

            if (teamTojoin == 3)
            {
                // Check if player is already in the playing list
                bool isInPlayingList = playingCtPlayers.Any(p => p.PlayerID == player.PlayerID);
                int maxTeamSize = cfg.MinimumReadyPlayers / 2;
                
                // Get actual connected player count (excludes disconnected players with reserved slots)
                var actualCtPlayers = GetPlayersInTeam(Team.CT);
                int actualCtCount = actualCtPlayers.Count;
                
                // Get list count (includes disconnected players with reserved slots)
                int listCount = playingCtPlayers.Count;

                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerJoinTeam - Match: CT list {ListCount}/{Max}, actual {Actual}, in list: {InList}", listCount, maxTeamSize, actualCtCount, isInPlayingList);

                if (isInPlayingList)
                {
                    // Player is already in the playing list (has a reserved slot), allow rejoin
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} re-joined CT team.", player.Controller.PlayerName);
                    Core.Scheduler.NextTick(() => FixTeammateColors());
                    CheckAutoResetOnLeave();
                    return HookResult.Continue;
                }

                // Player is NOT in playing list - check if they can join
                if (preventNotPickedPlayersFromJoiningOngoingMatch)
                {
                    // Config blocks new players from joining ongoing match
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} blocked from joining CT (prevention enabled).", player.Controller.PlayerName);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.ct"]);
                    return HookResult.Stop;
                }

                // Config allows new players - check list count (includes reserved slots for disconnected players)
                // This prevents exceeding team limit when disconnected players have reserved slots
                if (listCount < maxTeamSize)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} joined CT team (list: {List}/{Max}, actual: {Actual}).", player.Controller.PlayerName, listCount, maxTeamSize, actualCtCount);
                    playingCtPlayers.Add(player);
                    Core.Scheduler.NextTick(() => FixTeammateColors());
                    CheckAutoResetOnLeave();
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} blocked from CT - team full (list: {List}/{Max}, actual: {Actual}).", player.Controller.PlayerName, listCount, maxTeamSize, actualCtCount);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.ct"]);
                    return HookResult.Stop;
                }
            }

            if (teamTojoin == 2)
            {
                // Check if player is already in the playing list
                bool isInPlayingList = playingTPlayers.Any(p => p.PlayerID == player.PlayerID);
                int maxTeamSize = cfg.MinimumReadyPlayers / 2;
                
                // Get actual connected player count (excludes disconnected players with reserved slots)
                var actualTPlayers = GetPlayersInTeam(Team.T);
                int actualTCount = actualTPlayers.Count;
                
                // Get list count (includes disconnected players with reserved slots)
                int listCount = playingTPlayers.Count;

                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerJoinTeam - Match: T list {ListCount}/{Max}, actual {Actual}, in list: {InList}", listCount, maxTeamSize, actualTCount, isInPlayingList);

                if (isInPlayingList)
                {
                    // Player is already in the playing list (has a reserved slot), allow rejoin
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} re-joined T team.", player.Controller.PlayerName);
                    Core.Scheduler.NextTick(() => FixTeammateColors());
                    CheckAutoResetOnLeave();
                    return HookResult.Continue;
                }

                // Player is NOT in playing list - check if they can join
                if (preventNotPickedPlayersFromJoiningOngoingMatch)
                {
                    // Config blocks new players from joining ongoing match
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} blocked from joining T (prevention enabled).", player.Controller.PlayerName);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.t"]);
                    return HookResult.Stop;
                }

                // Config allows new players - check list count (includes reserved slots for disconnected players)
                // This prevents exceeding team limit when disconnected players have reserved slots
                if (listCount < maxTeamSize)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} joined T team (list: {List}/{Max}, actual: {Actual}).", player.Controller.PlayerName, listCount, maxTeamSize, actualTCount);
                    playingTPlayers.Add(player);
                    Core.Scheduler.NextTick(() => FixTeammateColors());
                    CheckAutoResetOnLeave();
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} blocked from T - team full (list: {List}/{Max}, actual: {Actual}).", player.Controller.PlayerName, listCount, maxTeamSize, actualTCount);
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.t"]);
                    return HookResult.Stop;
                }
            }

            if (teamTojoin == 1)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} joined Spectators.", player.Controller.PlayerName);
                // Don't remove players from playing lists when they go to spectator during active match
                // They should keep their slot reserved so they can rejoin their team
                // Lists will be cleaned up when the match resets
                return HookResult.Continue;
            }
        }

        return HookResult.Stop;
    }

    /// <summary>
    /// Handles the end of the round freeze period by updating the respawn state for players.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleFreezetimeEnd(EventRoundFreezeEnd @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Match || matchState == MatchState.KnifeRound)
        {
            canPlayerBeRespawned = false;
        }
        return HookResult.Continue;
    }

    [EventListener<EventDelegates.OnEntityTakeDamage>]
    public void HandleTakeDamage(IOnEntityTakeDamageEvent @event)
    {
        if (!cfg.FaceitLikeDamageControl)
        {
            return;
        }

        var victim = @event.Entity.As<CCSPlayerPawn>();
        var attacker = @event.Info.Attacker.Value?.As<CCSPlayerPawn>();
        var weapon = @event.Info.DamageType;

        if (attacker == null)
        {
            logger.LogWarning("HandleTakeDamage: Attacker is null");
            return;
        }

        if (victim == null)
        {
            logger.LogWarning("HandleTakeDamage: Victim is null");
            return;
        }

        //if (cfg.DetailedLogging)
        //    logger.LogInformation($"HandleTakeDamage: {attacker.Controller.Value?.PlayerName} damaged {victim.Controller.Value?.PlayerName} with {weapon}");

        if (attacker.Team == victim.Team)
        {
            if (weapon == DamageTypes_t.DMG_BULLET || weapon == DamageTypes_t.DMG_SLASH)
            {
                if(cfg.DetailedLogging)
                    logger.LogInformation("HandleTakeDamage: Friendly fire or knife slash detected, skipping.");

                @event.Info.Damage = 0;
            }
        }
    }
}
