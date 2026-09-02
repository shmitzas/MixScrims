using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.GameHooks;
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
        Core.GameHooks.Entities.TakeDamage.Pre += HandleTakeDamage;
    }

    /// <summary>
    /// Reconciles deferred-reset state on every map load. If MixScrims itself drove
    /// the map change (state == MapLoading) the flag is just cleared, since the
    /// MapChosen path will restore the prior phase. If an external actor (e.g. another
    /// plugin issuing changelevel/host_workshop_map while the server was empty) caused
    /// the map load while the flag was set, run ResetPluginState now so warmup.cfg is
    /// applied on the new map even before any player connects — otherwise CS2 starts
    /// with default cvars and the next-join hook can no-op against drifted state.
    /// </summary>
    internal void HandleStateAgnosticMapLoad(IOnMapLoadEvent @event)
    {
        // Compute plugin-driven vs external once so both the warmup-cvar-dirty marker
        // (below) and the resetMixOnFirstJoin reconciliation (further down) share the
        // same classification. MixScrims-driven changes can land here in either
        // MapLoading (if this handler runs before HandleMapChosenNewMapLoad) or MapChosen
        // (if it runs after, since the MapChosen handler promotes state on match-flow
        // loads). Any other state is an external map change.
        var currentState = mixScrimsService.GetCurrentMatchState();
        var isPluginDriven = currentState == MatchState.MapLoading || currentState == MatchState.MapChosen;

        if (!isPluginDriven)
        {
            // External map load (another plugin issued changelevel / host_workshop_map,
            // or the RCON/console did). Warmup cvars have reset to CS2 defaults on the
            // new map — mark them dirty so the next OnClientPutInServer during Warmup
            // re-execs warmup.cfg. Plugin-driven loads reach StartWarmup() on the new
            // map through their own state machine and will clear the flag via
            // LoadWarmupConfig(), so no set is needed for the plugin-driven branch.
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleStateAgnosticMapLoad: External map load detected (state={State}); marking warmup cvars dirty.", currentState);
            warmupCvarsDirty = true;
        }

        if (resetMixOnFirstJoin)
        {
            if (isPluginDriven)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleStateAgnosticMapLoad: Clearing resetMixOnFirstJoin flag — MixScrims-driven map change in progress (state={State}).", currentState);
                resetMixOnFirstJoin = false;
            }
            else
            {
                logger.LogInformation("HandleStateAgnosticMapLoad: External map change with resetMixOnFirstJoin set (state={State}) — running ResetPluginState now.", currentState);
                resetMixOnFirstJoin = false;
                ResetPluginState();
            }
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

        // Defensive re-apply of warmup config when cvars have been marked dirty by an
        // upstream event (external map load in HandleStateAgnosticMapLoad, or any reset
        // path via ResetVariables). Replaces the previous "humanCount <= 1" heuristic,
        // which incorrectly fired on every 1→2 join because the joining player is not
        // yet counted at OnClientPutInServer — teleporting existing warmup players back
        // to spawn every time a second player connected. warmupCvarsDirty is cleared by
        // LoadWarmupConfig() below, so this branch is self-quiescing.
        if (mixScrimsService.GetCurrentMatchState() == MatchState.Warmup && warmupCvarsDirty)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleClientPutInServer: Warmup cvars marked dirty — re-applying warmup config.");
            LoadWarmupConfig();
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
                    // Active match state. The only players allowed on a playing team are those
                    // in pickedCt/T (the static "who was picked into this match" snapshot, kept
                    // populated for the whole match). Everyone else is decided by the
                    // preventNotPickedPlayersFromJoiningOngoingMatch flag: on -> kick, off ->
                    // preemptively land on Spectator so the engine's auto-place onto a previous
                    // team doesn't produce a visible flash on a full side (the Post handler in
                    // HandleEventPlayerTeamPost is still the primary backstop).
                    bool isPicked = pickedCtPlayers.Any(p => SafeSteamId(p) == player.SteamID)
                                 || pickedTPlayers.Any(p => SafeSteamId(p) == player.SteamID);

                    if (preventNotPickedPlayersFromJoiningOngoingMatch)
                    {
                        if (isPicked)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandleClientPutInServer: {PlayerName} is picked, allowing.", player.Controller.PlayerName);
                        }
                        else
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("HandleClientPutInServer: {PlayerName} joined mid-match and is not picked, kicking.", player.Controller.PlayerName);
                            KickPlayer(player.SteamID, Core.Localizer["info.kick_reason.not_picked"]);
                            return;
                        }
                    }
                    else if (!isPicked)
                    {
                        if (cfg.DetailedLogging)
                            logger.LogInformation("HandleClientPutInServer: {PlayerName} is not picked during active match - forcing to Spectator.", player.Controller.PlayerName);

                        ScheduleForceToSpectator(player);
                        return;
                    }
                }

                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleClientPutInServer: Moving player slot {Slot} to Spectator team.", playerSlot);
                
                var specToken = Core.Scheduler.DelayBySeconds(2, () =>
                {
                    try
                    {
                        var delayedPlayer = Core.PlayerManager.GetPlayer(playerSlot);
                        if (delayedPlayer != null && delayedPlayer.IsValid)
                        {
                            var currentState = mixScrimsService.GetCurrentMatchState();
                            if (currentState == MatchState.Warmup || currentState == MatchState.MapVoting || currentState == MatchState.MapChosen)
                                HandlePlayerChangeTeam(delayedPlayer, 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "HandleClientPutInServer: deferred spectator move failed for slot {Slot}.", playerSlot);
                    }
                });
                Core.Scheduler.StopOnMapChange(specToken);
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
        ulong steamId = player.SteamID;
        var token = Core.Scheduler.DelayBySeconds(2, async () =>
        {
            try
            {
                // Revalidate by slot + SteamID to ensure the captured reference still refers
                // to the same physical player. After a map change the slot may belong to a
                // different player whose IsValid would still return true.
                var live = Core.PlayerManager.GetPlayer(playerSlot);
                if (live is null || !live.IsValid || live.SteamID != steamId)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("ScheduleAutoJoinTeamSwitch: player (slot {Slot}, steamId {SteamId}) no longer valid, skipping switch to {Team}.", playerSlot, steamId, targetTeam);
                    return;
                }

                await live.SwitchTeamAsync(targetTeam);

                Core.Scheduler.NextTick(() =>
                {
                    try
                    {
                        var live2 = Core.PlayerManager.GetPlayer(playerSlot);
                        if (live2 is null || !live2.IsValid || live2.SteamID != steamId)
                            return;
                        RespawnPlayer(live2);
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
        Core.Scheduler.StopOnMapChange(token);
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
        var recentlyDisconnectedToken = Core.Scheduler.DelayBySeconds(1, () => recentlyDisconnectedPlayers.Remove(disconnectingPlayerSlot));
        Core.Scheduler.StopOnMapChange(recentlyDisconnectedToken);

        // Defensive sweep: purge disposed IPlayer refs from every tracked roster before
        // running the per-player cleanup below. A previous disconnect that failed mid-cleanup
        // (or a mid-match dispose the plugin didn't catch) can otherwise leave ghost entries
        // that make later reads throw ObjectDisposedException from LINQ predicates - the exact
        // crash class the log showed at ForceReady.cs:69. SafeSteamId returns 0 on disposed
        // refs so the RemoveAll itself never throws on the entries it's removing.
        readyPlayers.RemoveAll(p => SafeSteamId(p) == 0);
        pickedCtPlayers.RemoveAll(p => SafeSteamId(p) == 0);
        pickedTPlayers.RemoveAll(p => SafeSteamId(p) == 0);
        playingCtPlayers.RemoveAll(p => SafeSteamId(p) == 0);
        playingTPlayers.RemoveAll(p => SafeSteamId(p) == 0);

        // Cache the disconnecting player's SteamID once. If the IPlayer is already disposed
        // by the time this handler runs, steamId will be 0 and the targeted per-player
        // removals below become no-ops - but the sweep above will already have taken care
        // of any ghost entries this player left behind.
        var steamId = SafeSteamId(player);

        // Cache player name for logging since Controller might become invalid during disconnect
        var playerName = IsPlayerValid(player) ? player.Controller.PlayerName : $"Player {player.PlayerID}";

        freshlyJoinedPlayers.Remove(player.Slot);
        HandlePlayerDisconnectRtv(steamId);
        forcedToSpectator.Remove(steamId);

        if (steamId != 0 && pickedCtPlayers.Any(p => SafeSteamId(p) == steamId))
        {
            pickedCtPlayers.RemoveAll(p => SafeSteamId(p) == steamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from pickedCtPlayers.", playerName);
        }

        if (steamId != 0 && pickedTPlayers.Any(p => SafeSteamId(p) == steamId))
        {
            pickedTPlayers.RemoveAll(p => SafeSteamId(p) == steamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from pickedTPlayers.", playerName);
        }

        if (steamId != 0 && playingCtPlayers.Any(p => SafeSteamId(p) == steamId))
        {
            playingCtPlayers.RemoveAll(p => SafeSteamId(p) == steamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from playingCtPlayers.", playerName);
        }

        if (steamId != 0 && playingTPlayers.Any(p => SafeSteamId(p) == steamId))
        {
            playingTPlayers.RemoveAll(p => SafeSteamId(p) == steamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleDisconnectedPlayer: Removed {PlayerName} from playingTPlayers.", playerName);
        }

        playerColors.Remove(player.PlayerID);

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.PickingTeam)
        {
            if (!cfg.DisableCaptains)
            {
                if (player.SteamID == captainCt?.SteamID)
                {
                    AssignCaptain(Team.CT, null);
                    StartTeamPickingPhase();
                }
                if (player.SteamID == captainT?.SteamID)
                {
                    AssignCaptain(Team.T, null);
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
                if (player.SteamID == captainCt?.SteamID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is CT captain");

                    AssignCaptain(Team.CT, null);
                    // steamId is the disconnecting player's ID; use SafeSteamId on roster entries
                    // to skip disposed ghosts when picking the successor captain.
                    var newCaptain = playingCtPlayers.Where(p => SafeSteamId(p) != steamId).FirstOrDefault();

                    if (cfg.DetailedLogging)
                    {
                        var newCaptainName = newCaptain != null && IsPlayerValid(newCaptain) 
                            ? newCaptain.Controller.PlayerName 
                            : "None";
                        logger.LogInformation("HandleDisconnectedPlayer: New CT captain is {NewCaptain}", newCaptainName);
                    }

                    PickCtCaptain(newCaptain);
                }
                if (player.SteamID == captainT?.SteamID)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is T captain");

                    AssignCaptain(Team.T, null);
                    var newCaptain = playingTPlayers.Where(p => SafeSteamId(p) != steamId).FirstOrDefault();

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
            if (!cfg.DisableCaptains && player.SteamID == winnerCaptain?.SteamID)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleDisconnectedPlayer: Disconnected player is winner captain");
                StayStartingSides(winnerCaptain);
            }
        }

        if (steamId != 0 && readyPlayers.Any(p => SafeSteamId(p) == steamId))
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
        // EventPlayerTeam payload carries both OldTeam (pre-swap) and Team (new). Threading
        // OldTeam here removes the adopt block's dependence on engine timing for
        // player.Controller.TeamNum - the payload value is authoritative regardless of when
        // Source2 updates the schema field. Sibling plugins (BotsManager, K4-Arenas) use the
        // same payload shape.
        int preSwapTeam = @event.OldTeam;

        return HandlePlayerChangeTeam(player, teamTojoin, preSwapTeam);
    }

    /// <summary>
    /// Post-mode reconciler: after a team change has actually been committed by the engine, do
    /// two things:
    /// (1) Overflow demote - if the just-committed player is on CT or T but is NOT in that team's
    ///     plugin roster, force them back to Spectator via <see cref="ScheduleForceToSpectator"/>.
    ///     This catches CS2's silent restore-at-round-start where a specced player is auto-placed
    ///     onto their old team without going through <see cref="HandlePlayerChangeTeam"/>. Fires
    ///     regardless of <see cref="preventNotPickedPlayersFromJoiningOngoingMatch"/> as
    ///     defense-in-depth against that bypass. The deferred-spec-then-fresh-join scenario is
    ///     unaffected because the fresh joiner is added to the roster by
    ///     <see cref="HandleActiveMatchJoin"/>'s Pre hook before this Post hook runs.
    /// (2) Prune - if a tracked player has moved off their tracked team (e.g. voluntary
    ///     Spectator move, or CT-&gt;T switch), remove them from the old team's roster. Only
    ///     runs when prevention is off - with prevention on, reservations are intentionally held
    ///     for the original occupant.
    /// Both branches are skipped while <see cref="isMovingPlayersToTeams"/> is set (halftime
    /// side-swap / knife-&gt;match / stay-or-switch), because those programmatic swaps commit
    /// team changes for tracked players without going through <see cref="HandleActiveMatchJoin"/>.
    /// The <see cref="ScheduleForceToSpectator"/> recursion is short-circuited because its own
    /// <c>SwitchTeamAsync(Spectator)</c> commit hits this hook with <c>committedTeam=Spectator</c>,
    /// which is neither CT nor T.
    /// </summary>
    [GameEventHandler(HookMode.Post)]
    public HookResult HandleEventPlayerTeamPost(EventPlayerTeam @event)
    {
        var player = @event.UserIdPlayer;
        if (player == null || !IsPlayerValid(player) || IsBot(player) || player.IsFakeClient)
            return HookResult.Continue;

        // Guard against connecting/uninitialised slots whose SteamID hasn't landed yet -
        // acting on SteamID=0 would either dedup incorrectly in forcedToSpectator or match
        // stale ghost entries in the rosters.
        var playerSteamId = player.SteamID;
        if (playerSteamId == 0)
            return HookResult.Continue;

        var matchState = mixScrimsService.GetCurrentMatchState();
        bool isActive = matchState == MatchState.KnifeRound
                      || matchState == MatchState.Match
                      || matchState == MatchState.PickingStartingSide
                      || matchState == MatchState.Timeout;
        if (!isActive)
            return HookResult.Continue;

        // While the plugin is performing a programmatic team move (side-swap at halftime
        // or OT period boundaries, knife->match transition, stay/switch sides) we MUST NOT
        // demote or prune. The pre-handler bypasses tracked players with Continue without
        // re-adding to the new list; running either branch here would either dump the whole
        // roster to spec or leave them untracked until ResyncPlayingListsFromEngine runs
        // (1s after EventRoundStart in Match/Events.cs) - the exact "OT side switch dumps
        // half the players to spec" symptom this file already guards against.
        if (isMovingPlayersToTeams)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleEventPlayerTeamPost: skipping reconcile for {PlayerName} during programmatic move (committed team {Team}).",
                    player.Controller.PlayerName, @event.Team);
            return HookResult.Continue;
        }

        // Committed team for this player. Use event value (Team=new) which is authoritative here.
        int committedTeam = @event.Team;

        // Overflow demote: player physically landed on CT or T but is not in that team's roster.
        // This catches CS2 auto-restoring a specced/reconnecting player back onto their old team
        // without firing HandlePlayerChangeTeam - the Pre-hook capacity gate never runs, so the
        // physical team can exceed the plugin's picked roster. Force them back to Spectator.
        // Runs even when prevention is on: HandleActiveMatchJoin blocks the gated path there,
        // but a silent-restore bypasses that gate entirely.
        if (committedTeam == (int)Team.CT || committedTeam == (int)Team.T)
        {
            var roster = committedTeam == (int)Team.CT ? playingCtPlayers : playingTPlayers;
            if (!roster.Any(p => SafeSteamId(p) == playerSteamId))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleEventPlayerTeamPost: {PlayerName} committed to {Team} but not in roster - forcing to Spectator.",
                        player.Controller.PlayerName, (Team)committedTeam);
                ScheduleForceToSpectator(player, "error.team.slot_unavailable");
                return HookResult.Continue;
            }
        }

        // Prune: reservations are intentionally held when prevention is on, so tracked players
        // that moved off their tracked team stay in the list.
        if (preventNotPickedPlayersFromJoiningOngoingMatch)
            return HookResult.Continue;

        // If the player is no longer on CT but is still in playingCtPlayers, prune.
        if (committedTeam != (int)Team.CT && playingCtPlayers.Any(p => SafeSteamId(p) == playerSteamId))
        {
            playingCtPlayers.RemoveAll(p => SafeSteamId(p) == playerSteamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleEventPlayerTeamPost: Pruned {PlayerName} from playingCtPlayers (committed team {Team}).", player.Controller.PlayerName, committedTeam);
        }

        // If the player is no longer on T but is still in playingTPlayers, prune.
        if (committedTeam != (int)Team.T && playingTPlayers.Any(p => SafeSteamId(p) == playerSteamId))
        {
            playingTPlayers.RemoveAll(p => SafeSteamId(p) == playerSteamId);
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleEventPlayerTeamPost: Pruned {PlayerName} from playingTPlayers (committed team {Team}).", player.Controller.PlayerName, committedTeam);
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Handles a player's request to change teams during a match, enforcing team selection rules based on the current
    /// match state.
    /// </summary>
    /// <param name="preSwapTeam">
    /// Authoritative pre-swap team from <c>EventPlayerTeam.OldTeam</c> when this call originates
    /// from the game-event Pre hook. Pass -1 (default) when unavailable (jointeam console command,
    /// deferred spec move); the silent-restore adopt block will then fall back to reading
    /// <c>player.Controller.TeamNum</c>.
    /// </param>
    public HookResult HandlePlayerChangeTeam(IPlayer? player, int teamTojoin, int preSwapTeam = -1)
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerChangeTeam: Called for player {PlayerName} (slot {Slot}), teamTojoin={Team}", player?.Controller.PlayerName, player?.Slot, teamTojoin);


        if (player == null)
        {
            logger.LogWarning("HandlePlayerChangeTeam: player is null, stopping jointeam handling.");
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
            logger.LogWarning("HandlePlayerChangeTeam: player {Slot} is not valid, stopping jointeam handling.", player.Slot);
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
            // Cache the live event player's SteamID once and use SafeSteamId on roster
            // entries; the picked/playing lists may carry disposed IPlayer refs whose raw
            // .SteamID read throws ObjectDisposedException per iteration.
            var moveSteamId = player.SteamID;
            bool isTracked = playingCtPlayers.Any(p => SafeSteamId(p) == moveSteamId)
                          || playingTPlayers.Any(p => SafeSteamId(p) == moveSteamId)
                          || pickedCtPlayers.Any(p => SafeSteamId(p) == moveSteamId)
                          || pickedTPlayers.Any(p => SafeSteamId(p) == moveSteamId);

            if (isTracked)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: Skipping validation during team move for tracked {PlayerName}", player.Controller.PlayerName);
                return HookResult.Continue;
            }

            // Silent-restore rescue: CS2 auto-restores a mid-match reconnecter's previous team
            // without firing EventPlayerTeam through our validation path, so their SteamID never
            // lands in playingCtPlayers/playingTPlayers. When the engine's halftime swap later
            // fires EventPlayerTeam for them, this handler sees them as untracked and the
            // untracked branch below rejects the swap + ScheduleForceToSpectator kicks in - the
            // exact "halftime dumps late joiners to spec" symptom. Adopt them into the playing
            // list of their pre-swap engine team so the swap goes through, then leave it to
            // ResyncPlayingListsFromEngine (Match/Main.cs, called from HandleRoundStart+1s in
            // Match/Events.cs) to move them to the correct post-swap side by SteamID reconciliation.
            //
            // Only adopt for halftime / side-pick moves (teamTojoin is a playing team). When
            // teamTojoin is Spec, this is a deliberate spec-force move (e.g. from
            // ScheduleForceToSpectator's SwitchTeamAsync(Team.Spectator) call) - adopting would
            // create a stale playing-list entry AND cause the ScheduleForceToSpectator retry to
            // see the player as tracked (via IsPlayerTrackedForActiveMatch) and exit early,
            // leaving the player on Spec while the plugin still lists them on a playing team.
            // Let those moves fall through to the normal validation path, which correctly
            // removes stale entries via the Spectator branch of HandlePlayerJoinTeam.
            //
            // Prefer the payload's OldTeam (threaded from HandleEventPlayerTeam) over reading
            // player.Controller.TeamNum, since the schema field's update timing relative to the
            // Pre hook is engine-version dependent while the payload value is not.
            int adoptTeam = preSwapTeam;
            if (adoptTeam < 0)
            {
                try
                {
                    if (player.Controller != null)
                        adoptTeam = player.Controller.TeamNum;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "HandlePlayerChangeTeam: failed reading pre-swap team for {PlayerName} during adopt check.", SafePlayerName(player));
                }
            }

            if (teamTojoin == (int)Team.CT || teamTojoin == (int)Team.T)
            {
                if (adoptTeam == (int)Team.CT)
                {
                    playingCtPlayers.RemoveAll(p => SafeSteamId(p) == moveSteamId);
                    playingCtPlayers.Add(player);
                    logger.LogInformation("HandlePlayerChangeTeam: Adopted untracked {PlayerName} into playingCtPlayers (pre-swap team CT) during programmatic move - likely a silent CS2 team restore on reconnect.", SafePlayerName(player));
                    return HookResult.Continue;
                }
                if (adoptTeam == (int)Team.T)
                {
                    playingTPlayers.RemoveAll(p => SafeSteamId(p) == moveSteamId);
                    playingTPlayers.Add(player);
                    logger.LogInformation("HandlePlayerChangeTeam: Adopted untracked {PlayerName} into playingTPlayers (pre-swap team T) during programmatic move - likely a silent CS2 team restore on reconnect.", SafePlayerName(player));
                    return HookResult.Continue;
                }
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerChangeTeam: Programmatic move active but {PlayerName} is untracked (pre-swap team {PreSwap}, target team {Target}) - validating normally", SafePlayerName(player), adoptTeam, teamTojoin);
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
                logger.LogInformation("HandlePlayerChangeTeam: Player {PlayerName} in freshlyJoinedPlayers: {InList}", SafePlayerName(player), isInFreshlyJoined);

            if (isInFreshlyJoined)
            {
                HandlePlayerChangeTeamOnJoin(player);
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: {MatchState}. {PlayerName} joined team {Team}", matchState, SafePlayerName(player), teamTojoin);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerChangeTeam: {MatchState}. {PlayerName} not freshly joined, allowing change to {Team}", matchState, SafePlayerName(player), teamTojoin);
            }

            if (cfg.ShowReadyStatusInScoreboard)
                SetPlayerReadyStatusInScoreboard(player, false);
            return HookResult.Continue;
        }

        if (matchState == MatchState.KnifeRound)
        {
            if (!cfg.DisableCaptains && (player.SteamID == captainCt?.SteamID || player.SteamID == captainT?.SteamID))
            {
                PrintMessageToPlayer(player, Core.Localizer["error.captain.cannot_change_team"]);
                return HookResult.Stop;
            }
        }

        if (matchState == MatchState.PickingTeam)
        {
            // Cache the live event player's SteamID once and use SafeSteamId on picked lists
            // to keep the .Any checks safe against disposed IPlayer refs (same crash class as
            // ForceReady.cs:69).
            var pickSteamId = player.SteamID;
            if (teamTojoin == 3)
            {
                if (pickedCtPlayers.Any(p => SafeSteamId(p) == pickSteamId))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} re-joined CT team.", SafePlayerName(player));
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} attempted to join CT without being picked.", SafePlayerName(player));
                    PrintMessageToPlayer(player, Core.Localizer["error.team.join_denied.ct"]);
                    return HookResult.Stop;
                }
            }
            if (teamTojoin == 2)
            {
                if (pickedTPlayers.Any(p => SafeSteamId(p) == pickSteamId))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} re-joined T team.", SafePlayerName(player));
                    return HookResult.Continue;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - PickingTeam: Player {PlayerName} attempted to join T without being picked.", SafePlayerName(player));
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
                // Cache live event player SteamID once and use SafeSteamId on playing lists
                // (may hold disposed refs).
                var autoSteamId = player.SteamID;
                bool isInCtTeam = playingCtPlayers.Any(p => SafeSteamId(p) == autoSteamId);
                bool isInTTeam = playingTPlayers.Any(p => SafeSteamId(p) == autoSteamId);

                if (isInCtTeam)
                {
                    // Player is in CT team, treat auto-select as CT team selection
                    teamTojoin = 3;
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select, converting to CT (3).", SafePlayerName(player));
                }
                else if (isInTTeam)
                {
                    // Player is in T team, treat auto-select as T team selection
                    teamTojoin = 2;
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select, converting to T (2).", SafePlayerName(player));
                }
                else
                {
                    // Player is not in any team list - block the attempt
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} used auto-select but is not in any team list. Blocking.", SafePlayerName(player));
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
                    "error.team.full.ct");
            }

            if (teamTojoin == 2)
            {
                return HandleActiveMatchJoin(
                    player,
                    Team.T,
                    playingTPlayers,
                    "error.team.full.t");
            }

            if (teamTojoin == 1)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandlePlayerJoinTeam - Match: {PlayerName} joined Spectators.", SafePlayerName(player));

                // Voluntary move to Spectator fully releases the slot so any other connected
                // player can take it. Returning to a team uses the normal capacity-checked join
                // path. Any stale forced-spectator marker for this SteamID is also cleared so it
                // cannot block their own rejoin later.
                // Cache live SteamID once; SafeSteamId on roster removals to skip disposed ghosts.
                var specSteamId = player.SteamID;
                playingCtPlayers.RemoveAll(p => SafeSteamId(p) == specSteamId);
                playingTPlayers.RemoveAll(p => SafeSteamId(p) == specSteamId);
                forcedToSpectator.Remove(specSteamId);

                return HookResult.Continue;
            }
        }

        return HookResult.Stop;
    }

    /// <summary>
    /// Shared implementation for validating a team-join request during an active match state
    /// (KnifeRound, Match, PickingStartingSide, Timeout) for either CT or T. Handles re-joins
    /// (existing listed players) and caps capacity at
    /// <c>min(listCount, actualCount) &lt; MinimumReadyPlayers/2</c> - the join is admitted
    /// whenever EITHER the plugin roster OR the physical team has room. This lets an
    /// untracked spec player fill a vacated slot as soon as the scoreboard shows the team
    /// as understaffed (disconnected picked player, roster ghost that outlived cleanup,
    /// picked player who self-spec'd), instead of stranding them behind a stale roster
    /// count. Two mismatched-source hazards are handled explicitly:
    ///   1. <c>actualCount</c> from <see cref="GetPlayersInTeam"/> reads
    ///      <c>PlayerPawn.TeamNum</c>, which CS2 defers for alive players (self-spec via
    ///      <c>jointeam 1</c> keeps the pawn on the old team until round transition, death,
    ///      or disconnect). Using <c>min</c> lets a fresher <c>listCount</c> unblock the
    ///      join in that window.
    ///   2. Silent-restore reconnects add a physical player without hitting this method, so
    ///      <c>actualCount</c> can exceed <c>listCount</c>. Using <c>min</c> avoids blocking
    ///      a legitimate replacement in that window; <see cref="HandleEventPlayerTeamPost"/>
    ///      still demotes the untracked physical join.
    /// </summary>
    private HookResult HandleActiveMatchJoin(IPlayer player, Team team, List<IPlayer> playingList, string fullErrorKey)
    {
        int maxTeamSize = cfg.MinimumReadyPlayers / 2;

        // Cache the live event player's SteamID once and use SafeSteamId on the playing list
        // (may hold disposed IPlayer refs).
        var joinSteamId = player.SteamID;
        bool isInPlayingList = playingList.Any(p => SafeSteamId(p) == joinSteamId);

        int listCount = playingList.Count;
        int actualCount = GetPlayersInTeam(team).Count;
        // The gate: allow when EITHER count reads under-cap (see class remarks).
        int effectiveCount = Math.Min(listCount, actualCount);

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleActiveMatchJoin - {Team}: list {ListCount}/{Max}, actual {Actual}, effective {Effective}, inList={InList}",
                team, listCount, maxTeamSize, actualCount, effectiveCount, isInPlayingList);

        // Rejoin path: already in the playing list. Physical team may lag due to CS2's
        // deferred team-switch for alive players (a departing self-spec's pawn still occupies
        // the seat until round transition / death / disconnect). Roster is authoritative -
        // always admit the list owner.
        if (isInPlayingList)
        {
            // Refresh the IPlayer reference in the list (stale ref from before disconnect would have
            // different PlayerID/slot). This keeps list identity aligned with the current connected player.
            playingList.RemoveAll(p => SafeSteamId(p) == joinSteamId);
            playingList.Add(player);
            forcedToSpectator.Remove(joinSteamId);

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

        // Capacity gate: uses min(listCount, actualCount) so either a fresh roster prune OR
        // a shrunken physical team unblocks the join. HandlePlayerChangeTeam runs synchronously
        // per event via the Pre hook, so two concurrent joiners cannot both pass - the first
        // mutates listCount before the second's Pre hook reads it. See class remarks above for
        // the pawn-vs-roster mismatch cases this deliberately handles.
        if (effectiveCount < maxTeamSize)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} joined (list:{List}, actual:{Actual}, effective:{Effective}, max:{Max}).",
                    team, player.Controller.PlayerName, listCount, actualCount, effectiveCount, maxTeamSize);
            playingList.Add(player);
            forcedToSpectator.Remove(player.SteamID);
            Core.Scheduler.NextTick(() => FixTeammateColors());
            CheckAutoResetOnLeave();
            SchedulePostJoinOverflowCheck(player, team, playingList, maxTeamSize);
            return HookResult.Continue;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleActiveMatchJoin - {Team}: {PlayerName} blocked - team full (list:{List}, actual:{Actual}, effective:{Effective}, max:{Max}).",
                team, player.Controller.PlayerName, listCount, actualCount, effectiveCount, maxTeamSize);
        PrintMessageToPlayer(player, Core.Localizer[fullErrorKey]);
        ScheduleForceToSpectator(player, fullErrorKey);
        return HookResult.Stop;
    }

    /// <summary>
    /// Belt-and-suspenders check fired 0.5s after <see cref="HandleActiveMatchJoin"/> admits a
    /// join. The pre-hook gate uses <c>min(listCount, actualCount)</c>, which can under-count in
    /// specific race windows (pawn TeamNum not yet updated for a self-spec'd tracked player, or
    /// a silent-restore reconnect landing between the read and the commit). If the physical team
    /// is over cap 0.5s later, revert the player we just admitted - they're the most recent
    /// joiner via this path - by pruning them from the roster and force-moving them to Spectator.
    /// </summary>
    private void SchedulePostJoinOverflowCheck(IPlayer player, Team team, List<IPlayer> playingList, int maxTeamSize)
    {
        int playerSlot = player.Slot;
        ulong playerSteamId = player.SteamID;
        if (playerSteamId == 0UL)
            return;

        var token = Core.Scheduler.DelayBySeconds(0.5f, () =>
        {
            try
            {
                var live = Core.PlayerManager.GetPlayer(playerSlot);
                if (live is null || !live.IsValid || live.SteamID != playerSteamId)
                    return;

                var state = mixScrimsService.GetCurrentMatchState();
                bool isActive = state == MatchState.KnifeRound
                              || state == MatchState.Match
                              || state == MatchState.PickingStartingSide
                              || state == MatchState.Timeout;
                if (!isActive)
                    return;

                int physicalCount = GetPlayersInTeam(team).Count;
                if (physicalCount <= maxTeamSize)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("SchedulePostJoinOverflowCheck - {Team}: within cap ({Count}/{Max}) for {PlayerName}, no action.",
                            team, physicalCount, maxTeamSize, SafePlayerName(live));
                    return;
                }

                logger.LogInformation("SchedulePostJoinOverflowCheck - {Team}: over cap ({Count}/{Max}) after {PlayerName} joined - reverting most recent joiner to Spectator.",
                    team, physicalCount, maxTeamSize, SafePlayerName(live));

                playingList.RemoveAll(p => SafeSteamId(p) == playerSteamId);
                ScheduleForceToSpectator(live, "error.team.slot_unavailable");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SchedulePostJoinOverflowCheck - {Team}: delayed cap check for slot {Slot} threw.", team, playerSlot);
            }
        });
        Core.Scheduler.StopOnMapChange(token);
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

    
    public void HandleTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (!cfg.FaceitLikeDamageControl)
        {
            return;
        }

        var victim = ctx.Params.Entity.As<CCSPlayerPawn>();
        var attacker = ctx.Params.Info.Attacker.Value?.As<CCSPlayerPawn>();
        var weapon = ctx.Params.Info.DamageType;

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

                ctx.Params.Info.Damage = 0;
            }
        }
    }
}
