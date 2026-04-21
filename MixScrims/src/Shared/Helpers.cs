using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public sealed partial class MixScrims
{
    /// <summary>
    /// Retrieves the server prefix to be used for command recognition or display.
    /// </summary>
    internal string GetServerPrefix()
    {
        var serverPrefix = cfg.GlobalServerPrefix;
        if (string.IsNullOrEmpty(serverPrefix))
        {
            serverPrefix = Core.Localizer["server_prefix"];
        }
        return serverPrefix;
    }

    /// <summary>
    /// Prints a message to a specified player.
    /// </summary>
    internal void PrintMessageToPlayer(IPlayer? player, string message)
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
            player.SendChat(GetServerPrefix() + " " + message);
        });
    }

    /// <summary>
    /// Prints a message to a list of specified players.
    /// </summary>
    internal void PrintMessageToCertainPlayers(List<IPlayer> players, string message)
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
    internal void PrintMessageToAllPlayers(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            logger.LogError("PrintMessageToAllPlayers: message is invalid");
            return;
        }

        Core.Scheduler.NextTick(() =>
        {
            Core.PlayerManager.SendChat(GetServerPrefix() + " " + message);
        });
    }

    /// <summary>
    /// Sends a message to all players in the specified team.
    /// </summary>
    internal void PrintMessageToTeam(Team team, string message)
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
    internal bool IsPlayerValid(IPlayer? player)
    {
        return player != null && player.IsValid;
    }

    /// <summary>
    /// Determines whether the specified player is a bot.
    /// </summary>
    internal bool IsBot(IPlayer? player)
    {
        return player != null && player.IsFakeClient;
    }

    /// <summary>
    /// Returns a list of all valid players.
    /// </summary>
    internal List<IPlayer> GetPlayers()
    {
        return Core.PlayerManager.GetAllValidPlayers().ToList();
    }

    /// <summary>
    /// Returns a list of players currently playing (CT or T).
    /// </summary>
    internal List<IPlayer> GetPlayingPlayers()
    {
        return GetPlayers()
            .Where(p => IsPlayerValid(p)
                && p.PlayerPawn != null
                && (p.PlayerPawn.TeamNum == 2
                || p.PlayerPawn.TeamNum == 3))
                .ToList()!;
    }

    /// <summary>
    /// Returns a list of players for a specified team.
    /// </summary>
    internal List<IPlayer> GetPlayersInTeam(Team team)
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
    internal List<IPlayer> GetNotReadyPlayers()
    {
        var allPlayers = GetPlayers();
        if (allPlayers.Count == 0)
            return new List<IPlayer>();

        return allPlayers.Where(player => !readyPlayers.Any(rp => rp.PlayerID == player.PlayerID)).ToList();
    }

    /// <summary>
    /// Returns a list of maps that can be voted for.
    /// </summary>
    internal List<MapDetails> GetMapsToVote()
    {
        return mapsConfig.Maps
            .Where(m => m.CanBeVoted && !playedMaps.Any(pm => pm.MapName == m.MapName)).ToList();
    }

    /// <summary>
    /// Determines the number of players required to start the game.
    /// </summary>
    internal int GetNumberOfPlayersRequiredToStart()
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
    internal IPlayer? GetPlayerByName(string playerName)
    {
        return GetPlayers().FirstOrDefault(p =>
            string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase));
    }

    internal IPlayer? GetPlayerBySteamId(ulong steamId)
    {
        return GetPlayers().FirstOrDefault(p => p.SteamID == steamId);
	}

    /// <summary>
    /// Pauses the match using cvar.
    /// </summary>
    internal void PauseMatch()
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
    internal void UnpauseMatch()
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
    internal MapDetails? GetMapByName(string mapName)
    {
        return mapsConfig.Maps.FirstOrDefault(m =>
            string.Equals(m.MapName, mapName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.DisplayName, mapName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Retrieves the map details associated with the specified workshop identifier.
    /// </summary>
	internal MapDetails? GetMapByWorkshopId(string workshopId)
	{
		return mapsConfig.Maps.FirstOrDefault(m =>
			string.Equals(m.WorkshopId, workshopId, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Respawns the specified player if they are eligible for respawn.
	/// </summary>
	internal void RespawnPlayer(IPlayer player)
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
                logger.LogWarning("RespawnPlayer: Player {PlayerName} is no longer valid, skipping respawn.", player.Name ?? "Unknown");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RespawnPlayer: Error while respawning player {PlayerName}", player.Name ?? "Unknown");
        }

    }

    /// <summary>
    /// Closes the currently open menu for the specified player, if one exists.
    /// </summary>
    internal void CloseMenuForPlayer(IPlayer player)
    {
        if (!IsBot(player) && IsPlayerValid(player))
        {
            Core.MenusAPI.CloseActiveMenu(player);
        }
    }

    /// <summary>
    /// Formats a server ban command by replacing placeholders with the specified Steam ID, duration, and reason.
    /// </summary>
    internal string FormatBanCommand(ulong steamId)
    {
        var command = cfg.PlayerLeavePunishment.ServerCommand;
        command = command.Replace("{steamId}", steamId.ToString());
        command = command.Replace("{duration}", cfg.PlayerLeavePunishment.BanDurationMinutes.ToString());
        command = command.Replace("{reason}", cfg.PlayerLeavePunishment.BanReason);
        return command;
    }
}
