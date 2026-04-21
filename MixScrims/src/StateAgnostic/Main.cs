using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;
using SwiftlyS2.Shared.SteamAPI;

namespace MixScrims;

public sealed partial class MixScrims
{
    internal CancellationTokenSource? playerStatusTimer;
    internal CancellationTokenSource? playerStatusTimerCenterHtml;
    internal CancellationTokenSource? commandRemindersTimer;
    internal CancellationTokenSource? captainsAnnouncementsTimer;
    internal CancellationTokenSource? autoResetOnLeaveTimer;

    internal readonly List<IPlayer> readyPlayers = [];
    internal readonly HashSet<int> freshlyJoinedPlayers = [];
    internal readonly HashSet<int> recentlyDisconnectedPlayers = [];
    internal Team previousAutoJoinedTeam = Team.None;
    internal bool canPlayerBeRespawned = true;
    internal bool isMovingPlayersToTeams = false;
    internal bool preventNotPickedPlayersFromJoiningOngoingMatch = false;
    internal DateTime lastDiscordInviteSentAt = DateTime.MinValue;
    internal HashSet<ulong> playersWaitingForPunishment = [];
    internal readonly Dictionary<ulong, CancellationTokenSource> _punishmentTimers = [];
    internal bool resetMixOnFirstJoin = false;

    internal void StartAnnouncementTimers()
    {
        // Cancel any existing timers first to prevent duplicates
        playerStatusTimer?.Cancel();
        playerStatusTimerCenterHtml?.Cancel();
        commandRemindersTimer?.Cancel();

        // Players ready status
        playerStatusTimer = Core.Scheduler.RepeatBySeconds(
            periodSeconds: cfg.ChatAnnouncementTimers.PlayersReadyStatus,
            task: PrintReadyAndNotReadyPlayers
        );
        Core.Scheduler.StopOnMapChange(playerStatusTimer);

        // Player ready status center html
        if (cfg.ShowReadyStatusInCenterHtml)
        {
            playerStatusTimerCenterHtml = Core.Scheduler.RepeatBySeconds(
                periodSeconds: 1,
                task: () => DisplayReadyAndNotReadyPlayersInCenterHtml(1000)
            );
            Core.Scheduler.StopOnMapChange(playerStatusTimerCenterHtml);
        }

        // Command reminders
        commandRemindersTimer = Core.Scheduler.RepeatBySeconds(
            periodSeconds: cfg.ChatAnnouncementTimers.CommandReminders,
            task: PrintCommandReminders
        );
        Core.Scheduler.StopOnMapChange(commandRemindersTimer);
    }

