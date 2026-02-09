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
    }

    /// <summary>
    /// Handles the event when a client is put into the server.
    /// </summary>
    internal void HandleClientPutInServer(IOnClientPutInServerEvent clientKind)
    {
        var playerSlot = clientKind.PlayerId;

        if (cfg.DetailedLogging)
            logger.LogInformation($"HandleClientPutInServer: Slot {playerSlot}");

        if (!freshlyJoinedPlayers.Any(p => p == playerSlot))
        {
            freshlyJoinedPlayers.Add(playerSlot);
        }
        if (cfg.DetailedLogging)
            logger.LogInformation($"HandleClientPutInServer: Added player slot {playerSlot} to freshlyJoinedPlayers.");

        try
        {
            var player = Core.PlayerManager.GetPlayer(playerSlot);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleClientPutInServer: Retrieved player from slot {playerSlot}.");
            if (player != null && player.IsValid)
            {
                if (MatchState != MatchState.Warmup && MatchState != MatchState.MapVoting && MatchState != MatchState.MapChosen && MatchState != MatchState.PickingTeam)
                {
                    if (preventNotPickedPlayersFromJoiningOngoingMatch)
                    {
                        if (playingCtPlayers.Any(p => p.PlayerID == player.PlayerID) || playingTPlayers.Any(p => p.PlayerID == player.PlayerID) || pickedCtPlayers.Any(p => p.PlayerID == player.PlayerID) || pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation($"HandlePlayerJoinTeam: Player {player.Controller.PlayerName} is already in a team, allowing.");
                        }
                        else
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation($"HandlePlayerJoinTeam: Player {player.Controller.PlayerName} attempted to join a team but is not in playingCtPlayers or playingTPlayers or pickedCtPlayers or pickedTPlayers, kicking.");
                            KickPlayer(player.SteamID, Core.Localizer["info.kick_reason.not_picked"]);
                        }
                    }
                }

                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandleClientPutInServer: Moving player slot {playerSlot} to Spectator team.");
                
                Core.Scheduler.DelayBySeconds(2, () =>
                {
                    var delayedPlayer = Core.PlayerManager.GetPlayer(playerSlot);
                    if (delayedPlayer != null && delayedPlayer.IsValid)
                    {
                        HandlePlayerChangeTeam(delayedPlayer, 0);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"HandleClientPutInServer: Error moving player slot {playerSlot} to Spectator team.");
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
            logger.LogInformation($"OnClientCommand: {player.Controller.PlayerName} executing jointeam command with team {teamTojoin}");

        if (teamTojoin == 9)
        {
            logger.LogError($"HandleJointeamListener: {player.Controller.PlayerName} tried to join, but selected team was not found in command: {commandLine}");
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
        if (!freshlyJoinedPlayers.Any(p => p == playerSlot))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandlePlayerChangeTeamOnJoin: Player {player.Controller.PlayerName} is not in freshlyJoinedPlayers, ignoring.");
            return;
        }

        freshlyJoinedPlayers.Remove(playerSlot);

        if (cfg.DetailedLogging)
            logger.LogInformation($"HandlePlayerChangeTeamOnJoin: Player {player.Controller.PlayerName} has joined the server.");

        if (player.IsValid && !IsBot(player))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandlePlayerChangeTeamOnJoin: Player {player.Controller.PlayerName} is valid.");

            var ctPlayers = GetPlayersInTeam(Team.CT);
            var tPlayers = GetPlayersInTeam(Team.T);

            if (ctPlayers.Count > tPlayers.Count)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandlePlayerChangeTeamOnJoin: joining Terrorists");
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
                    logger.LogInformation($"HandlePlayerChangeTeamOnJoin: joining CounterTerrorists");
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
                        logger.LogInformation($"HandlePlayerChangeTeamOnJoin: both teams equal, joining CounterTerrorists");
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
                        logger.LogInformation($"HandlePlayerChangeTeamOnJoin: both teams equal, joining Terrorists");
                    previousAutoJoinedTeam = Team.T;
                    Core.Scheduler.DelayBySeconds(2, async () =>
                    {
                        await player.SwitchTeamAsync(Team.T);
                        Core.Scheduler.NextTick(() => RespawnPlayer(player));
                    });
                }
            }
        }
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
            logger.LogInformation($"OnPlayerDisconnect: slot {slot}");
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

        if (pickedCtPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            pickedCtPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleDisconnectedPlayer: Removed {player.Controller.PlayerName} from pickedCtPlayers.");
        }

        if (pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            pickedTPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleDisconnectedPlayer: Removed {player.Controller.PlayerName} from pickedTPlayers.");
        }

        if (playingCtPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            playingCtPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleDisconnectedPlayer: Removed {player.Controller.PlayerName} from playingCtPlayers.");
        }

        if (playingTPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            playingTPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleDisconnectedPlayer: Removed {player.Controller.PlayerName} from playingTPlayers.");
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

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
                    logger.LogInformation($"HandleDisconnectedPlayer: MatchState is {matchState}");
                if (player.PlayerID == captainCt?.PlayerID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandleDisconnectedPlayer: Disconnected player is CT captain");

                    captainCt = null;
                    var newCaptain = playingCtPlayers.Where(p => p.PlayerID != player.PlayerID).FirstOrDefault();

                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandleDisconnectedPlayer: New CT captain is {newCaptain?.Controller.PlayerName}");

                    PickCtCaptain(newCaptain);
                }
                if (player.PlayerID == captainT?.PlayerID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandleDisconnectedPlayer: Disconnected player is T captain");

                    captainT = null;
                    var newCaptain = playingTPlayers.Where(p => p.PlayerID != player.PlayerID).FirstOrDefault();

                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandleDisconnectedPlayer: New T captain is {newCaptain?.Controller.PlayerName}");
                    PickTCaptain(newCaptain);
                }
            }
        }

        if (matchState == MatchState.PickingStartingSide
            || matchState == MatchState.Timeout)
        {
            if (!cfg.DisableCaptains && player.PlayerID == winnerCaptain?.PlayerID)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandleDisconnectedPlayer: Disconnected player is winner captain");
                StayStartingSides(winnerCaptain);
            }
        }

        if (readyPlayers.Any(p => p.PlayerID == player.PlayerID))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleDisconnectedPlayer: Removing {player.Controller.PlayerName} from readyPlayers.");
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
            logger.LogError(ex, $"HandleDisconnectedPlayer: Error closing active menu for captain \"{player?.Controller.PlayerName}\"");
        }

        CheckReadyPlayersToStart();
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
            logger.LogInformation($"HandlePlayerChangeTeam: Called for player {player?.Controller.PlayerName} (slot {player?.Slot}), teamTojoin={teamTojoin}");


        if (player == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("HandlePlayerChangeTeam: player is null");
            return HookResult.Stop;
        }

        if (player.IsFakeClient)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandlePlayerChangeTeam: {player.Controller.PlayerName} is a fake client, allowing");
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
                logger.LogInformation($"HandlePlayerChangeTeam: {player.Controller.PlayerName} is a bot, allowing");
            return HookResult.Continue;
        }

        // Skip validation during programmatic team moves (switch/stay sides, etc.)
        if (isMovingPlayersToTeams)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandlePlayerChangeTeam: Skipping validation during team move for {player.Controller.PlayerName}");
            return HookResult.Continue;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (cfg.DetailedLogging)
            logger.LogInformation($"HandlePlayerChangeTeam: Current match state is {matchState}");

        if (matchState == MatchState.Warmup ||
            matchState == MatchState.MapVoting ||
            matchState == MatchState.MapChosen)
        {
            bool isInFreshlyJoined = freshlyJoinedPlayers.Any(p => p == player.Slot);
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandlePlayerChangeTeam: Player {player.Controller.PlayerName} in freshlyJoinedPlayers: {isInFreshlyJoined}");

            if (isInFreshlyJoined)
            {
                HandlePlayerChangeTeamOnJoin(player);
                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandlePlayerChangeTeam: Match state {matchState}. {player.Controller.PlayerName} joined team {teamTojoin}");
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandlePlayerChangeTeam: Match state {matchState}. {player.Controller.PlayerName} not in freshlyJoinedPlayers, allowing team change to {teamTojoin}");
            }

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
                        logger.LogInformation($"HandlePlayerJoinTeam - PickingTeam: Player {player.Controller.PlayerName} re-joined CT team.");
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - PickingTeam: Player {player.Controller.PlayerName} attempted to join CT team without being picked.");
                    PrintMessageToPlayer(player, Core.Localizer["error.team.join_denied.ct"]);
                    return HookResult.Stop;
                }
            }
            if (teamTojoin == 2)
            {
                if (pickedTPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - PickingTeam: Player {player.Controller.PlayerName} re-joined T team.");
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - PickingTeam: Player {player.Controller.PlayerName} attempted to join T team without being picked.");
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
            if (teamTojoin == 3)
            {
                if (playingCtPlayers.Count < cfg.MinimumReadyPlayers / 2)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} joined CT team.");
                    playingCtPlayers.Add(player);
                    return HookResult.Continue;
                }

                if (playingCtPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} re-joined CT team.");
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} attempted to join CT team but it is full.");
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.ct"]);
                    return HookResult.Stop;
                }
            }

            if (teamTojoin == 2)
            {
                if (playingTPlayers.Count < cfg.MinimumReadyPlayers / 2)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} joined T team.");
                    playingTPlayers.Add(player);
                    return HookResult.Continue;
                }

                if (playingTPlayers.Any(p => p.PlayerID == player.PlayerID))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} re-joined T team.");
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} attempted to join T team but it is full.");
                    PrintMessageToPlayer(player, Core.Localizer["error.team.full.t"]);
                    return HookResult.Stop;
                }
            }

            if (teamTojoin == 0 || teamTojoin == 1)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"HandlePlayerJoinTeam - Match: Player {player.Controller.PlayerName} joined Spectators.");
                playingCtPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
                playingTPlayers.RemoveAll(p => p.PlayerID == player.PlayerID);
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
