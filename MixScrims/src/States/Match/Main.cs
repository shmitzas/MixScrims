using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal Dictionary<int, int> playerColors = new();

    /// <summary>
    /// Starts the match by updating the match state, notifying players, and executing the match_start cvar configuration.
    /// </summary>
    internal void StartMatch()
    {
        mixScrimsService.SetMatchState(MatchState.Match);

        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.match"]);

        MovePlayersToDesignatedTeamsPreMatch();

        StopPreMatchAnnouncementTimers();

        if (cfg.ShowReadyStatusInScoreboard)
            RemoveReadyClanTagsFromAllPlayers();

        UnpauseMatch();
        Core.Scheduler.NextTick(() =>
        {
            Core.Engine.ExecuteCommand("exec mixscrims/match_start.cfg");
            // Override engine team limits immediately after match cvars settle, so the
            // very first side switch (and every subsequent one) cannot dump players to spec.
            RelaxEngineTeamLimits("StartMatch");
        });

        var mapName = Core.Engine.GlobalVars.MapName;
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

        FixTeammateColors();
        
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
        // Snapshot to avoid mutating the lists we're iterating.
        var ctSnapshot = playingCtPlayers.ToList();
        var tSnapshot = playingTPlayers.ToList();

        var newCt = new List<IPlayer>();
        var newT = new List<IPlayer>();
        int movedCtToT = 0;
        int movedTToCt = 0;

        void Place(IPlayer tracked, List<IPlayer> originList)
        {
            // Try to refresh the IPlayer reference (handles reconnects with new PlayerID/slot).
            IPlayer? live = null;
            try { live = GetPlayerBySteamId(tracked.SteamID); }
            catch { live = null; }

            var current = live ?? tracked;
            int teamNum = -1;
            if (current.IsValid && current.Controller != null)
                teamNum = current.Controller.TeamNum;

            if (teamNum == (int)Team.CT)
                newCt.Add(current);
            else if (teamNum == (int)Team.T)
                newT.Add(current);
            else
            {
                // Disconnected, in Spectator, or unassigned - keep on the original side
                // so reserved-slot semantics are preserved.
                if (originList == playingCtPlayers)
                    newCt.Add(current);
                else
                    newT.Add(current);
            }
        }

        foreach (var p in ctSnapshot)
        {
            int before = newCt.Count + newT.Count;
            Place(p, playingCtPlayers);
            // Track moves for logging
            if (newT.Count + newCt.Count == before + 1
                && newT.Count > 0
                && newT[^1].SteamID == p.SteamID)
                movedCtToT++;
        }
        foreach (var p in tSnapshot)
        {
            int before = newCt.Count + newT.Count;
            Place(p, playingTPlayers);
            if (newT.Count + newCt.Count == before + 1
                && newCt.Count > 0
                && newCt[^1].SteamID == p.SteamID)
                movedTToCt++;
        }

        playingCtPlayers = newCt;
        playingTPlayers = newT;

        // Reservations are SteamID-based and must follow side switches too. When we detect any
        // movement of tracked players between sides, swap the reservation sets so a reserved
        // player who is currently in Spectator returns to the correct (post-swap) side.
        if (movedCtToT > 0 || movedTToCt > 0)
        {
            var oldReservedCt = reservedCtSlots.ToList();
            var oldReservedT = reservedTSlots.ToList();
            reservedCtSlots.Clear();
            reservedTSlots.Clear();
            foreach (var s in oldReservedCt) reservedTSlots.Add(s);
            foreach (var s in oldReservedT) reservedCtSlots.Add(s);
        }

        if (cfg.DetailedLogging && (movedCtToT > 0 || movedTToCt > 0))
            logger.LogInformation("ResyncPlayingListsFromEngine: side swap detected - moved {CtToT} CT->T, {TToCt} T->CT (now CT:{CT} T:{T})",
                movedCtToT, movedTToCt, playingCtPlayers.Count, playingTPlayers.Count);
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
                player.Controller.CompTeammateColor = color;
                player.Controller.CompTeammateColorUpdated();
                playerColors[player.PlayerID] = color;

                if (cfg.DetailedLogging)
                    logger.LogInformation("AssignUniqueTeamColors: Assigned color {Color} to {PlayerName} (ID: {PlayerId}) on {Team}", color, player.Name, player.PlayerID, team);
            }
        }
    }
}