    /// <summary>
    /// Checks whether the required number of players are ready to begin the next phase of the match, and advances the
    /// match state if conditions are met.
    /// </summary>
    internal void CheckReadyPlayersToStart()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("CheckReadyPlayersToStart: readyPlayers={ReadyCount} | Required={Required}", readyPlayers.Count, GetNumberOfPlayersRequiredToStart());

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup && readyPlayers.Count >= GetNumberOfPlayersRequiredToStart())
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("CheckReadyPlayersToStart: Starting Map Voting Phase");

            if (cfg.SkipMapVoting)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("CheckReadyPlayersToStart: Skipping Map Voting Phase, using current map");
                
                mixScrimsService.SetMatchState(MatchState.MapChosen);
                StartTeamPickingPhase();
                return;
            }
            
            if (cfg.DetailedLogging)
                logger.LogInformation("CheckReadyPlayersToStart: Starting Map Voting Phase");
            StartMapVotingPhase();
        }

        if (matchState == MatchState.MapChosen && readyPlayers.Count >= GetNumberOfPlayersRequiredToStart())
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("CheckReadyPlayersToStart: Starting Team Picking Phase");
            StartTeamPickingPhase();
        }
    }
    
    /// <summary>
    /// Adds the specified player to the ready list if the match is in a state that allows players to become ready.
    /// </summary>
    internal void AddPlayerToReadyList(IPlayer player, bool announce = false)
    {
        var name = player.Name ?? $"#{player.PlayerID}";
        logger.LogInformation("AddPlayerToReadyList: called for {Player}", name);

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup || matchState == MatchState.MapChosen)
        {
            if (readyPlayers.Any(p => p.PlayerID == player.PlayerID))
            {
                if (announce)
                {
                    PrintMessageToPlayer(player, Core.Localizer["command.ready.already_ready"]);
                }
                return;
            }

            readyPlayers.Add(player);
            if (announce)
            {
                PrintMessageToAllPlayers(Core.Localizer["command.ready", name]);
            }
            CheckReadyPlayersToStart();

            if (cfg.ShowReadyStatusInScoreboard)
                SetPlayerReadyStatusInScoreboard(player, true);
        }
        else
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "ready"]);
        }
    }

    /// <summary>
    /// Removes the specified player from the ready list, optionally announcing the change.
    /// </summary>
    internal void RemovePlayerFromReadyList(IPlayer player, bool announce = false)
    {
        var name = player.Name ?? $"#{player.PlayerID}";
        logger.LogInformation("RemovePlayerFromReadyList: called for {Player}", name);

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup || matchState == MatchState.MapChosen)
        {
            var existing = readyPlayers.FirstOrDefault(p => p.PlayerID == player.PlayerID);

            if (existing == null)
            {
                if (announce)
                {
                    PrintMessageToPlayer(player, Core.Localizer["command.unready.already_unready"]);
                }
                return;
            }

            if (announce)
            {
                PrintMessageToAllPlayers(Core.Localizer["command.unready", name]);
            }
            readyPlayers.Remove(existing);
            CheckReadyPlayersToStart();

            if (cfg.ShowReadyStatusInScoreboard)
                SetPlayerReadyStatusInScoreboard(player, false);
        }
        else
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "unready"]);
        }
    }

    /// <summary>
    /// Initiates the process of loading the specified map and updates the match state accordingly.
    /// </summary>
    internal void LoadSelectedMap(MapDetails map)
    {
		if (cfg.DetailedLogging)
			logger.LogInformation("LoadSelectedMap: Loading map {Map}", map.MapName);

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.MapLoading)
        {
            ScheduleMapLoadingAnnouncement(map);
            return;
        }

        Core.Engine.ExecuteCommand("tv_stoprecord");
        var loadMapToken = Core.Scheduler.DelayBySeconds(5, () => LoadMap(map));
        Core.Scheduler.StopOnMapChange(loadMapToken);

        mixScrimsService.SetMatchState(MatchState.MapLoading);
        PrintMessageToAllPlayers(Core.Localizer["announcement.map.changing", map.DisplayName]);
        ScheduleMapLoadingAnnouncement(map);
    }

    /// <summary>
    /// Loads the specified map into the game engine, switching the current level to the provided map.
    /// </summary>
    internal void LoadMap(MapDetails map)
    {
		if (cfg.DetailedLogging)
			logger.LogInformation("LoadMap: Executing map change to {Map}", map.MapName);
        if (map.IsWorkshopMap && !string.IsNullOrWhiteSpace(map.WorkshopId))
        {
            Core.Engine.ExecuteCommand($"host_workshop_map {map.WorkshopId}");
        }
        else
        {
            Core.Engine.ExecuteCommand($"map {map.MapName}");
        }
    }

    /// <summary>
    /// Schedules a chat announcement to notify players that the specified map is loading after a 15-second delay.
    /// </summary>
    internal void ScheduleMapLoadingAnnouncement(MapDetails map)
    {
		if (cfg.DetailedLogging)
			logger.LogInformation("ScheduleMapLoadingAnnouncement: Scheduling map loading announcement in 15 seconds");

        var token = Core.Scheduler.DelayBySeconds(15, () =>
        {
            var matchState = mixScrimsService.GetCurrentMatchState();

            if (matchState == MatchState.MapLoading)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.map.loading", map.DisplayName]);
                LoadSelectedMap(map);
            }
        });

        Core.Scheduler.StopOnMapChange(token);
    }
    
    /// <summary>
    /// Selects a random map from the list of available maps that can be nominated for voting.
    /// </summary>
    internal MapDetails GetRandomMap()
    {
        var maps = GetMapsToVote();
        if (maps.Count == 0)
        {
            maps = mapsConfig.Maps.Where(m => m.CanBeVoted).ToList();
        }
        if (maps.Count == 0)
        {
            logger.LogError("GetRandomMap: No maps available for voting. Check configuration.");
            return new MapDetails { MapName = "de_mirage", DisplayName = "Mirage", CanBeVoted = true };
        }
        var index = Random.Shared.Next(maps.Count);
        return maps[index];
    }

    /// <summary>
    /// Sets the display name for the specified team.
    /// </summary>
    internal void SetTeamName(Team team, string? name = null)
    {
        var teamName = (name is null || string.IsNullOrWhiteSpace(name)) ? null : name.Trim();

        if (team == Team.CT)
        {
            Core.Scheduler.NextTick(() =>
            {
                if (teamName is null)
                {
                    Core.Engine.ExecuteCommand("mp_teamname_1 COUNTER-TERRORISTS");
                }
                else
                {
                    Core.Engine.ExecuteCommand($"mp_teamname_1 team_{teamName}");
                }
            });
        }
        else if (team == Team.T)
        {
            Core.Scheduler.NextTick(() =>
            {
                if (teamName is null)
                {
                    Core.Engine.ExecuteCommand("mp_teamname_2 TERRORISTS");
                }
                else
                {
                    Core.Engine.ExecuteCommand($"mp_teamname_2 team_{teamName}");
                }
            });
        }
    }

    /// <summary>
    /// Assigns the Counter-Terrorist (CT) captain role to the specified player.
    /// </summary>
    internal void SetCtCaptain(IPlayer admin, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("SetCtCaptain: picked player is invalid");
            var localizer = Core.Translation.GetPlayerLocalizer(admin);
            admin.SendChat(GetServerPrefix() + " " + Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            return;
        }

        PrintMessageToAllPlayers(Core.Localizer["command.captain.ct", admin.Name ?? $"#{admin.PlayerID}", player.Name ?? $"#{player.PlayerID}"]);
        PickCtCaptain(player);

        CloseMenuForPlayer(admin);
    }

    /// <summary>
    /// Assigns the Terrorist team captain to the specified player, as selected by an administrator.
    /// </summary>
    internal void SetTCaptain(IPlayer admin, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("SetTCaptain: picked player is invalid");
            var localizer = Core.Translation.GetPlayerLocalizer(admin);
            admin.SendChat(GetServerPrefix() + " " + Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            return;
        }

        PrintMessageToAllPlayers(Core.Localizer["command.captain.t", admin.Name ?? $"#{admin.PlayerID}", player.Name ?? $"#{player.PlayerID}"]);
        PickTCaptain(player);

        CloseMenuForPlayer(admin);
    }

    /// <summary>
    /// Applies a configured punishment to a player who leaves the game, if enabled.
    /// </summary>
    internal void PunishOnLeave(IPlayer? player)
    {
        if (player == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("PunishOnLeave: player is null");
            return;
        }

        if (!cfg.PunishPlayerLeaves)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PunishOnLeave: player leave punishment is disabled in config");
            return;
        }

        var steamId = player.SteamID;
        var matchState = mixScrimsService.GetCurrentMatchState();

        if (cfg.PlayerLeavePunishment.Sensitivity == 0)
        {
            if (matchState == MatchState.Match
                || matchState == MatchState.Timeout)
            {
                QueuePlayerPunishment(steamId);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("PunishOnLeave: match state {MatchState} does not meet sensitivity level 0 requirements", matchState);
            }
            return;
        }

        if (cfg.PlayerLeavePunishment.Sensitivity == 1)
        {
            if (matchState == MatchState.KnifeRound
                || matchState == MatchState.PickingStartingSide
                || matchState == MatchState.Match
                || matchState == MatchState.Timeout)
            {
                QueuePlayerPunishment(steamId);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("PunishOnLeave: match state {MatchState} does not meet sensitivity level 1 requirements", matchState);
            }
            return;
        }

        if (cfg.PlayerLeavePunishment.Sensitivity == 2)
        {
            if (matchState == MatchState.PickingTeam
                || matchState == MatchState.KnifeRound
                || matchState == MatchState.PickingStartingSide
                || matchState == MatchState.Match
                || matchState == MatchState.Timeout)
            {
                QueuePlayerPunishment(steamId);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("PunishOnLeave: match state {MatchState} does not meet sensitivity level 2 requirements", matchState);
            }
            return;
        }
    }

    /// <summary>
    /// Queues a player for punishment based on their Steam ID, scheduling the punishment to be applied after a
    /// configured delay if the player does not rejoin.
    /// </summary>
    internal void QueuePlayerPunishment(ulong steamId)
    {
        if (playersWaitingForPunishment.Contains(steamId))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("QueuePlayerPunishment: player with SteamID {SteamId} is already queued for punishment", steamId);
            return;
        }
        playersWaitingForPunishment.Add(steamId);

        var banCommand = FormatBanCommand(steamId);
        if (string.IsNullOrWhiteSpace(banCommand))
        {
            logger.LogError("ExecutePunishmentCommand: formatted ban command is null or whitespace for SteamID {SteamId}", steamId);
            return;
        }

        var token = Core.Scheduler.DelayBySeconds(cfg.PlayerLeavePunishment.WaitBeforePunishmentSeconds, () =>
        {
            Core.Scheduler.NextTick(() =>
            { 
                _punishmentTimers.Remove(steamId);
                var players = GetPlayers();
                if (players == null)
                {
                    if (cfg.DetailedLogging)
                        logger.LogWarning("PunishPlayer: players list is null, cannot verify rejoin status");
                    return;
                }

                if (players.Count == 0)
                {
                    if (cfg.DetailedLogging)
                        logger.LogWarning("PunishPlayer: players list is empty, cannot verify rejoin status");
                    return;
                }

                if (players.Any(p => p.SteamID == steamId))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("PunishPlayer: Player {SteamId} has rejoined, skipping punishment", steamId);
                    return;
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("PunishOnLeave: punishing player {SteamId} for leaving", steamId);
                    ExecutePunishmentCommand(steamId);
                }
            });
        });
        _punishmentTimers[steamId] = token;
    }

    /// <summary>
    /// Executes the punishment command for the specified player, removing them from the punishment queue after
    /// execution.
    /// </summary>
    internal void ExecutePunishmentCommand(ulong steamId)
    {
        if (!playersWaitingForPunishment.Contains(steamId))
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("ExecutePunishmentCommand: player with SteamID {SteamId} is no longer queued for punishment", steamId);
            return;
        }

        var banCommand = FormatBanCommand(steamId);
        if (string.IsNullOrWhiteSpace(banCommand))
        {
            logger.LogError("ExecutePunishmentCommand: formatted ban command is null or whitespace for SteamID {SteamId}", steamId);
            return;
        }
        Core.Scheduler.NextTick(() =>
        {
            Core.Engine.ExecuteCommand(banCommand);
        });
        playersWaitingForPunishment.Remove(steamId);
        _punishmentTimers.Remove(steamId);
    }

    internal void KickPlayer(ulong steamId, string? reason)
    {
        var player = GetPlayerBySteamId(steamId);
        if (player == null)
        {
            logger.LogError("KickPlayer: player with SteamID {SteamId} not found", steamId);
            return;
        }

        if (string.IsNullOrEmpty(reason))
        {
            reason = Core.Localizer["info.kick_reason.generic"];
        }

        // KickAsync is thread-safe; Kick() is not safe outside the main game thread
        _ = player.KickAsync(reason, ENetworkDisconnectionReason.NETWORK_DISCONNECT_DISCONNECT_BY_SERVER);
    }

    /// <summary>
    /// Checks whether the active player count has dropped below the configured threshold during a match,
    /// and starts or cancels the auto-reset grace period timer accordingly.
    /// </summary>
    internal void CheckAutoResetOnLeave()
    {
        if (!cfg.AutoResetOnLeave.Enabled)
            return;

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Warmup || matchState == MatchState.MapLoading)
            return;

        var currentPlayerCount = GetPlayingPlayers().Count(p => !IsBot(p));
        var requiredPlayers = cfg.AutoResetOnLeave.MinimumPlayersRequired;

        if (currentPlayerCount < requiredPlayers)
        {
            // If no human players remain, defer reset to when the next player joins.
            // The server hibernates with 0 players so timers won't fire.
            if (currentPlayerCount == 0)
            {
                logger.LogInformation("CheckAutoResetOnLeave: No human players remaining, deferring reset to next player join.");
                autoResetOnLeaveTimer?.Cancel();
                autoResetOnLeaveTimer = null;
                resetMixOnFirstJoin = true;
                return;
            }

            if (autoResetOnLeaveTimer != null)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("CheckAutoResetOnLeave: Timer already running, player count {Current}/{Required}", currentPlayerCount, requiredPlayers);
                return;
            }

            logger.LogInformation("CheckAutoResetOnLeave: Player count {Current} below threshold {Required}, starting grace period of {Seconds}s", currentPlayerCount, requiredPlayers, cfg.AutoResetOnLeave.GracePeriodSeconds);

            PrintMessageToAllPlayers(Core.Localizer["announcement.auto_reset.warning", currentPlayerCount, requiredPlayers, cfg.AutoResetOnLeave.GracePeriodSeconds]);

            autoResetOnLeaveTimer = Core.Scheduler.DelayBySeconds(cfg.AutoResetOnLeave.GracePeriodSeconds, () =>
            {
                var currentState = mixScrimsService.GetCurrentMatchState();
                if (currentState == MatchState.Warmup || currentState == MatchState.MapLoading)
                {
                    autoResetOnLeaveTimer = null;
                    return;
                }

                var playersNow = GetPlayingPlayers().Count(p => !IsBot(p));
                if (playersNow < requiredPlayers)
                {
                    logger.LogInformation("CheckAutoResetOnLeave: Grace period expired, player count {Current} still below {Required}. Resetting match.", playersNow, requiredPlayers);
                    PrintMessageToAllPlayers(Core.Localizer["announcement.auto_reset.triggered"]);
                    autoResetOnLeaveTimer = null;
                    ResetPluginState();
                }
                else
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("CheckAutoResetOnLeave: Grace period expired but player count {Current} is now sufficient.", playersNow);
                    autoResetOnLeaveTimer = null;
                }
            });
            Core.Scheduler.StopOnMapChange(autoResetOnLeaveTimer);
        }
        else
        {
            CancelAutoResetOnLeaveTimer();
        }
    }

    /// <summary>
    /// Cancels the auto-reset grace period timer if it is currently running, and notifies players.
    /// </summary>
    internal void CancelAutoResetOnLeaveTimer(bool announce = true)
    {
        if (autoResetOnLeaveTimer != null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("CancelAutoResetOnLeaveTimer: Cancelling auto-reset timer, player count restored.");
            autoResetOnLeaveTimer.Cancel();
            autoResetOnLeaveTimer = null;

            if (announce)
                PrintMessageToAllPlayers(Core.Localizer["announcement.auto_reset.cancelled"]);
        }
    }

    /// <summary>
    /// Stops all active announcement timers, preventing further scheduled announcements from occurring.
    /// </summary>
    internal void StopAllAnnouncmentTimers()
    {
        commandRemindersTimer?.Cancel();
        playerStatusTimer?.Cancel();
        playerStatusTimerCenterHtml?.Cancel();
        captainsAnnouncementsTimer?.Cancel();
    }

    /// <summary>
    /// Stops and cancels all timers related to pre-match announcements.
    /// </summary>
    internal void StopPreMatchAnnouncementTimers()
    {
        playerStatusTimer?.Cancel();
        playerStatusTimerCenterHtml?.Cancel();
        captainsAnnouncementsTimer?.Cancel();
    }
}
