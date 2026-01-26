using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public sealed partial class MixScrims
{
    private CancellationTokenSource? playerStatusTimer;
    private CancellationTokenSource? playerStatusTimerCenterHtml;
    private CancellationTokenSource? commandRemindersTimer;
    private CancellationTokenSource? captainsAnnouncementsTimer;

    private readonly List<IPlayer> readyPlayers = [];
    private readonly List<int> freshlyJoinedPlayers = new();
    private readonly List<int> recentlyDisconnectedPlayers = new();
    private Team previousAutoJoinedTeam = Team.None;
    private bool canPlayerBeRespawned = true;
    private bool isMovingPlayersToTeams = false;

    private DateTime lastDiscordInviteSentAt = DateTime.MinValue;


    private void StartAnnouncementTimers()
    {
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
    private void CheckReadyPlayersToStart()
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
    private void AddPlayerToReadyList(IPlayer player, bool announce = false)
    {
        var name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
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
    private void RemovePlayerFromReadyList(IPlayer player, bool announce = false)
    {
        var name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
        logger.LogInformation("RemovePlayerFromReadyList: called for {Player}", name);

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup || matchState == MatchState.MapChosen)
        {
            var existing = readyPlayers.FirstOrDefault(p => p.PlayerID == player.PlayerID);

            if (existing == null)
            {
                if (announce)
                {
                    PrintMessageToPlayer(player, Core.Localizer["command.unready.not_ready"]);
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
    private void LoadSelectedMap(MapDetails map)
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
    private void LoadMap(MapDetails map)
    {
		if (cfg.DetailedLogging)
			logger.LogInformation("LoadMap: Executing map change to {Map}", map.MapName);
        if (map.IsWorkshopMap && !string.IsNullOrWhiteSpace(map.WorkshopId))
        {
            Core.Engine.ExecuteCommand($"ds_workshop_changelevel {map.MapName}");
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
    private void ScheduleMapLoadingAnnouncement(MapDetails map)
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
    private MapDetails GetRandomMap()
    {
        var maps = cfg.Maps.Where(m => m.CanBeVoted).ToList();
        if (maps.Count == 0)
        {
            logger.LogError("GetRandomMap: No maps available for voting. Check configuration.");
            return new MapDetails { MapName = "de_mirage", DisplayName = "Mirage", CanBeVoted = true };
        }
        var random = new Random();
        int index = random.Next(maps.Count);
        return maps[index];
    }

    /// <summary>
    /// Sets the display name for the specified team.
    /// </summary>
    private void SetTeamName(Team team, string? name = null)
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
    private void SetCtCaptain(IPlayer admin, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("SetCtCaptain: picked player is invalid");
            var localizer = Core.Translation.GetPlayerLocalizer(admin);
            admin.SendChat(Core.Localizer["server_prefix"] + " " + Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            return;
        }

        PrintMessageToAllPlayers(Core.Localizer["command.captain.ct", admin.Controller?.PlayerName ?? $"#{admin.PlayerID}", player.Controller?.PlayerName ?? $"#{player.PlayerID}"]);
        PickCtCaptain(player);

        CloseMenuForPlayer(admin);
    }

    /// <summary>
    /// Assigns the Terrorist team captain to the specified player, as selected by an administrator.
    /// </summary>
    private void SetTCaptain(IPlayer admin, string pickedPlayerName)
    {
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("SetTCaptain: picked player is invalid");
            var localizer = Core.Translation.GetPlayerLocalizer(admin);
            admin.SendChat(Core.Localizer["server_prefix"] + " " + Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            return;
        }

        PrintMessageToAllPlayers(Core.Localizer["command.captain.t", admin.Controller?.PlayerName ?? $"#{admin.PlayerID}", player.Controller?.PlayerName ?? $"#{player.PlayerID}"]);
        PickTCaptain(player);

        CloseMenuForPlayer(admin);
    }

    /// <summary>
    /// Applies a configured punishment to a player who leaves the game, if enabled.
    /// </summary>
    private void PunishOnLeave(IPlayer? player)
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

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (cfg.PlayerLeavePunishment.Sensitivity == 0)
        {
            if (matchState == MatchState.Match
                || matchState == MatchState.Timeout)
            {
                PunishPlayer(player);
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
                PunishPlayer(player);
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
                PunishPlayer(player);
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
    /// Initiates a punishment action against the specified player if they have left the game and have not rejoined
    /// within the configured wait period.
    /// </summary>
    private void PunishPlayer(IPlayer? player)
    {
        if (player == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("PunishPlayer: player is null");
            return;
        }

        var banCommand = FormatBanCommand(player);
        var steamId = player.SteamID.ToString();
        if (!string.IsNullOrWhiteSpace(banCommand))
        {
            Core.Scheduler.DelayBySeconds(cfg.PlayerLeavePunishment.WaitBeforePunishmentSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                { 
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

                    if (players.Any(p => p.SteamID.ToString() == steamId))
                    {
                        if (cfg.DetailedLogging)
                            logger.LogInformation("PunishPlayer: Player {PlayerName} has rejoined, skipping punishment", player.Controller?.PlayerName ?? $"#{player.PlayerID}");
                        return;
                    }
                    else
                    {
                        if (cfg.DetailedLogging)
                            logger.LogInformation("PunishOnLeave: punishing player {PlayerName} for leaving", player.Controller?.PlayerName ?? $"#{player.PlayerID}");
                        Core.Engine.ExecuteCommand(banCommand);
                    }
                });
            });
        }
    }

    /// <summary>
    /// Stops all active announcement timers, preventing further scheduled announcements from occurring.
    /// </summary>
    private void StopAllAnnouncmentTimers()
    {
        commandRemindersTimer?.Cancel();
        playerStatusTimer?.Cancel();
        playerStatusTimerCenterHtml?.Cancel();
        captainsAnnouncementsTimer?.Cancel();
    }

    /// <summary>
    /// Stops and cancels all timers related to pre-match announcements.
    /// </summary>
    private void StopPreMatchAnnouncementTimers()
    {
        playerStatusTimer?.Cancel();
        playerStatusTimerCenterHtml?.Cancel();
        captainsAnnouncementsTimer?.Cancel();
    }
}