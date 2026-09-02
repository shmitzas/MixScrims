using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal Dictionary<int, int> playerColors = new();

    // Consumed once by HandleRoundStart after the match-start round transition lands.
    internal bool pendingMatchStartReset = false;

    /// <summary>
    /// Starts the match by updating the match state, notifying players, and executing the match_start cvar configuration.
    /// </summary>
    internal void StartMatch()
    {
        mixScrimsService.SetMatchState(MatchState.Match);

        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.match"]);

        MovePlayersToDesignatedTeamsPreMatch();

        // Primed before the restart so CCSGameRules::Think never observes a stale limit.
        // HandleMatchRoundPrestart / HandleRoundStart re-apply it on the fresh round too.
        RelaxEngineTeamLimits("StartMatch");

        StopPreMatchAnnouncementTimers();

        UnpauseMatch();

        pendingMatchStartReset = true;

        // Replaces `mp_restartgame 2`, which was the confirmed crash site (see repo memory
        // `mixscrims-mp-restartgame-team-limits-segv.md`): it routes through
        // CCSGameRules::RestartRound()'s complete-reset branch and segfaults on the 3rd match
        // of a server process. TerminateRound reaches RestartRound via the normal round-end
        // path instead, which every mid-match round-end already exercises safely on the same
        // process. The score / round-counter / money reset that mp_restartgame used to do is
        // now ResetMatchStartState(), run from HandleRoundStart once the transition lands.
        //   T+0.5s  exec cfg  (cvars only)
        //   T+2.5s  TerminateRound(GameCommencing, 1.0f) -> RestartRound at T+3.5s
        var cfgToken = Core.Scheduler.DelayBySeconds(0.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.Match)
            {
                logger.LogWarning("StartMatch: state changed before cfg exec (now {State}); skipping match_start.cfg.", mixScrimsService.GetCurrentMatchState());
                return;
            }

            if (Core.Engine is not { } engine)
            {
                logger.LogWarning("StartMatch: Core.Engine unavailable; skipping match_start.cfg.");
                return;
            }

            try
            {
                var gameRules = Core.EntitySystem.GetGameRules();
                if (gameRules is null || !gameRules.IsValid)
                {
                    logger.LogWarning("StartMatch: game rules invalid before cfg exec; skipping match_start.cfg.");
                    return;
                }

                engine.ExecuteCommand("exec mixscrims/match_start.cfg");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "StartMatch: exception dispatching match_start.cfg exec");
            }
        });
        Core.Scheduler.StopOnMapChange(cfgToken);

        var restartToken = Core.Scheduler.DelayBySeconds(2.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.Match)
            {
                logger.LogWarning("StartMatch: state changed before TerminateRound (now {State}); skipping manual restart.", mixScrimsService.GetCurrentMatchState());
                return;
            }
            RestartRoundManually("StartMatch", RoundEndReason.GameCommencing, 1.0f);
        });
        Core.Scheduler.StopOnMapChange(restartToken);

        var deferredToken = Core.Scheduler.DelayBySeconds(5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.Match) return;

            try
            {
                if (cfg.ShowReadyStatusInScoreboard)
                    RemoveReadyClanTagsFromAllPlayers();
                // Captain tags are set unconditionally during team-picking; strip them here so the
                // match starts with the players' original clan tags restored (empty if none).
                RemoveCaptainClanTagsFromAllPlayers();
                FixTeammateColors();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "StartMatch: deferred cosmetic mutations failed");
            }
        });
        Core.Scheduler.StopOnMapChange(deferredToken);

        if (Core.Engine is not { } matchEngine)
        {
            logger.LogError("StartMatch: Core.Engine unavailable");
            return;
        }
        var mapName = matchEngine.GlobalVars.MapName.ToString();
        if (string.IsNullOrEmpty(mapName))
        {
            logger.LogError("StartMatch: mapName is null or empty");
            return;
        }

        var mapDetails = mapsConfig.Maps.FirstOrDefault(m => m.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase));
        if (mapDetails == null)
        {
            logger.LogWarning("StartMatch: Map {MapName} not found in configuration.", mapName);
            return;
        }

        if (playedMaps.Count >= cfg.DisallowVotePreviousMaps)
        {
            if (cfg.DisallowVotePreviousMaps <= 0)
            {
                logger.LogWarning("StartMatch: DisallowVotePreviousMaps is <= 0. Clearing playedMaps to avoid out-of-range errors.");
                playedMaps.Clear();
            }
            else
            {
                int maxHistory = cfg.DisallowVotePreviousMaps - 1;
                while (playedMaps.Count > maxHistory)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("StartMatch: Removing oldest map {MapName} from history.", playedMaps[0].MapName);
                    playedMaps.RemoveAt(0);
                }
            }
        }
        playedMaps.Add(mapDetails);

        if (cfg.KickPlayersNotInMatch)
        {
            mixScrimsService.KickNotPlayingPlayers(Core.Localizer["info.kick_reason.not_picked"]);
        }
    }

    /// <summary>
    /// Resynchronizes <see cref="playingCtPlayers"/> and <see cref="playingTPlayers"/>
    /// with the engine's actual team assignments. Walks both lists, looks up each tracked
    /// SteamID's current team, and moves them between lists when sides have been swapped
    /// by the engine (regular halftime, OT halftime, OT-period transitions, surrender,
    /// or any other engine-driven swap). Players currently in Spectator or disconnected
    /// keep their existing assignment so reserved slots are preserved.
    /// </summary>
    internal void ResyncPlayingListsFromEngine()
    {
        // Snapshot SteamIDs (value-type) up front rather than IPlayer references. The
        // tracked IPlayer objects may have been disposed by SwiftlyS2 between the time
        // they were added to the list and this resync (e.g. a player disconnected after
        // the round-prestart). Accessing .SteamID on a disposed Player throws
        // ObjectDisposedException, which previously killed the entire resync and left the
        // playing lists desynced from the engine. The plugin-wide SafeSteamId helper
        // (Shared/Helpers.cs) wraps the read in try/catch and returns 0UL on failure,
        // with dedup'd warning logging via LogDisposedIfNew.
        var ctSnapshot = playingCtPlayers
            .Select(p => (player: p, steamId: SafeSteamId(p)))
            .ToList();
        var tSnapshot = playingTPlayers
            .Select(p => (player: p, steamId: SafeSteamId(p)))
            .ToList();

        var newCt = new List<IPlayer>();
        var newT = new List<IPlayer>();
        int movedCtToT = 0;
        int movedTToCt = 0;

        void Place(IPlayer tracked, ulong steamId, List<IPlayer> originList)
        {
            // Drop entries whose SteamID could not be read (disposed object). These
            // belong to disconnected players that HandleDisconnectedPlayer will/has
            // already pruned; carrying them forward only re-introduces disposed refs.
            if (steamId == 0UL)
            {
                logger.LogWarning("ResyncPlayingListsFromEngine: dropping tracked player due to unreadable SteamID (disposed reference).");
                return;
            }

            // Try to refresh the IPlayer reference (handles reconnects with new PlayerID/slot).
            IPlayer? live = null;
            try { live = GetPlayerBySteamId(steamId); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ResyncPlayingListsFromEngine: exception while refreshing player by SteamID {SteamId}.", steamId);
                live = null;
            }

            var current = live ?? tracked;
            int teamNum = -1;
            try
            {
                if (current.IsValid && current.Controller != null)
                    teamNum = current.Controller.TeamNum;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ResyncPlayingListsFromEngine: failed reading controller/team for SteamID {SteamId}.", steamId);
                teamNum = -1;
            }

            if (teamNum == (int)Team.CT)
                newCt.Add(current);
            else if (teamNum == (int)Team.T)
                newT.Add(current);
            else
            {
                // Currently in Spectator or unassigned - keep on the original side so
                // a player who briefly went to spec is still tracked on their team.
                // Disposed/disconnected players were filtered out above.
                if (originList == playingCtPlayers)
                    newCt.Add(current);
                else
                    newT.Add(current);
            }
        }

        foreach (var (player, steamId) in ctSnapshot)
        {
            int before = newCt.Count + newT.Count;
            Place(player, steamId, playingCtPlayers);
            // Track moves for logging
            if (newT.Count + newCt.Count == before + 1
                && newT.Count > 0
                && SafeSteamId(newT[^1]) == steamId)
                movedCtToT++;
        }
        foreach (var (player, steamId) in tSnapshot)
        {
            int before = newCt.Count + newT.Count;
            Place(player, steamId, playingTPlayers);
            if (newT.Count + newCt.Count == before + 1
                && newCt.Count > 0
                && SafeSteamId(newCt[^1]) == steamId)
                movedTToCt++;
        }

        // Second reconciliation pass - the first pass only walks TRACKED SteamIDs and cannot
        // detect a physical-on-team player that was never added to the playing list. This
        // happens when a player connects mid-match and gets placed by the engine (via
        // silent-restore reconnect, auto-fill, or a race with ScheduleForceToSpectator)
        // without their team change hitting HandleActiveMatchJoin's add path. Without this
        // pass, the tracked list under-counts vs the engine, HandleActiveMatchJoin blocks
        // legitimate join attempts ("list 5/5, actual 5"), and the resync's own log detects
        // the drift ("Round start resync complete ... CT:5 T:4, actual CT:5 T:5") but never
        // corrects it. This pass adopts every authenticated engine player onto the plugin's
        // tracked list - the correct semantic is "if you're physically on this team when the
        // round starts, you're playing this round".
        //
        // Cap enforcement: the CS2 silent-restore reconnect places a returning player back on
        // their previous team WITHOUT firing HandlePlayerChangeTeam, so HandleActiveMatchJoin's
        // capacity check never runs. Without a cap check here, this pass would blindly adopt
        // the returning player and push the team above MinimumReadyPlayers/2. When the target
        // side is already at cap we evict the untracked player to Spectator via the same
        // ScheduleForceToSpectator helper HandleActiveMatchJoin uses for its own "team full"
        // reject path, and drop any stale SteamID reservation so a next-round rejoin can't
        // re-admit them through the reservation door.
        int maxTeamSize = cfg.MinimumReadyPlayers / 2;
        int adoptedCt = 0;
        int adoptedT = 0;
        int evictedCt = 0;
        int evictedT = 0;
        foreach (var p in GetPlayersInTeam(Team.CT))
        {
            if (!IsPlayerValid(p) || IsBot(p)) continue;
            var sid = SafeSteamId(p);
            if (sid == 0UL) continue;
            if (newCt.Any(existing => SafeSteamId(existing) == sid)) continue;
            if (newCt.Count >= maxTeamSize)
            {
                logger.LogInformation("ResyncPlayingListsFromEngine: rejecting untracked CT adoption for {PlayerName} (SteamID {SteamId}) - team at cap ({Count}/{Max}), forcing to Spectator.",
                    SafePlayerName(p), sid, newCt.Count, maxTeamSize);
                ScheduleForceToSpectator(p, "error.team.slot_unavailable");
                evictedCt++;
                continue;
            }
            newCt.Add(p);
            adoptedCt++;
        }
        foreach (var p in GetPlayersInTeam(Team.T))
        {
            if (!IsPlayerValid(p) || IsBot(p)) continue;
            var sid = SafeSteamId(p);
            if (sid == 0UL) continue;
            if (newT.Any(existing => SafeSteamId(existing) == sid)) continue;
            if (newT.Count >= maxTeamSize)
            {
                logger.LogInformation("ResyncPlayingListsFromEngine: rejecting untracked T adoption for {PlayerName} (SteamID {SteamId}) - team at cap ({Count}/{Max}), forcing to Spectator.",
                    SafePlayerName(p), sid, newT.Count, maxTeamSize);
                ScheduleForceToSpectator(p, "error.team.slot_unavailable");
                evictedT++;
                continue;
            }
            newT.Add(p);
            adoptedT++;
        }

        playingCtPlayers = newCt;
        playingTPlayers = newT;

        // Un-gated from DetailedLogging: this is the operator's primary drift-visibility
        // signal for side switches at halftime / OT halftime / OT-period boundaries and for
        // the second-pass adopt-untracked pass above. The no-drift steady state stays silent
        // because the guard requires at least one move, adoption, or eviction.
        if (movedCtToT > 0 || movedTToCt > 0 || adoptedCt > 0 || adoptedT > 0 || evictedCt > 0 || evictedT > 0)
            logger.LogInformation("ResyncPlayingListsFromEngine: reconciliation complete - moved {CtToT} CT->T, {TToCt} T->CT, adopted {AdoptedCt} untracked CT, {AdoptedT} untracked T, evicted {EvictedCt} over-cap CT, {EvictedT} over-cap T (now CT:{CT} T:{T})",
                movedCtToT, movedTToCt, adoptedCt, adoptedT, evictedCt, evictedT, playingCtPlayers.Count, playingTPlayers.Count);
    }

    /// <summary>
    /// Assigns unique teammate colors to all currently playing players, per team.
    /// Uses the tracking dictionary as the source of truth to avoid stale controller reads.
    /// </summary>
    internal void FixTeammateColors()
    {
        AssignUniqueTeamColors(Team.CT);
        AssignUniqueTeamColors(Team.T);
    }

    /// <summary>
    /// Builds a guaranteed-unique color assignment for a team in two passes:
    /// 1. Honor existing dict entries that are still unique.
    /// 2. Assign the first free color to players without a valid unique entry.
    /// All assignments are applied atomically at the end to avoid stale reads.
    /// </summary>
    private void AssignUniqueTeamColors(Team team)
    {
        var players = GetPlayersInTeam(team);
        if (players.Count == 0) return;

        var finalAssignments = new Dictionary<int, int>(); // playerID → color
        var usedColors = new HashSet<int>();

        // First pass: preserve existing unique assignments from the tracking dict
        foreach (var player in players)
        {
            if (playerColors.TryGetValue(player.PlayerID, out int existingColor)
                && existingColor >= 0 && existingColor < 5
                && usedColors.Add(existingColor))
            {
                finalAssignments[player.PlayerID] = existingColor;
            }
        }

        // Second pass: assign a free color to players without a valid unique entry
        foreach (var player in players)
        {
            if (finalAssignments.ContainsKey(player.PlayerID))
                continue;

            for (int color = 0; color < 5; color++)
            {
                if (usedColors.Add(color))
                {
                    finalAssignments[player.PlayerID] = color;
                    break;
                }
            }
        }

        // Apply all assignments to controllers and update the tracking dict
        foreach (var player in players)
        {
            if (finalAssignments.TryGetValue(player.PlayerID, out int color))
            {
                if (player.Controller is null) continue;
                player.Controller.CompTeammateColor = color;
                player.Controller.CompTeammateColorUpdated();
                playerColors[player.PlayerID] = color;

                if (cfg.DetailedLogging)
                    logger.LogInformation("AssignUniqueTeamColors: Assigned color {Color} to {PlayerName} (ID: {PlayerId}) on {Team}", color, player.Name, player.PlayerID, team);
            }
        }
    }
}
