namespace MixScrims.Contract;

public interface IMixScrims : IDisposable
{
    /// <summary>
    /// Retrieves the current state of the match.
    /// </summary>
    MatchState GetCurrentMatchState();
    /// <summary>
    /// Sets the current match state to the specified value.
    /// </summary>
    void SetMatchState(MatchState state);
    /// <summary>
    /// Retrieves the current operational state of the plugin.
    /// </summary>
    PluginState GetCurrentPluginState();
    /// <summary>
    /// Sets the current state of the plugin.
    /// </summary>
    void SetPluginState(PluginState state);
    /// <summary>
    /// Sets the display name for the Counter-Terrorists team.
    /// </summary>
    void SetCounterTerroristsTeamName(string name);
    /// <summary>
    /// Sets the display name for the terrorists team.
    /// </summary>
    void SetTerroristsTeamName(string name);
    /// <summary>
    /// Initiates the warmup process to prepare the component for operation.
    /// </summary>
    void StartWarmup();
    /// <summary>
    /// Initiates the map voting process, allowing participants to vote for the next map.
    /// </summary>
    void StartMapVoting();
    /// <summary>
    /// Initiates the process of selecting teams for the current session.
    /// </summary>
    void StartTeamPicking();
    /// <summary>
    /// Starts the timeout cancellation token, enabling timeout monitoring for the current operation.
    /// </summary>
    void StartTimeoutCt();
    /// <summary>
    /// 
    /// </summary>
    void StartTimeoutT();
    /// <summary>
    /// Stops the timeout cancellation token, preventing any further timeout-triggered cancellation operations.
    /// </summary>
    void StopTimeout();
    /// <summary>
    /// Initiates the surrender process for the current Counter-Terrorist team.
    /// </summary>
    void SurrenderCt();
    /// <summary>
    /// Performs a surrender action for the current entity or operation.
    /// </summary>
    void SurrenderT();
    /// <summary>
    /// Begins a new match, initializing all necessary game state and resources.
    /// </summary>
    void StartMatch();
    /// <summary>
    /// Starts a knife round phase in the game, typically used to determine which team selects a side.
    /// </summary>
    void StartKnifeRound();
    /// <summary>
    /// Cancels the current match, terminating any ongoing gameplay or matchmaking process.
    /// </summary>
    void CancelMatch();
    /// <summary>
    /// Changes the current map to the specified map or workshop map.
    /// </summary>
    void ChangeMap(string mapName = "", string workshopId = "");
    /// <summary>
    /// Sets all players in the game to a ready state, regardless of their current status.
    /// </summary>
    void ForceAllPlayersToReady();
    /// <summary>
    /// Forces all players in the session to become unready, overriding their current ready status.
    /// </summary>
    void ForceAllPlayersToUnready();
    /// <summary>
    /// Retrieves a list of player identifiers that have been selected for the Counter-Terrorist team.
    /// </summary>
    List<ulong> GetPickedCtPlayers();
    /// <summary>
    /// Retrieves a list of player identifiers that have been selected.
    /// </summary>
    List<ulong> GetPickedTPlayers();
    /// <summary>
    /// Adds a player to the collection of picked players using the specified Steam ID.
    /// </summary>
    void AddPlayerToPickedCtPlayers(ulong steamId);
    /// <summary>
    /// Adds a player to the collection of picked players using the specified Steam ID.
    /// </summary>
    void AddPlayerToPickedTPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of picked players.
    /// </summary>
    void RemovePlayerFromPickedCtPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of picked players.
    /// </summary>
    void RemovePlayerFromPickedTPlayers(ulong steamId);
    /// <summary>
    /// Retrieves a list of player identifiers for all players currently in the playing state.
    /// </summary>
    List<ulong> GetPlayingCtPlayers();
    /// <summary>
    /// Retrieves a list of player identifiers for all players currently in a playing state.
    /// </summary>
    List<ulong> GetPlayingTPlayers();
    /// <summary>
    /// Adds a player to the collection of currently playing players using the specified Steam ID.
    /// </summary>
    void AddPlayerToPlayingCtPlayers(ulong steamId);
    /// <summary>
    /// Adds a player to the collection of currently playing players using the specified Steam ID.
    /// </summary>
    void AddPlayerToPlayingTPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of currently playing players.
    /// </summary>
    void RemovePlayerFromPlayingCtPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of currently playing players.
    /// </summary>
    void RemovePlayerFromPlayingTPlayers(ulong steamId);
    /// <summary>
    /// Retrieves a list of player Steam IDs who are awaiting punishment actions associated with the specified user.
    /// </summary>
    List<ulong> GetPlayersWaitingForPunishment(ulong steamId);
    /// <summary>
    /// Adds a player, identified by their Steam ID, from the waiting-for-punishment list to the active player list or
    /// system.
    /// </summary>
    void AddPlayerToWaitingForPunishmentList(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the waiting-for-punishmentslist.
    /// </summary>
    void RemovePlayerFromWaitingForPunishmentList(ulong steamId);
    /// <summary>
    /// Assigns the captain role to the player identified by the specified Steam ID.
    /// </summary>
    void SetCtCaptain(ulong steamId);
    /// <summary>
    /// Assigns the captain role to the player identified by the specified Steam ID.
    /// </summary>
    void SetTCaptain(ulong steamId);
    /// <summary>
    /// Kicks players who are not actively participating in the game.
    /// </summary>
    void KickNotPlayingPlayers(string? reason = "");
    /// <summary>
    /// Removes all players who have not been picked from the game session.
    /// </summary>
    void KickNotPickedPlayers(string? reason = "");
    /// <summary>
    /// Prevents additional players from joining the ongoing match. This is automatically disabled when the match ends.
    /// </summary>
    void PreventNewPlayersJoining(bool value = false);
}
