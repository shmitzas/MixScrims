using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public class MixScrimsService : IMixScrims
{
    MixScrims _mixScrims {  get; set; }

    public MixScrimsService(MixScrims mixScrims)
    {
        _mixScrims = mixScrims;
    }

    public MatchState GetCurrentMatchState()
    {
        return _mixScrims.MatchState;
    }

    public PluginState GetCurrentPluginState()
    {
        return _mixScrims.PluginState;
    }

    public void SetMatchState(MatchState state)
    {
        var previous = _mixScrims.MatchState;
        _mixScrims.MatchState = state;
        // State transitions are infrequent and high-signal. Log unconditionally (not gated
        // by DetailedLogging) so issues like \"OT side switch dumps players to spec\" can be
        // correlated with the surrounding state machine activity even on production servers.
        if (previous != state)
        {
            _mixScrims.logger.LogInformation("SetMatchState: {Previous} -> {New} (playing CT:{Ct}/T:{T}, picked CT:{PCt}/T:{PT}, ready:{Ready})",
                previous, state,
                _mixScrims.playingCtPlayers.Count, _mixScrims.playingTPlayers.Count,
                _mixScrims.pickedCtPlayers.Count, _mixScrims.pickedTPlayers.Count,
                _mixScrims.readyPlayers.Count);
        }
    }

    public void SetPluginState(PluginState state)
    {
        var previous = _mixScrims.PluginState;
        _mixScrims.PluginState = state;
        if (previous != state)
            _mixScrims.logger.LogInformation("SetPluginState: {Previous} -> {New}", previous, state);
    }

    public void SetCounterTerroristsTeamName(string name)
    {
        _mixScrims.SetTeamName(Team.CT,  name);
    }

    public void SetTerroristsTeamName(string name)
    {
        _mixScrims.SetTeamName(Team.T, name);
    }

    public void StartWarmup()
    {
        _mixScrims.StartWarmup();
    }

    public void StartMapVoting()
    {
        _mixScrims.StartMapVotingPhase();
    }

    public void StartTeamPicking()
    {
        _mixScrims.StartTeamPickingPhase();
    }

    public void StartTimeoutCt()
    {
        _mixScrims.StartTimeout(Team.CT);
    }

    public void StartTimeoutT()
    {
        _mixScrims.StartTimeout(Team.T);
    }

    public void StopTimeout()
    {
        _mixScrims.EndTimeout();
    }

    public void SurrenderCt()
    {
        _mixScrims.Surrender(Team.CT);
    }

    public void SurrenderT()
    {
        _mixScrims.Surrender(Team.T);
    }

    public void StartMatch()
    {
        _mixScrims.StartMatch();
    }

    public void StartKnifeRound()
    {
        _mixScrims.StartKnifeRound();
    }

    public void CancelMatch()
    {
        _mixScrims.ResetPluginState();
    }

    public void ChangeMap(string mapName = "", string workshopId = "")
    {
        if (string.IsNullOrEmpty(mapName) && string.IsNullOrEmpty(workshopId))
        {
            _mixScrims.logger.LogError("ChangeMap: Both mapName and workshopId cannot be empty. Please provide at least one.");
            return;
        }

        // Debounce: refuse external map-change requests while another transition is already
        // in flight. Stacking host_workshop_map / map commands across plugins is a known
        // CS2 server crash trigger during the map-transition window.
        var currentState = _mixScrims.mixScrimsService.GetCurrentMatchState();
        if (currentState == MatchState.MapLoading || currentState == MatchState.MapChosen)
        {
            _mixScrims.logger.LogWarning("ChangeMap: ignoring external request to {Map}/{Workshop} - map change already in progress (state={State}).", mapName, workshopId, currentState);
            return;
        }

        if (!string.IsNullOrEmpty(workshopId))
        {
            var map = _mixScrims.GetMapByWorkshopId(workshopId);
            if (map == null)
            {
                _mixScrims.logger.LogError("ChangeMap: Map with workshop ID {WorkshopId} was not found in the config.", workshopId);
                return;
            }
            _mixScrims.LoadSelectedMap(map);
            return;
        }

        var mapByName = _mixScrims.GetMapByName(mapName);
        if (mapByName == null)
        {
            _mixScrims.logger.LogError("ChangeMap: Map with name {MapName} was not found in the config.", mapName);
            return;
        }
        _mixScrims.LoadSelectedMap(mapByName);
    }

    public void ForceAllPlayersToReady()
    {
        _mixScrims.ForceReadyAllPlayers();
    }

    public void ForceAllPlayersToUnready()
    {
        _mixScrims.ForceUnreadyAllPlayers();
    }

    public List<ulong> GetPickedCtPlayers()
    {
        return _mixScrims.pickedCtPlayers.Select(player => player.SteamID).ToList();
    }

    public List<ulong> GetPickedTPlayers()
    {
        return _mixScrims.pickedTPlayers.Select(player => player.SteamID).ToList();
    }

    public void AddPlayerToPickedCtPlayers(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("AddPlayerToPickedCtPlayers: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.pickedCtPlayers.Add(player);
    }

    public void AddPlayerToPickedTPlayers(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("AddPlayerToPickedTPlayers: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.pickedTPlayers.Add(player);
    }

    public void RemovePlayerFromPickedCtPlayers(ulong steamId)
    {
        _mixScrims.pickedCtPlayers.RemoveAll(p => p.SteamID == steamId);
    }

    public void RemovePlayerFromPickedTPlayers(ulong steamId)
    {
        _mixScrims.pickedTPlayers.RemoveAll(p => p.SteamID == steamId);
    }

    public List<ulong> GetPlayingCtPlayers()
    {
        return _mixScrims.playingCtPlayers.Select(player => player.SteamID).ToList();
    }

    public List<ulong> GetPlayingTPlayers()
    {
        return _mixScrims.playingTPlayers.Select(player => player.SteamID).ToList();
    }

    public void AddPlayerToPlayingCtPlayers(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("AddPlayerToPlayingCtPlayers: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.playingCtPlayers.Add(player);
    }

    public void AddPlayerToPlayingTPlayers(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("AddPlayerToPlayingTPlayers: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.playingTPlayers.Add(player);
    }

    public void RemovePlayerFromPlayingCtPlayers(ulong steamId)
    {
        _mixScrims.playingCtPlayers.RemoveAll(p => p.SteamID == steamId);
    }

    public void RemovePlayerFromPlayingTPlayers(ulong steamId)
    {
        _mixScrims.playingTPlayers.RemoveAll(p => p.SteamID == steamId);
    }

    public List<ulong> GetPlayersWaitingForPunishment(ulong steamId)
    {
        return _mixScrims.playersWaitingForPunishment.ToList();
    }

    public void RemovePlayerFromWaitingForPunishmentList(ulong steamId)
    {
        if (!_mixScrims.playersWaitingForPunishment.Contains(steamId))
        {
            _mixScrims.logger.LogWarning("RemovePlayerFromWaitingForPunishmentsList: Player with Steam ID {SteamId} is not in the waiting-for-punishment list.", steamId);
            return;
        }
        _mixScrims.playersWaitingForPunishment.Remove(steamId);
    }

    public void AddPlayerToWaitingForPunishmentList(ulong steamId)
    {
        if (_mixScrims.playersWaitingForPunishment.Contains(steamId))
        {
            _mixScrims.logger.LogWarning("AddPlayerToWaitingForPunishmentsList: Player with Steam ID {SteamId} is already in the waiting-for-punishment list.", steamId);
            return;
        }
        _mixScrims.QueuePlayerPunishment(steamId);
    }

    public void SetCtCaptain(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("SetCtCaptain: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.PickCtCaptain(player);
    }

    public void SetTCaptain(ulong steamId)
    {
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null)
        {
            _mixScrims.logger.LogError("SetTCaptain: Player with Steam ID {SteamId} was not found.", steamId);
            return;
        }
        _mixScrims.PickTCaptain(player);
    }

    public void KickNotPlayingPlayers(string? reason = "")
    {
        var players = _mixScrims.GetPlayers();
        var playingPlayers = _mixScrims.playingCtPlayers.Concat(_mixScrims.playingTPlayers).Select(p => p.SteamID).ToHashSet();
        var notPlayingPlayers = players.Where(p => !playingPlayers.Contains(p.SteamID)).ToList();
        foreach(var player in notPlayingPlayers)
        {
            _mixScrims.KickPlayer(player.SteamID, reason);
        }
    }

    public void KickNotPickedPlayers(string? reason = "")
    {
        var players = _mixScrims.GetPlayers();
        var pickedPlayers = _mixScrims.pickedCtPlayers.Concat(_mixScrims.pickedTPlayers).Select(p => p.SteamID).ToHashSet();
        var notPickedPlayers = players.Where(p => !pickedPlayers.Contains(p.SteamID)).ToList();
        foreach(var player in notPickedPlayers)
        {
            _mixScrims.KickPlayer(player.SteamID, reason);
        }
    }

    public void PreventNewPlayersJoining(bool value = false)
    {
        _mixScrims.preventNotPickedPlayersFromJoiningOngoingMatch = value;
    }

    public void Dispose()
    {
        // Cleanup resources if needed
        // Currently no unmanaged resources to dispose
    }
}
