using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public sealed partial class MixScrims
{
    // Cached display names last written via SetTeamName so IMixScrims.GetCt/TTeamName
    // can echo them back to consumers. Null means "engine default" (COUNTER-TERRORISTS / TERRORISTS).
    internal string? ctTeamNameOverride = null;
    internal string? tTeamNameOverride = null;

    /// <summary>
    /// Field-write helper that consolidates every <c>captainCt = ...</c> /
    /// <c>captainT = ...</c> assignment across the plugin so a single place
    /// fires the <see cref="MixScrimsService.CaptainAssigned"/> /
    /// <see cref="MixScrimsService.CaptainRemoved"/> events. When replacing a
    /// live captain: <c>CaptainRemoved</c> fires FIRST for the outgoing player,
    /// then <c>CaptainAssigned</c> for the incoming one. No-op assignments
    /// (same SteamID on the same team) don't refire either event.
    /// </summary>
    internal void AssignCaptain(Team team, IPlayer? player)
    {
        var current = team == Team.CT ? captainCt : captainT;
        var newValid = player != null && IsPlayerValid(player);
        var oldId = current != null && IsPlayerValid(current) ? SafeSteamId(current) : 0;
        ulong newId = 0;
        if (newValid)
        {
            try { newId = player!.SteamID; } catch { newId = 0; }
        }

        // No-op: same non-zero SteamID reassignment on the same team. Skip event fire so
        // idempotent code paths (EnsureCaptainsAlive → PickRandomCaptain returning the
        // same player) don't spam consumers.
        if (oldId != 0 && oldId == newId)
        {
            if (team == Team.CT) captainCt = player;
            else if (team == Team.T) captainT = player;
            return;
        }

        // Field write first, then event dispatch — subscribers reading the
        // corresponding snapshot getter (e.g. GetCtCaptain) inside their handler
        // will see the post-assignment state.
        if (team == Team.CT) captainCt = player;
        else if (team == Team.T) captainT = player;

        if (oldId != 0)
            mixScrimsService.RaiseCaptainRemoved(team, oldId);
        if (newId != 0)
            mixScrimsService.RaiseCaptainAssigned(team, newId);
    }

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
    /// Safe against stale/disposed IPlayer references whose property access throws.
    /// </summary>
    internal bool IsPlayerValid(IPlayer? player)
    {
        if (player == null) return false;
        try { return player.IsValid; }
        catch (ObjectDisposedException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Validates the current captain references and clears any that are null, disposed, or otherwise
    /// invalid. If a captain reference is cleared and the corresponding team has playing/picked players
    /// available, attempts to re-pick a replacement captain from those rosters.
    /// </summary>
    internal void EnsureCaptainsAlive()
    {
        if (captainCt != null && !IsPlayerValid(captainCt))
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("EnsureCaptainsAlive: CT captain reference is invalid/disposed, clearing.");
            AssignCaptain(Team.CT, null);
        }

        if (captainT != null && !IsPlayerValid(captainT))
        {
            if (cfg.DetailedLogging)
                logger.LogWarning("EnsureCaptainsAlive: T captain reference is invalid/disposed, clearing.");
            AssignCaptain(Team.T, null);
        }

        if (captainCt == null)
        {
            IPlayer? replacement = null;
            if (playingCtPlayers.Count > 0)
                replacement = playingCtPlayers.FirstOrDefault(p => IsPlayerValid(p) && !IsBot(p))
                              ?? playingCtPlayers.FirstOrDefault(p => IsPlayerValid(p));
            if (replacement == null && pickedCtPlayers.Count > 0)
                replacement = pickedCtPlayers.FirstOrDefault(p => IsPlayerValid(p) && !IsBot(p))
                              ?? pickedCtPlayers.FirstOrDefault(p => IsPlayerValid(p));
            if (replacement == null)
                replacement = PickRandomCaptain(Team.CT);
            if (replacement != null)
            {
                AssignCaptain(Team.CT, replacement);
                SetCaptainClanTag(replacement, Team.CT);
                if (cfg.DetailedLogging)
                    logger.LogInformation("EnsureCaptainsAlive: Re-picked CT captain: {Name}", replacement.Name);
            }
        }

        if (captainT == null)
        {
            IPlayer? replacement = null;
            if (playingTPlayers.Count > 0)
                replacement = playingTPlayers.FirstOrDefault(p => IsPlayerValid(p) && !IsBot(p))
                              ?? playingTPlayers.FirstOrDefault(p => IsPlayerValid(p));
            if (replacement == null && pickedTPlayers.Count > 0)
                replacement = pickedTPlayers.FirstOrDefault(p => IsPlayerValid(p) && !IsBot(p))
                              ?? pickedTPlayers.FirstOrDefault(p => IsPlayerValid(p));
            if (replacement == null)
                replacement = PickRandomCaptain(Team.T);
            if (replacement != null)
            {
                AssignCaptain(Team.T, replacement);
                SetCaptainClanTag(replacement, Team.T);
                if (cfg.DetailedLogging)
                    logger.LogInformation("EnsureCaptainsAlive: Re-picked T captain: {Name}", replacement.Name);
            }
        }
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
    /// Returns a list of players who haven't readied up yet. In TestMode bots are treated as
    /// implicitly ready and excluded from this list, so the "not ready: ..." announcement and
    /// the center-HTML display don't drown in bot names during scrim/staging sessions.
    /// </summary>
    internal List<IPlayer> GetNotReadyPlayers()
    {
        var allPlayers = GetPlayers();
        if (allPlayers.Count == 0)
            return new List<IPlayer>();

        return allPlayers
            .Where(player => !(cfg.TestMode && IsBot(player)))
            .Where(player =>
            {
                // Cache the live player's SteamID once, and use SafeSteamId on each roster
                // entry: readyPlayers can carry disposed IPlayer refs, and a raw .SteamID
                // read on a disposed entry throws ObjectDisposedException. The != 0 guard
                // skips disposed ghosts so a real player never gets falsely matched to one.
                var playerId = player.SteamID;
                return !readyPlayers.Any(rp =>
                {
                    var rpId = SafeSteamId(rp);
                    return rpId != 0 && rpId == playerId;
                });
            })
            .ToList();
    }

    /// <summary>
    /// Effective ready count used for state-transition decisions and UI counters. In TestMode
    /// every connected bot is counted as implicitly ready on top of the human ready list, so
    /// a lone human running !forceready in a bot-filled staging lobby can finalize the match.
    /// Bots are never inserted into <see cref="readyPlayers"/> itself because they all share
    /// SteamID 0, which would collapse them onto a single dedup slot.
    /// </summary>
    internal int GetEffectiveReadyCount()
    {
        if (!cfg.TestMode)
            return readyPlayers.Count;

        int botCount = GetPlayers().Count(IsBot);
        return readyPlayers.Count + botCount;
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
            if (Core.Engine is { } engine)
                engine.ExecuteCommand("mp_pause_match");
            else
                logger.LogWarning("PauseMatch: Core.Engine unavailable; skipping mp_pause_match.");
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
            if (Core.Engine is { } engine)
                engine.ExecuteCommand("mp_unpause_match");
            else
                logger.LogWarning("UnpauseMatch: Core.Engine unavailable; skipping mp_unpause_match.");
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

    // Throttles disposed-reference warnings: one warning per unique IPlayer instance (identity
    // hash from RuntimeHelpers.GetHashCode, which is safe on disposed objects because it does
    // not touch the disposed instance's own state). Prevents log floods when a disposed ref
    // stays in a tracked list and is polled on every tick.
    private readonly HashSet<int> _loggedDisposedPlayerHashes = new();

    /// <summary>
    /// Safely reads <see cref="IPlayer.SteamID"/> from a possibly-null or possibly-disposed
    /// player reference. Returns <c>0UL</c> on any failure (null, ObjectDisposedException,
    /// or any other exception). Use this for LINQ predicates and projections over the plugin's
    /// stored roster lists (<c>playingCtPlayers</c>, <c>playingTPlayers</c>, <c>pickedCtPlayers</c>,
    /// <c>pickedTPlayers</c>, <c>readyPlayers</c>) — SwiftlyS2 may dispose the underlying Player
    /// object between the time we added it and the time we read it, and every direct property
    /// access throws <see cref="ObjectDisposedException"/> on a disposed object.
    /// </summary>
    /// <remarks>
    /// Real players always have a non-zero SteamID; only bots return <c>0UL</c> from a live
    /// read. Rosters never contain bots (they are filtered upstream), so a returned <c>0UL</c>
    /// from a roster entry unambiguously means "disposed".
    /// </remarks>
    internal ulong SafeSteamId(IPlayer? player)
    {
        if (player is null) return 0UL;
        try
        {
            return player.SteamID;
        }
        catch (ObjectDisposedException)
        {
            LogDisposedIfNew(player, "SafeSteamId");
            return 0UL;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SafeSteamId: unexpected error reading SteamID from tracked player reference.");
            return 0UL;
        }
    }

    /// <summary>
    /// Safely reads <see cref="IPlayer.PlayerID"/> from a possibly-null or possibly-disposed
    /// player reference. Returns <c>-1</c> on any failure. Same disposal-safety rationale as
    /// <see cref="SafeSteamId"/>.
    /// </summary>
    internal int SafePlayerId(IPlayer? player)
    {
        if (player is null) return -1;
        try
        {
            return player.PlayerID;
        }
        catch (ObjectDisposedException)
        {
            LogDisposedIfNew(player, "SafePlayerId");
            return -1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SafePlayerId: unexpected error reading PlayerID from tracked player reference.");
            return -1;
        }
    }

    /// <summary>
    /// Safely reads <see cref="IPlayer.Controller"/>.<c>PlayerName</c> from a possibly-null or
    /// possibly-disposed player reference. Returns a sentinel string (<c>&lt;null&gt;</c>,
    /// <c>&lt;disposed&gt;</c>, or <c>&lt;error&gt;</c>) on any failure, and a <c>Slot {id}</c>
    /// fallback (via <see cref="SafePlayerId"/>) when the controller itself is null or its
    /// <c>PlayerName</c> is null. Use this for structured-log <c>{PlayerName}</c> placeholders
    /// over the plugin's stored roster lists so log formatting never throws when SwiftlyS2 has
    /// disposed the underlying Player object between the time we added it and the time we read
    /// it. Same disposal-safety rationale as <see cref="SafeSteamId"/>.
    /// </summary>
    internal string SafePlayerName(IPlayer? player)
    {
        if (player is null) return "<null>";
        try
        {
            var controller = player.Controller;
            if (controller is null) return $"Slot {SafePlayerId(player)}";
            return controller.PlayerName ?? $"Slot {SafePlayerId(player)}";
        }
        catch (ObjectDisposedException)
        {
            LogDisposedIfNew(player, "SafePlayerName");
            return "<disposed>";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SafePlayerName: unexpected error reading PlayerName from tracked player reference.");
            return "<error>";
        }
    }

    private void LogDisposedIfNew(IPlayer player, string context)
    {
        var identity = RuntimeHelpers.GetHashCode(player);
        if (_loggedDisposedPlayerHashes.Add(identity))
        {
            logger.LogWarning("{Context}: encountered disposed IPlayer reference (identity=0x{Identity:X}) in a tracked collection; skipping this entry. Subsequent hits on the same reference are suppressed. See HandleDisconnectedPlayer roster cleanup for the release path.",
                context, identity);
        }
    }
}
