using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public sealed partial class MixScrims
{
    /// <summary>
    /// Prints a message to a specified player.
    /// </summary>
    private void PrintMessageToPlayer(IPlayer? player, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            logger.LogError("PrintMessageToPlayer: message is invalid");
            return;
        }

        Core.Scheduler.NextTick(() =>
        {
             if (player == null || !player.IsValid)
            {
                logger.LogDebug("PrintMessageToPlayer: target is not a player entity anymore");
                return;
            }

            player.SendChat(Core.Localizer["server_prefix"] + " " + message);
        });
    }

    /// <summary>
    /// Prints a message to a list of specified players.
    /// </summary>
    private void PrintMessageToCertainPlayers(List<IPlayer> players, string message)
    {
        if (players == null)
        {
            logger.LogError("PrintMessageToCertainPlayers: players list is invalid");
            return;
        }
        foreach (var player in players)
        {
            PrintMessageToPlayer(player, message);
        }
    }

    /// <summary>
    /// Prints a message to all players in the server.
    /// </summary>
    private void PrintMessageToAllPlayers(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            logger.LogError("PrintMessageToAllPlayers: message is invalid");
            return;
        }

        Core.Scheduler.NextTick(() =>
        {
            Core.PlayerManager.SendChat(Core.Localizer["server_prefix"] + " " + message);
        });
    }

    /// <summary>
    /// Sends a message to all players in the specified team.
    /// </summary>
    private void PrintMessageToTeam(Team team, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            logger.LogError("PrintMessageToTeam: message is invalid");
            return;
        }

        var playersInTeam = GetPlayersInTeam(team);
        PrintMessageToCertainPlayers(playersInTeam, message);
    }

    /// <summary>
    /// Checks if the player is valid (not null, has a controller, and is on a valid team).
    /// </summary>
    private bool IsPlayerValid(IPlayer? player)
    {
        if (player != null && player.IsValid)
            return true;
        
        return false;
    }

    /// <summary>
    /// Determines whether the specified player is a bot.
    /// </summary>
    private bool IsBot(IPlayer? player)
    {
        if (!IsPlayerValid(player))
            return false;
        return player!.PlayerPawn?.Bot?.IsValid ?? false;
    }

    /// <summary>
    /// Returns a list of all valid players.
    /// </summary>
    private List<IPlayer> GetPlayers()
    {
        return Core.PlayerManager.GetAllPlayers().Where(IsPlayerValid).ToList()!;
    }

    /// <summary>
    /// Returns a list of players currently playing (CT or T).
    /// </summary>
    private List<IPlayer> GetPlayingPlayers()
    {
        return Core.PlayerManager
            .GetAllPlayers()
            .Where(p => IsPlayerValid(p)
                && p.PlayerPawn != null
                && (p.PlayerPawn.TeamNum == 2
                || p.PlayerPawn.TeamNum == 3))
                .ToList()!;
    }

    /// <summary>
    /// Returns a list of players for a specified team.
    /// </summary>
    private List<IPlayer> GetPlayersInTeam(Team team)
    {
        var teamNum = (int)team;
        var players = GetPlayingPlayers();
        var result = new List<IPlayer>();
        foreach (var player in players)
        {
            if (player.PlayerPawn != null && player.PlayerPawn.TeamNum == teamNum)
                result.Add(player);
        }
        return result;
    }

    /// <summary>
    /// Returns a list of players who haven't readied up yet.
    /// </summary>
    private List<IPlayer> GetNotReadyPlayers()
    {
        var allPlayers = GetPlayers();
        if (allPlayers.Count == 0)
            return new List<IPlayer>();

        return allPlayers.Where(player => !readyPlayers.Any(rp => rp.PlayerID == player.PlayerID)).ToList();
    }

    /// <summary>
    /// Returns a list of maps that can be voted for.
    /// </summary>
    private List<MapDetails> GetMapsToVote()
    {
        return cfg.Maps
            .Where(m => m.CanBeVoted && !playedMaps.Any(pm => pm.MapName == m.MapName)).ToList();
    }

    /// <summary>
    /// Determines the number of players required to start the game.
    /// </summary>
    private int GetNumberOfPlayersRequiredToStart()
    {
        int totalPlayers = GetPlayers().Count;
        if (cfg.RequireAllConnectedPlayersToBeReady)
        {
            if (totalPlayers < cfg.MinimumReadyPlayers)
                return cfg.MinimumReadyPlayers;
            return totalPlayers;
        }

        return cfg.MinimumReadyPlayers;
    }

    /// <summary>
    /// Returns a player by their Controller.PlayerName.
    /// </summary>
    private IPlayer? GetPlayerByName(string playerName)
    {
        return GetPlayers().FirstOrDefault(p =>
            string.Equals(p.Controller?.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Pauses the match using cvar.
    /// </summary>
    private void PauseMatch()
    {
        logger.LogInformation("Pausing match");
        Core.Scheduler.NextTick(() =>
        {
            Core.Engine.ExecuteCommand("mp_pause_match");
        });
    }

    /// <summary>
    /// Unpauses the match using cvar.
    /// </summary>
    private void UnpauseMatch()
    {
        logger.LogInformation("Unpausing match");
        Core.Scheduler.NextTick(() =>
        {
            Core.Engine.ExecuteCommand("mp_unpause_match");
        });
    }

    /// <summary>
    /// Retrieves the details of a map by its name or display name.
    /// </summary>
    private MapDetails? GetMapByName(string mapName)
    {
        return cfg.Maps.FirstOrDefault(m =>
            string.Equals(m.MapName, mapName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.DisplayName, mapName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Respawns the specified player if they are eligible for respawn.
    /// </summary>
    private void RespawnPlayer(IPlayer player)
    {
        if (!canPlayerBeRespawned)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("RespawnPlayer: Player respawning is currently disabled.");
            return;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("Respawning player {PlayerName}", player.Controller.PlayerName);

        try
        {
            if (IsPlayerValid(player))
            {
                player.Controller.RespawnAsync();
            }
            else
            {
                logger.LogWarning("RespawnPlayer: Player {PlayerName} is no longer valid, skipping respawn.", player.Controller?.PlayerName ?? "Unknown");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RespawnPlayer: Error while respawning player {PlayerName}", player.Controller?.PlayerName ?? "Unknown");
        }

    }

    /// <summary>
    /// Closes the currently open menu for the specified player, if one exists.
    /// </summary>
    private void CloseMenuForPlayer(IPlayer player)
    {
        if (!IsBot(player) && IsPlayerValid(player))
        {
            var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
            if (currentMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
            }
        }
    }

    /// <summary>
    /// Formats a server ban command by replacing placeholders with the specified Steam ID, duration, and reason.
    /// </summary>
    private string FormatBanCommand(IPlayer? player)
    {
        if (player == null)
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("FormatBanCommand: player is null");
            
            return string.Empty;
        }

        var steamId = player.SteamID.ToString();
        var command = cfg.PlayerLeavePunishment.ServerCommand;
        command = command.Replace("{steamId}", steamId);
        command = command.Replace("{duration}", cfg.PlayerLeavePunishment.BanDurationMinutes.ToString());
        command = command.Replace("{reason}", cfg.PlayerLeavePunishment.BanReason);
        return command;
    }
}