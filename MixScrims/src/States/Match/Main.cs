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
            logger.LogWarning($"StartMatch: Map {mapName} not found in configuration.");
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
                        logger.LogInformation($"StartMatch: Removing oldest map '{playedMaps[0].MapName}' from history.");
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
    /// Assigns available teammate colors to all currently playing players who do not already have one.
    /// </summary>
    internal void FixTeammateColors()
    {
        var players = GetPlayingPlayers();
        foreach(var player in players)
        {
            var freeColor = GetFreePlayerColor(player);
            if (freeColor != null)
            {
                player.Controller.CompTeammateColor = freeColor.Value;
                player.Controller.CompTeammateColorUpdated();
                playerColors[player.PlayerID] = freeColor.Value;
            }
        }

        FixColorDuplicatesForTeam(Team.CT);
        FixColorDuplicatesForTeam(Team.T);
    }

    /// <summary>
    /// Finds the first available player color index for the specified player based on their team affiliation.
    /// </summary>
    internal int? GetFreePlayerColor(IPlayer player)
    {
        if (player == null)
            return null;
        if (player.PlayerPawn == null)
            return null;

        if (player.PlayerPawn.Team == Team.CT)
        {
            var ocupiedColors = new HashSet<int>(GetPlayersInTeam(Team.CT)
                .Where(p => p.PlayerID != player.PlayerID)
                .Select(p => {
                    // First check the actual in-game color to catch all assigned colors
                    if (p.Controller?.CompTeammateColor != null)
                        return p.Controller.CompTeammateColor;
                    // Fallback to tracking dictionary if controller color is not set
                    if (playerColors.ContainsKey(p.PlayerID))
                        return playerColors[p.PlayerID];
                    return -1; // No color assigned
                })
                .Where(color => color >= 0 && color < 5)); // Filter out invalid colors
            
            if (ocupiedColors.Count >= 5)
                return null;

            for (int color = 0; color < 5; color++)
            {
                if (!ocupiedColors.Contains(color))
                    return color;
            }
        }

        if (player.PlayerPawn.Team == Team.T)
        {
            var ocupiedColors = new HashSet<int>(GetPlayersInTeam(Team.T)
                .Where(p => p.PlayerID != player.PlayerID)
                .Select(p => {
                    // First check the actual in-game color to catch all assigned colors
                    if (p.Controller?.CompTeammateColor != null)
                        return p.Controller.CompTeammateColor;
                    // Fallback to tracking dictionary if controller color is not set
                    if (playerColors.ContainsKey(p.PlayerID))
                        return playerColors[p.PlayerID];
                    return -1; // No color assigned
                })
                .Where(color => color >= 0 && color < 5)); // Filter out invalid colors
            
            if (ocupiedColors.Count >= 5)
                return null;

            for (int color = 0; color < 5; color++)
            {
                if (!ocupiedColors.Contains(color))
                    return color;
            }
        }

        return null;
    }

    /// <summary>
    /// Fixes color duplicates for a specific team by identifying players with the same color and reassigning free colors.
    /// </summary>
    private void FixColorDuplicatesForTeam(Team team)
    {
        var teamPlayers = GetPlayersInTeam(team)
            .Select(p => new {
                Player = p,
                Color = GetPlayerActualColor(p)
            })
            .Where(x => x.Color >= 0 && x.Color < 5) // Only players with valid colors
            .ToList();

        var colorGroups = teamPlayers
            .GroupBy(x => x.Color)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicateGroup in colorGroups)
        {
            var playersWithDuplicateColor = duplicateGroup.Skip(1).Select(x => x.Player).ToList();

            foreach (var player in playersWithDuplicateColor)
            {
                var freeColor = GetFreePlayerColor(player);
                if (freeColor != null)
                {
                    player.Controller.CompTeammateColor = freeColor.Value;
                    player.Controller.CompTeammateColorUpdated();
                    playerColors[player.PlayerID] = freeColor.Value;

                    if (cfg.DetailedLogging)
                        logger.LogInformation($"FixColorDuplicates: Reassigned player {player.Name} (ID: {player.PlayerID}) from color {duplicateGroup.Key} to {freeColor.Value} on {team}");
                }
                else
                {
                    logger.LogWarning($"FixColorDuplicates: Could not find free color for player {player.Name} (ID: {player.PlayerID}) on {team}");
                }
            }
        }
    }

    /// <summary>
    /// Gets the actual color assigned to a player, checking both the controller and tracking dictionary.
    /// </summary>
    private int GetPlayerActualColor(IPlayer player)
    {
        // First check the actual in-game color
        if (player.Controller?.CompTeammateColor != null)
            return player.Controller.CompTeammateColor;
        
        // Fallback to tracking dictionary
        if (playerColors.ContainsKey(player.PlayerID))
            return playerColors[player.PlayerID];
        
        return -1; // No color assigned
    }
}
