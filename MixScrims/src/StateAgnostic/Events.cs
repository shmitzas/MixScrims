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
                    // Active match state. Resolve roster membership by SteamID (reconnects get new PlayerID/slot).
                    bool isListed = playingCtPlayers.Any(p => p.SteamID == player.SteamID)
                                 || playingTPlayers.Any(p => p.SteamID == player.SteamID)
                                 || pickedCtPlayers.Any(p => p.SteamID == player.SteamID)
                                 || pickedTPlayers.Any(p => p.SteamID == player.SteamID)
                                 || reservedCtSlots.Contains(player.SteamID)
                                 || reservedTSlots.Contains(player.SteamID);

                    if (preventNotPickedPlayersFromJoiningOngoingMatch)
                    {
                        if (isListed)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandleClientPutInServer: {PlayerName} is tracked/reserved, allowing.", player.Controller.PlayerName);
                        }
                        else
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandleClientPutInServer: {PlayerName} joined mid-match and is not tracked, kicking.", player.Controller.PlayerName);
                            KickPlayer(player.SteamID, Core.Localizer["info.kick_reason.not_picked"]);
                            return;
                        }
                    }
                    else if (!isListed)
                    {
                        // Prevention disabled but slots may still be full. The engine typically places a
                        // reconnecting player back onto their previous team automatically even though our
                        // validation would have rejected it, and the exact timing of that placement is
                        // not reliable to intercept via the Pre hook alone. Mark this SteamID as forced
                        // to spectator - any join attempt will be rejected AND a retrying scheduler will
                        // actively switch them to Spectator until they're confirmed there.
                        if (cfg.DetailedLogging)
                            logger.LogInformation("HandleClientPutInServer: {PlayerName} is not tracked during active match - forcing to Spectator.", player.Controller.PlayerName);

                        ScheduleForceToSpectator(player);
                        return;
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
                ScheduleAutoJoinTeamSwitch(player, Team.T);
                return;
            }

            if (ctPlayers.Count < tPlayers.Count)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeamOnJoin: joining CounterTerrorists");
                previousAutoJoinedTeam = Team.CT;
                ScheduleAutoJoinTeamSwitch(player, Team.CT);
                return;
            }

            if (ctPlayers.Count == tPlayers.Count)
            {
                if (previousAutoJoinedTeam == Team.T)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerChangeTeamOnJoin: both teams equal, joining CounterTerrorists");
                    previousAutoJoinedTeam = Team.CT;
                    ScheduleAutoJoinTeamSwitch(player, Team.CT);
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerChangeTeamOnJoin: both teams equal, joining Terrorists");
                    previousAutoJoinedTeam = Team.T;
                    ScheduleAutoJoinTeamSwitch(player, Team.T);
                }
            }
        }
        CancelAutoResetOnLeaveTimer();
    }

    /// <summary>
    /// Schedules a delayed team switch + respawn for a freshly joined player, with safety re-checks
    /// to avoid throwing if the player disconnects during the delay.
    /// </summary>
    private void ScheduleAutoJoinTeamSwitch(IPlayer player, Team targetTeam)
    {
        int playerSlot = player.Slot;
        Core.Scheduler.DelayBySeconds(2, async () =>
        {
            try
            {
                if (player is null || !player.IsValid)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("ScheduleAutoJoinTeamSwitch: player (slot {Slot}) no longer valid, skipping switch to {Team}.", playerSlot, targetTeam);
                    return;
                }

                await player.SwitchTeamAsync(targetTeam);

                Core.Scheduler.NextTick(() =>
                {
                    try
                    {
                        if (player is null || !player.IsValid)
                            return;
                        RespawnPlayer(player);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "ScheduleAutoJoinTeamSwitch: error respawning player (slot {Slot}).", playerSlot);
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ScheduleAutoJoinTeamSwitch: error switching team for player (slot {Slot}) to {Team}.", playerSlot, targetTeam);
            }
        });
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
        forcedToSpectator.Remove(player.SteamID);

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
                reservedCtSlots.Add(player.SteamID);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: {PlayerName} disconnected from CT - keeping slot reserved (SteamID {SteamId}).", playerName, player.SteamID);
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
                reservedTSlots.Add(player.SteamID);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: {PlayerName} disconnected from T - keeping slot reserved (SteamID {SteamId}).", playerName, player.SteamID);
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

        // NOTE: PlayerPawn can legitimately be null here (e.g. when moving TO Spectator the pawn
        // is being destroyed). Do NOT early-return on null pawn - HandlePlayerChangeTeam will
        // handle that case per state (Spec branch doesn't need a pawn).
        int teamTojoin = @event.Team;

        return HandlePlayerChangeTeam(player, teamTojoin);
    }

    /// <summary>
    /// Post-mode reconciler: after a team change has actually been committed by the engine,
    /// ensure the playing lists match physical team membership. Prunes entries for players that
    /// the engine placed on a team other than the one they're tracked on (e.g. a voluntary
    /// move to Spectator). Only runs during active match states and only when prevention is off -
    /// with prevention on, reservations are intentionally held for the original occupant.
    /// </summary>
    [GameEventHandler(HookMode.Post)]
    public HookResult HandleEventPlayerTeamPost(EventPlayerTeam @event)
    {
        var player = @event.UserIdPlayer;
        if (player == null || !IsPlayerValid(player) || IsBot(player) || player.IsFakeClient)
            return HookResult.Continue;

        var matchState = mixScrimsService.GetCurrentMatchState();
        bool isActive = matchState == MatchState.KnifeRound
                      || matchState == MatchState.Match
                      || matchState == MatchState.PickingStartingSide
                      || matchState == MatchState.Timeout;
        if (!isActive)
            return HookResult.Continue;

        if (preventNotPickedPlayersFromJoiningOngoingMatch)
            return HookResult.Continue;

        // Committed team for this player. Use event value (Team=new) which is authoritative here.
        int committedTeam = @event.Team;

        // If the player is no longer on CT but is still in playingCtPlayers, prune.
        if (committedTeam != (int)Team.CT && playingCtPlayers.Any(p => p.SteamID == player.SteamID))
        {
            playingCtPlayers.RemoveAll(p => p.SteamID == player.SteamID);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleEventPlayerTeamPost: Pruned {PlayerName} from playingCtPlayers (committed team {Team}).", player.Controller.PlayerName, committedTeam);
        }

        // If the player is no longer on T but is still in playingTPlayers, prune.
        if (committedTeam != (int)Team.T && playingTPlayers.Any(p => p.SteamID == player.SteamID))
        {
            playingTPlayers.RemoveAll(p => p.SteamID == player.SteamID);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleEventPlayerTeamPost: Pruned {PlayerName} from playingTPlayers (committed team {Team}).", player.Controller.PlayerName, committedTeam);
        }

        return HookResult.Continue;
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

        // Do not reject on null PlayerPawn — this is valid when moving to/from Spectator.
        // State-specific branches that need the pawn (e.g. capacity via GetPlayersInTeam)
        // handle null pawns themselves.

        if (IsBot(player))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: {PlayerName} is a bot, allowing", player.Controller.PlayerName);
            return HookResult.Continue;
        }

        // Skip validation during programmatic team moves (switch/stay sides, halftime swap, team picking).
        // Narrowed so only players the plugin is actively moving (those already tracked in the picked
        // or playing rosters by SteamID) get the bypass. Untracked players must still be validated,
        // otherwise they could exploit the window (e.g. halftime) to bypass team size limits.
        if (isMovingPlayersToTeams)
        {
            bool isTracked = playingCtPlayers.Any(p => p.SteamID == player.SteamID)
                          || playingTPlayers.Any(p => p.SteamID == player.SteamID)
                          || pickedCtPlayers.Any(p => p.SteamID == player.SteamID)
                          || pickedTPlayers.Any(p => p.SteamID == player.SteamID)
                          || reservedCtSlots.Contains(player.SteamID)
                          || reservedTSlots.Contains(player.SteamID);

            if (isTracked)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: Skipping validation during team move for tracked {PlayerName}", player.Controller.PlayerName);
                return HookResult.Continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: Programmatic move active but {PlayerName} is untracked - validating normally", player.Controller.PlayerName);
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
                // Determine which team the player belongs to based on playing lists / reservations
                // (match by SteamID so reconnected players with new PlayerID/slot still resolve correctly)
                bool isInCtTeam = playingCtPlayers.Any(p => p.SteamID == player.SteamID) || reservedCtSlots.Contains(player.SteamID);
                bool isInTTeam = playingTPlayers.Any(p => p.SteamID == player.SteamID) || reservedTSlots.Contains(player.SteamID);

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
                return HandleActiveMatchJoin(
                    player,
                    Team.CT,
                    playingCtPlayers,
                    reservedCtSlots,
                    "error.team.full.ct");
            }

            if (teamTojoin == 2)
            {
                return HandleActiveMatchJoin(
                    player,
                    Team.T,
                    playingTPlayers,
                    reservedTSlots,
                    "error.team.full.t");
            }

            if (teamTojoin == 1)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} joined Spectators.", player.Controller.PlayerName);

                // Slot release on Spec move depends on config:
                // - PreventNotPickedPlayersFromJoiningOngoingMatch = true: keep the slot reserved
                //   for the player so no one else can take it (list identity preserved).
                // - false: release the slot so other spectators can claim it. The player can still
                //   rejoin via the normal capacity check as long as the team isn't full.
                if (!preventNotPickedPlayersFromJoiningOngoingMatch)
                {
                    bool wasInCt = playingCtPlayers.RemoveAll(p => p.SteamID == player.SteamID) > 0;
                    bool wasInT = playingTPlayers.RemoveAll(p => p.SteamID == player.SteamID) > 0;
                    if (cfg.DetailedLogging && (wasInCt || wasInT))
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} moved to Spectator - released slot (CT:{Ct} T:{T}).", player.Controller.PlayerName, wasInCt, wasInT);
                }

                return HookResult.Continue;
            }
        }

        return HookResult.Stop;
    }

    /// <summary>
    /// Shared implementation for validating a team-join request during an active match state
    /// (KnifeRound, Match, PickingStartingSide, Timeout) for either CT or T.
    /// Handles re-joins (existing listed players OR reserved SteamIDs) and caps capacity at
    /// <c>max(listCount, actualCount)</c> so the engine's live team never exceeds the configured limit.
    /// </summary>
    private HookResult HandleActiveMatchJoin(IPlayer player, Team team, List<IPlayer> playingList, HashSet<ulong> reservedSlots, string fullErrorKey)
    {
        int maxTeamSize = cfg.MinimumReadyPlayers / 2;

        bool isInPlayingList = playingList.Any(p => p.SteamID == player.SteamID);
        bool hasReservation = reservedSlots.Contains(player.SteamID);

        int listCount = playingList.Count;
        int actualCount = GetPlayersInTeam(team).Count;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleActiveMatchJoin - {Team}: list {ListCount}/{Max}, actual {Actual}, inList={InList}, reserved={Reserved}",
                team, listCount, maxTeamSize, actualCount, isInPlayingList, hasReservation);

        // Rejoin path: already in the playing list OR has a SteamID-based reservation.
        if (isInPlayingList || hasReservation)
        {
            // If the player is not currently on this team and the engine team is already at capacity,
            // reject. This covers the window where an untracked reconnect is still physically occupying
            // a seat (ScheduleForceToSpectator hasn't yet moved them off) - allowing the list-owner to
            // rejoin here would push the physical team above the configured limit.
            var currentTeam = (Team)player.Controller.TeamNum;
            if (currentTeam != team && actualCount >= maxTeamSize)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} has slot but team is physically full (actual:{Actual}/{Max}) - rejecting.",
                        team, player.Controller.PlayerName, actualCount, maxTeamSize);
                PrintMessageToPlayer(player, Core.Localizer[fullErrorKey]);
                return HookResult.Stop;
            }

            // Refresh the IPlayer reference in the list (stale ref from before disconnect would have
            // different PlayerID/slot). This keeps list identity aligned with the current connected player.
            playingList.RemoveAll(p => p.SteamID == player.SteamID);
            playingList.Add(player);
            reservedSlots.Remove(player.SteamID);
            forcedToSpectator.Remove(player.SteamID);

            if (cfg.DetailedLogging)
                logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} re-joined.", team, player.Controller.PlayerName);

            Core.Scheduler.NextTick(() => FixTeammateColors());
            CheckAutoResetOnLeave();
            return HookResult.Continue;
        }

        // Untracked player. If prevention is enabled, always block.
        if (preventNotPickedPlayersFromJoiningOngoingMatch)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} blocked (prevention enabled).", team, player.Controller.PlayerName);
            PrintMessageToPlayer(player, Core.Localizer[fullErrorKey]);
            ScheduleForceToSpectator(player, fullErrorKey);
            return HookResult.Stop;
        }

        // Use the larger of list-count and actual engine-team count. This closes races where
        // engine-side placements during the isMovingPlayersToTeams window inflated the actual
        // team count above the tracked list count.
        int effectiveCount = Math.Max(listCount, actualCount);
        if (effectiveCount < maxTeamSize)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} joined (list:{List}, actual:{Actual}, max:{Max}).",
                    team, player.Controller.PlayerName, listCount, actualCount, maxTeamSize);
            playingList.Add(player);
            forcedToSpectator.Remove(player.SteamID);
            Core.Scheduler.NextTick(() => FixTeammateColors());
            CheckAutoResetOnLeave();
            return HookResult.Continue;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} blocked - team full (list:{List}, actual:{Actual}, max:{Max}).",
                team, player.Controller.PlayerName, listCount, actualCount, maxTeamSize);
        PrintMessageToPlayer(player, Core.Localizer[fullErrorKey]);
        ScheduleForceToSpectator(player, fullErrorKey);
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
            if (weapon == DamageTypes_t.DMG_BULLET || weapon == DamageTypes_t.DMG_SLASH || weapon == DamageTypes_t.DMG_SHOCK)
            {
                if(cfg.DetailedLogging)
                    logger.LogInformation("HandleTakeDamage: Friendly fire or knife slash detected, skipping.");

                @event.Info.Damage = 0;
            }
        }
    }
}
