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

    // =========================================================================
    // Events (v2.0.0+)
    // =========================================================================
    //
    // Backing storage for every event on IMixScrims. Firing goes through the
    // Raise* helpers below so plugin code doesn't have to worry about null
    // subscriber lists, and every raise is wrapped in try/catch so a
    // misbehaving subscriber can't crash the state machine.

    public event Action<MatchState, MatchState>? MatchStateChanged;
    public event Action<PluginState, PluginState>? PluginStateChanged;
    public event Action<ulong, bool>? PlayerReadyChanged;
    public event Action<Team, ulong>? CaptainAssigned;
    public event Action<Team, ulong>? CaptainRemoved;
    public event Action<Team>? TeamPickingStarted;
    public event Action<Team, ulong, int>? PlayerPickedForTeam;
    public event Action<IReadOnlyList<string>, int>? MapVotingStarted;
    public event Action<ulong, string, string?>? MapVoteCast;
    public event Action<string, int>? MapVotingEnded;
    public event Action? KnifeRoundStarted;
    public event Action<Team>? KnifeRoundWon;
    public event Action<ulong>? PickingStartingSideStarted;
    public event Action<Team>? StartingSideChosen;
    public event Action<Team, int>? TimeoutStarted;
    public event Action<int>? TimeoutTick;
    public event Action<Team>? TimeoutEnded;
    public event Action<Team>? TimeoutVoteStarted;
    public event Action<ulong, bool, Team>? TimeoutVoteCast;
    public event Action<Team, bool>? TimeoutVoteResult;
    public event Action<Team>? SurrenderVoteStarted;
    public event Action<ulong, bool, Team>? SurrenderVoteCast;
    public event Action<Team, bool>? SurrenderVoteResult;
    public event Action<Team, ulong, ulong>? VoteKickStarted;
    public event Action<VoteKickCastEventArgs>? VoteKickCast;
    public event Action<Team, bool>? VoteKickResult;
    public event Action<Team, int, int>? MatchEnded;
    public event Action<ulong, Team?>? CaptainMenuRequested;
    public event Action<ulong, Team?>? VolunteerCaptainMenuRequested;
    public event Action<ulong>? MapVoteMenuRequested;

    // -- Raise* helpers ------------------------------------------------------
    //
    // Called from state-machine chokepoints inside the plugin. Each wraps the
    // invocation in try/catch — a subscriber that throws logs a warning and
    // the state machine continues. Null subscribers are a no-op.

    internal void RaiseMatchStateChanged(MatchState oldState, MatchState newState) => SafeInvoke(nameof(MatchStateChanged), MatchStateChanged, oldState, newState);
    internal void RaisePluginStateChanged(PluginState oldState, PluginState newState) => SafeInvoke(nameof(PluginStateChanged), PluginStateChanged, oldState, newState);
    internal void RaisePlayerReadyChanged(ulong steamId, bool isReady) => SafeInvoke(nameof(PlayerReadyChanged), PlayerReadyChanged, steamId, isReady);
    internal void RaiseCaptainAssigned(Team team, ulong steamId) => SafeInvoke(nameof(CaptainAssigned), CaptainAssigned, team, steamId);
    internal void RaiseCaptainRemoved(Team team, ulong oldSteamId) => SafeInvoke(nameof(CaptainRemoved), CaptainRemoved, team, oldSteamId);
    internal void RaiseTeamPickingStarted(Team startingTeam) => SafeInvoke(nameof(TeamPickingStarted), TeamPickingStarted, startingTeam);
    internal void RaisePlayerPickedForTeam(Team team, ulong pickedSteamId, int pickIndex) => SafeInvoke(nameof(PlayerPickedForTeam), PlayerPickedForTeam, team, pickedSteamId, pickIndex);
    internal void RaiseMapVotingStarted(IReadOnlyList<string> mapDisplayNames, int deadlineSeconds) => SafeInvoke(nameof(MapVotingStarted), MapVotingStarted, mapDisplayNames, deadlineSeconds);
    internal void RaiseMapVoteCast(ulong steamId, string mapDisplayName, string? previousMapDisplayName) => SafeInvoke(nameof(MapVoteCast), MapVoteCast, steamId, mapDisplayName, previousMapDisplayName);
    internal void RaiseMapVotingEnded(string winningMapDisplayName, int winningVoteCount) => SafeInvoke(nameof(MapVotingEnded), MapVotingEnded, winningMapDisplayName, winningVoteCount);
    internal void RaiseKnifeRoundStarted() => SafeInvoke(nameof(KnifeRoundStarted), KnifeRoundStarted);
    internal void RaiseKnifeRoundWon(Team winningTeam) => SafeInvoke(nameof(KnifeRoundWon), KnifeRoundWon, winningTeam);
    internal void RaisePickingStartingSideStarted(ulong winningCaptainSteamId) => SafeInvoke(nameof(PickingStartingSideStarted), PickingStartingSideStarted, winningCaptainSteamId);
    internal void RaiseStartingSideChosen(Team keepSide) => SafeInvoke(nameof(StartingSideChosen), StartingSideChosen, keepSide);
    internal void RaiseTimeoutStarted(Team team, int durationSeconds) => SafeInvoke(nameof(TimeoutStarted), TimeoutStarted, team, durationSeconds);
    internal void RaiseTimeoutTick(int remainingSeconds) => SafeInvoke(nameof(TimeoutTick), TimeoutTick, remainingSeconds);
    internal void RaiseTimeoutEnded(Team team) => SafeInvoke(nameof(TimeoutEnded), TimeoutEnded, team);
    internal void RaiseTimeoutVoteStarted(Team team) => SafeInvoke(nameof(TimeoutVoteStarted), TimeoutVoteStarted, team);
    internal void RaiseTimeoutVoteCast(ulong steamId, bool voteYes, Team team) => SafeInvoke(nameof(TimeoutVoteCast), TimeoutVoteCast, steamId, voteYes, team);
    internal void RaiseTimeoutVoteResult(Team team, bool passed) => SafeInvoke(nameof(TimeoutVoteResult), TimeoutVoteResult, team, passed);
    internal void RaiseSurrenderVoteStarted(Team team) => SafeInvoke(nameof(SurrenderVoteStarted), SurrenderVoteStarted, team);
    internal void RaiseSurrenderVoteCast(ulong steamId, bool voteYes, Team team) => SafeInvoke(nameof(SurrenderVoteCast), SurrenderVoteCast, steamId, voteYes, team);
    internal void RaiseSurrenderVoteResult(Team team, bool passed) => SafeInvoke(nameof(SurrenderVoteResult), SurrenderVoteResult, team, passed);
    internal void RaiseVoteKickStarted(Team team, ulong targetSteamId, ulong initiatorSteamId) => SafeInvoke(nameof(VoteKickStarted), VoteKickStarted, team, targetSteamId, initiatorSteamId);
    internal void RaiseVoteKickCast(VoteKickCastEventArgs args) => SafeInvoke(nameof(VoteKickCast), VoteKickCast, args);
    internal void RaiseVoteKickResult(Team team, bool passed) => SafeInvoke(nameof(VoteKickResult), VoteKickResult, team, passed);
    internal void RaiseMatchEnded(Team winningTeam, int ctScore, int tScore) => SafeInvoke(nameof(MatchEnded), MatchEnded, winningTeam, ctScore, tScore);
    internal void RaiseCaptainMenuRequested(ulong steamId, Team? side) => SafeInvoke(nameof(CaptainMenuRequested), CaptainMenuRequested, steamId, side);
    internal void RaiseVolunteerCaptainMenuRequested(ulong steamId, Team? side) => SafeInvoke(nameof(VolunteerCaptainMenuRequested), VolunteerCaptainMenuRequested, steamId, side);
    internal void RaiseMapVoteMenuRequested(ulong steamId) => SafeInvoke(nameof(MapVoteMenuRequested), MapVoteMenuRequested, steamId);

    private void SafeInvoke(string eventName, Action? handler)
    {
        if (handler == null) return;
        try { handler.Invoke(); }
        catch (Exception ex) { _mixScrims.logger?.LogWarning(ex, "MixScrims event handler for {Event} threw.", eventName); }
    }
    private void SafeInvoke<T1>(string eventName, Action<T1>? handler, T1 a1)
    {
        if (handler == null) return;
        try { handler.Invoke(a1); }
        catch (Exception ex) { _mixScrims.logger?.LogWarning(ex, "MixScrims event handler for {Event} threw.", eventName); }
    }
    private void SafeInvoke<T1, T2>(string eventName, Action<T1, T2>? handler, T1 a1, T2 a2)
    {
        if (handler == null) return;
        try { handler.Invoke(a1, a2); }
        catch (Exception ex) { _mixScrims.logger?.LogWarning(ex, "MixScrims event handler for {Event} threw.", eventName); }
    }
    private void SafeInvoke<T1, T2, T3>(string eventName, Action<T1, T2, T3>? handler, T1 a1, T2 a2, T3 a3)
    {
        if (handler == null) return;
        try { handler.Invoke(a1, a2, a3); }
        catch (Exception ex) { _mixScrims.logger?.LogWarning(ex, "MixScrims event handler for {Event} threw.", eventName); }
    }

    // =========================================================================
    // Snapshot queries (v2.0.0+)
    // =========================================================================

    public IReadOnlyList<ulong> GetReadyPlayers()
    {
        // readyPlayers holds IPlayer refs that can go stale after reconnects;
        // SafeSteamId + the != 0 filter drop those disposed entries so callers
        // don't see phantom 0-SteamIDs.
        return _mixScrims.readyPlayers
            .Select(p => _mixScrims.SafeSteamId(p))
            .Where(sid => sid != 0)
            .ToList();
    }

    public bool IsPlayerReady(ulong steamId)
    {
        if (steamId == 0) return false;
        return _mixScrims.readyPlayers.Any(p => _mixScrims.SafeSteamId(p) == steamId);
    }

    public int GetMinimumReadyPlayers() => _mixScrims.cfg.MinimumReadyPlayers;
    public int GetEffectiveReadyCount() => _mixScrims.GetEffectiveReadyCount();
    public int GetPlayersRequiredToStart() => _mixScrims.GetNumberOfPlayersRequiredToStart();
    public bool GetRequireAllConnectedPlayersToBeReady() => _mixScrims.cfg.RequireAllConnectedPlayersToBeReady;

    public ulong? GetCtCaptain()
    {
        var c = _mixScrims.captainCt;
        if (c == null || !_mixScrims.IsPlayerValid(c)) return null;
        try { return c.SteamID; } catch { return null; }
    }

    public ulong? GetTCaptain()
    {
        var c = _mixScrims.captainT;
        if (c == null || !_mixScrims.IsPlayerValid(c)) return null;
        try { return c.SteamID; } catch { return null; }
    }

    public string GetCtTeamName() => _mixScrims.ctTeamNameOverride ?? "COUNTER-TERRORISTS";
    public string GetTTeamName() => _mixScrims.tTeamNameOverride ?? "TERRORISTS";

    public (int Ct, int T) GetMatchScore()
    {
        try
        {
            var m = MixScrims.Core.Game.MatchData;
            return (m.CTScoreTotal, m.TerroristScoreTotal);
        }
        catch (Exception ex)
        {
            _mixScrims.logger?.LogDebug(ex, "GetMatchScore: MatchData unavailable, returning (0, 0).");
            return (0, 0);
        }
    }

    public IReadOnlyDictionary<string, int> GetMapVoteTallies()
    {
        // votedMaps carries the ballot tallies; empty outside MapVoting because
        // StartMapVotingPhase clears it and the flow only rebuilds when voting reopens.
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in _mixScrims.votedMaps)
        {
            if (v.Map == null || string.IsNullOrEmpty(v.Map.DisplayName)) continue;
            result[v.Map.DisplayName] = v.Votes;
        }
        return result;
    }

    public IReadOnlyList<string> GetVoteableMapDisplayNames()
    {
        var names = _mixScrims.currentBallotDisplayNames;
        if (names == null || names.Count == 0) return Array.Empty<string>();
        return names.ToList();
    }

    public int GetMapVoteSecondsRemaining()
    {
        var deadline = _mixScrims.mapVoteDeadline;
        if (deadline == null) return 0;
        var remaining = (int)Math.Ceiling((deadline.Value - DateTime.UtcNow).TotalSeconds);
        return remaining < 0 ? 0 : remaining;
    }

    public string? GetPlayerMapVote(ulong steamId)
    {
        if (steamId == 0) return null;
        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null) return null;
        var pid = player.PlayerID;
        foreach (var v in _mixScrims.votedMaps)
        {
            if (v.VotedBy.Contains(pid))
                return v.Map?.DisplayName;
        }
        return null;
    }

    public Team? GetActivePickingTeam() => _mixScrims.activePickingTeam;
    public IReadOnlyList<ulong> GetUnpickedPlayers()
    {
        if (_mixScrims.MatchState != MatchState.PickingTeam)
            return Array.Empty<ulong>();

        var pickedIds = new HashSet<ulong>();
        foreach (var p in _mixScrims.pickedCtPlayers) { var sid = _mixScrims.SafeSteamId(p); if (sid != 0) pickedIds.Add(sid); }
        foreach (var p in _mixScrims.pickedTPlayers) { var sid = _mixScrims.SafeSteamId(p); if (sid != 0) pickedIds.Add(sid); }

        var result = new List<ulong>();
        foreach (var p in _mixScrims.GetPlayers())
        {
            if (!_mixScrims.IsPlayerValid(p)) continue;
            ulong sid;
            try { sid = p.SteamID; } catch { continue; }
            if (sid == 0) continue;
            if (!pickedIds.Contains(sid)) result.Add(sid);
        }
        return result;
    }

    public IReadOnlyList<int> GetUnpickedPlayerSlots()
    {
        if (_mixScrims.MatchState != MatchState.PickingTeam)
            return Array.Empty<int>();

        // Slot-keyed so bots (SteamID 0) survive the filter — MixScrims' own
        // pick menu lists them, so a consumer-built menu must too.
        var pickedSlots = new HashSet<int>();
        foreach (var p in _mixScrims.pickedCtPlayers.Concat(_mixScrims.pickedTPlayers))
        {
            var slot = SafePlayerId(p);
            if (slot >= 0) pickedSlots.Add(slot);
        }

        var result = new List<int>();
        foreach (var p in _mixScrims.GetPlayers())
        {
            if (!_mixScrims.IsPlayerValid(p)) continue;
            var slot = SafePlayerId(p);
            if (slot < 0 || pickedSlots.Contains(slot)) continue;
            result.Add(slot);
        }
        return result;
    }
    public int GetCurrentPickIndex() => _mixScrims.currentPickIndex;

    public int GetActiveTimeoutRemainingSeconds()
    {
        // Mirror the tick-driven counter maintained by BroadcastRemainingTimeoutTime.
        return _mixScrims.activeTimeoutRemainingSeconds;
    }
    public Team? GetActiveTimeoutTeam() => _mixScrims.activeTimeoutTeam;
    public int GetRemainingTimeoutsCt() => _mixScrims.timeoutCountCt;
    public int GetRemainingTimeoutsT() => _mixScrims.timeoutCountT;

    public ulong? GetVoteKickTargetCt()
    {
        var t = _mixScrims.voteKickTargetCt;
        if (t == null || !_mixScrims.IsPlayerValid(t)) return null;
        try { return t.SteamID; } catch { return null; }
    }
    public ulong? GetVoteKickTargetT()
    {
        var t = _mixScrims.voteKickTargetT;
        if (t == null || !_mixScrims.IsPlayerValid(t)) return null;
        try { return t.SteamID; } catch { return null; }
    }
    public (int Yes, int Cast, int Eligible) GetVoteKickTallyCt() => (_mixScrims.voteKickYesCountCt, _mixScrims.voteKickTotalVotesCastCt, _mixScrims.voteKickEligibleVotesCt);
    public (int Yes, int Cast, int Eligible) GetVoteKickTallyT() => (_mixScrims.voteKickYesCountT, _mixScrims.voteKickTotalVotesCastT, _mixScrims.voteKickEligibleVotesT);

    public Team? GetActiveSurrenderVoteTeam() => _mixScrims.isSurrenderVoteInProgress ? _mixScrims.surrenderVoteTeam : (Team?)null;
    public (int Yes, int Cast, int Eligible) GetSurrenderVoteTally()
    {
        if (!_mixScrims.isSurrenderVoteInProgress) return (0, 0, 0);
        return (
            _mixScrims.surrenderVoteYesCount,
            _mixScrims.surrenderVoteYesCount + _mixScrims.surrenderVoteNoCount,
            _mixScrims.surrenderTotalEligibleVotes);
    }

    public string GetLocalizedString(ulong steamId, string key, params object[] args)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        try
        {
            if (steamId != 0)
            {
                var player = _mixScrims.GetPlayerBySteamId(steamId);
                if (player != null && _mixScrims.IsPlayerValid(player))
                {
                    var loc = MixScrims.Core.Translation.GetPlayerLocalizer(player);
                    return args is { Length: > 0 } ? loc[key, args] : loc[key];
                }
            }
            return args is { Length: > 0 } ? MixScrims.Core.Localizer[key, args] : MixScrims.Core.Localizer[key];
        }
        catch (Exception ex)
        {
            _mixScrims.logger?.LogWarning(ex, "GetLocalizedString: failed to resolve key {Key} for {SteamId}.", key, steamId);
            return key;
        }
    }

    // =========================================================================
    // Presentation suppression (v2.0.0+)
    // =========================================================================

    public void SetBuiltInMenusSuppressed(bool suppressed)
    {
        _mixScrims.suppressBuiltInMenus = suppressed;
        _mixScrims.logger?.LogInformation("SetBuiltInMenusSuppressed: {Suppressed}", suppressed);
    }
    public void SetBuiltInCenterHtmlSuppressed(bool suppressed)
    {
        _mixScrims.suppressBuiltInCenterHtml = suppressed;
        _mixScrims.logger?.LogInformation("SetBuiltInCenterHtmlSuppressed: {Suppressed}", suppressed);
    }
    public bool AreBuiltInMenusSuppressed() => _mixScrims.suppressBuiltInMenus;
    public bool IsBuiltInCenterHtmlSuppressed() => _mixScrims.suppressBuiltInCenterHtml;

    // =========================================================================
    // Config value getters (v2.0.0+)
    // =========================================================================

    public bool GetCaptainsEnabled() => !_mixScrims.cfg.DisableCaptains;

    public ulong? GetStartingSidePicker()
    {
        // Null for captains-disabled (the winning team votes instead) and for a
        // bot picker (SteamID 0), which MixScrims resolves automatically.
        if (_mixScrims.cfg.DisableCaptains) return null;
        if (_mixScrims.winnerCaptain is not { } captain) return null;
        var sid = _mixScrims.SafeSteamId(captain);
        return sid == 0 ? null : sid;
    }
    public bool GetSkipTeamPickingEnabled() => _mixScrims.cfg.SkipTeamPicking;
    public bool GetSkipMapVotingEnabled() => _mixScrims.cfg.SkipMapVoting;
    public bool GetAllowVolunteerCaptainsEnabled() => _mixScrims.cfg.AllowVolunteerCaptains;
    public int GetTimeoutDurationSeconds() => _mixScrims.cfg.TimeoutDurationSeconds;
    public int GetTotalTimeoutsPerTeam() => _mixScrims.cfg.Timeouts;
    public int GetDefaultVoteTimeSeconds() => _mixScrims.cfg.DefaultVoteTimeSeconds;

    // =========================================================================
    // Match flow drivers (v2.1.0+)
    // =========================================================================
    //
    // Guarded pass-throughs to the same internal handlers the built-in menu
    // buttons invoke, so the downstream pipeline (tallies, events, phase
    // progression) is identical. Authority stays inside MixScrims: nothing here
    // re-implements or bypasses the handler's own validation — these guards only
    // reject calls the built-in UI could never have produced.

    public void CastMapVote(ulong steamId, string mapDisplayName)
    {
        if (_mixScrims.MatchState != MatchState.MapVoting)
        {
            _mixScrims.logger?.LogWarning("CastMapVote: ignored, match state is {State} (expected MapVoting).", _mixScrims.MatchState);
            return;
        }
        if (string.IsNullOrWhiteSpace(mapDisplayName))
        {
            _mixScrims.logger?.LogWarning("CastMapVote: ignored, map display name is empty.");
            return;
        }

        var player = ResolveConnectedPlayer(steamId, nameof(CastMapVote));
        if (player == null) return;

        // Only the current ballot is votable — RegisterMapVoteByName would otherwise accept
        // any map in maps.jsonc, including ones excluded by DisallowVotePreviousMaps.
        var onBallot = _mixScrims.currentBallotDisplayNames
            .Any(n => string.Equals(n, mapDisplayName, StringComparison.OrdinalIgnoreCase));
        if (!onBallot)
        {
            _mixScrims.logger?.LogWarning("CastMapVote: ignored, {Map} is not on the current ballot.", mapDisplayName);
            return;
        }

        _mixScrims.RegisterMapVoteByName(player, mapDisplayName);
    }

    public void CastTimeoutVote(ulong steamId, bool voteYes)
    {
        if (!_mixScrims.isTimeoutVoteInProgress)
        {
            _mixScrims.logger?.LogWarning("CastTimeoutVote: ignored, no timeout vote is open.");
            return;
        }

        var player = ResolveConnectedPlayer(steamId, nameof(CastTimeoutVote));
        if (player == null) return;

        if (!IsPlayerOnTeam(player, _mixScrims.timeoutVoteTeam))
        {
            _mixScrims.logger?.LogWarning("CastTimeoutVote: ignored, {SteamId} is not on the voting team {Team}.", steamId, _mixScrims.timeoutVoteTeam);
            return;
        }
        if (_mixScrims.timeoutVoters.Contains(steamId))
        {
            _mixScrims.logger?.LogWarning("CastTimeoutVote: ignored, {SteamId} already voted.", steamId);
            return;
        }

        _mixScrims.HandleTimeoutVote(player, voteYes ? "Yes" : "No");
    }

    public void CastSurrenderVote(ulong steamId, bool voteYes)
    {
        if (!_mixScrims.isSurrenderVoteInProgress)
        {
            _mixScrims.logger?.LogWarning("CastSurrenderVote: ignored, no surrender vote is open.");
            return;
        }

        var player = ResolveConnectedPlayer(steamId, nameof(CastSurrenderVote));
        if (player == null) return;

        if (!IsPlayerOnTeam(player, _mixScrims.surrenderVoteTeam))
        {
            _mixScrims.logger?.LogWarning("CastSurrenderVote: ignored, {SteamId} is not on the voting team {Team}.", steamId, _mixScrims.surrenderVoteTeam);
            return;
        }
        if (_mixScrims.surrenderVoters.Contains(steamId))
        {
            _mixScrims.logger?.LogWarning("CastSurrenderVote: ignored, {SteamId} already voted.", steamId);
            return;
        }

        _mixScrims.HandleSurrenderVote(player, voteYes ? "Yes" : "No");
    }

    public void CastVoteKickVote(ulong steamId, Team team, bool voteYes)
    {
        if (team != Team.CT && team != Team.T)
        {
            _mixScrims.logger?.LogWarning("CastVoteKickVote: ignored, unsupported team {Team}.", team);
            return;
        }

        var inProgress = team == Team.CT ? _mixScrims.isVoteKickInProgressCt : _mixScrims.isVoteKickInProgressT;
        if (!inProgress)
        {
            _mixScrims.logger?.LogWarning("CastVoteKickVote: ignored, no {Team} vote kick is open.", team);
            return;
        }

        var player = ResolveConnectedPlayer(steamId, nameof(CastVoteKickVote));
        if (player == null) return;

        if (!IsPlayerOnTeam(player, team))
        {
            _mixScrims.logger?.LogWarning("CastVoteKickVote: ignored, {SteamId} is not on team {Team}.", steamId, team);
            return;
        }

        // HandleVoteKickVote owns the already-voted check via voteKickVotersCt/T.
        _mixScrims.HandleVoteKickVote(player, team, voteYes);
    }

    public void PickPlayerForTeam(ulong captainSteamId, ulong pickedSteamId)
    {
        if (_mixScrims.MatchState != MatchState.PickingTeam)
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeam: ignored, match state is {State} (expected PickingTeam).", _mixScrims.MatchState);
            return;
        }

        var activeTeam = _mixScrims.activePickingTeam;
        if (activeTeam == null)
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeam: ignored, no team is currently picking.");
            return;
        }

        var expectedCaptain = activeTeam == Team.CT ? _mixScrims.captainCt : _mixScrims.captainT;
        if (expectedCaptain == null || _mixScrims.SafeSteamId(expectedCaptain) != captainSteamId)
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeam: ignored, {SteamId} is not the active {Team} picker.", captainSteamId, activeTeam);
            return;
        }

        var picked = ResolveConnectedPlayer(pickedSteamId, nameof(PickPlayerForTeam));
        if (picked == null) return;

        if (_mixScrims.pickedCtPlayers.Any(p => _mixScrims.SafeSteamId(p) == pickedSteamId)
            || _mixScrims.pickedTPlayers.Any(p => _mixScrims.SafeSteamId(p) == pickedSteamId))
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeam: ignored, {SteamId} has already been picked.", pickedSteamId);
            return;
        }

        // AssignPickedPlayerToTeam* resolves by name (that's what the built-in menu passes).
        // Round-trip the name back through GetPlayerByName so a duplicate-name collision
        // can't silently route the pick onto a different player.
        var pickedName = picked.Name;
        if (string.IsNullOrEmpty(pickedName)
            || _mixScrims.SafeSteamId(_mixScrims.GetPlayerByName(pickedName)) != pickedSteamId)
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeam: ignored, {SteamId} has an empty or ambiguous name ({Name}).", pickedSteamId, pickedName);
            return;
        }

        if (activeTeam == Team.CT)
            _mixScrims.AssignPickedPlayerToTeamCt(expectedCaptain, pickedName);
        else
            _mixScrims.AssignPickedPlayerToTeamT(expectedCaptain, pickedName);
    }

    public void PickPlayerForTeamBySlot(ulong captainSteamId, int pickedSlot)
    {
        if (!TryResolveActivePicker(captainSteamId, nameof(PickPlayerForTeamBySlot), out var activeTeam, out var expectedCaptain))
            return;

        IPlayer? picked;
        try { picked = MixScrims.Core.PlayerManager.GetPlayer(pickedSlot); }
        catch (Exception ex)
        {
            _mixScrims.logger?.LogWarning(ex, "PickPlayerForTeamBySlot: slot {Slot} lookup threw.", pickedSlot);
            return;
        }

        if (picked == null || !_mixScrims.IsPlayerValid(picked))
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeamBySlot: ignored, no valid player in slot {Slot}.", pickedSlot);
            return;
        }

        // Bots share SteamID 0, so identity here is the slot — compare slots, not SteamIDs.
        if (_mixScrims.pickedCtPlayers.Concat(_mixScrims.pickedTPlayers).Any(p => SafePlayerId(p) == pickedSlot))
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeamBySlot: ignored, slot {Slot} has already been picked.", pickedSlot);
            return;
        }

        var pickedName = picked.Name;
        if (string.IsNullOrEmpty(pickedName)
            || SafePlayerId(_mixScrims.GetPlayerByName(pickedName)) != pickedSlot)
        {
            _mixScrims.logger?.LogWarning("PickPlayerForTeamBySlot: ignored, slot {Slot} has an empty or ambiguous name ({Name}).", pickedSlot, pickedName);
            return;
        }

        if (activeTeam == Team.CT)
            _mixScrims.AssignPickedPlayerToTeamCt(expectedCaptain, pickedName);
        else
            _mixScrims.AssignPickedPlayerToTeamT(expectedCaptain, pickedName);
    }

    /// <summary>Shared state + active-picker guard for both PickPlayerForTeam overloads.</summary>
    private bool TryResolveActivePicker(ulong captainSteamId, string caller, out Team activeTeam, out IPlayer expectedCaptain)
    {
        activeTeam = default;
        expectedCaptain = default!;

        if (_mixScrims.MatchState != MatchState.PickingTeam)
        {
            _mixScrims.logger?.LogWarning("{Caller}: ignored, match state is {State} (expected PickingTeam).", caller, _mixScrims.MatchState);
            return false;
        }

        if (_mixScrims.activePickingTeam is not { } team)
        {
            _mixScrims.logger?.LogWarning("{Caller}: ignored, no team is currently picking.", caller);
            return false;
        }
        activeTeam = team;

        var captain = activeTeam == Team.CT ? _mixScrims.captainCt : _mixScrims.captainT;
        if (captain == null || _mixScrims.SafeSteamId(captain) != captainSteamId)
        {
            _mixScrims.logger?.LogWarning("{Caller}: ignored, {SteamId} is not the active {Team} picker.", caller, captainSteamId, activeTeam);
            return false;
        }

        expectedCaptain = captain;
        return true;
    }

    /// <summary>Slot read that survives a disposed IPlayer, mirroring SafeSteamId.</summary>
    private static int SafePlayerId(IPlayer? player)
    {
        if (player == null) return -1;
        try { return player.PlayerID; }
        catch { return -1; }
    }

    public void VolunteerAsCaptain(ulong steamId, Team team)
    {
        var player = ResolveConnectedPlayer(steamId, nameof(VolunteerAsCaptain));
        if (player == null) return;

        // TryVolunteerCaptain owns the full eligibility chain so this driver and the
        // !volunteer_captain chat command can never drift apart.
        _mixScrims.TryVolunteerCaptain(player, team);
    }

    public void ChooseStartingSide(ulong steamId, bool stay)
    {
        var state = _mixScrims.MatchState;
        if (state != MatchState.PickingStartingSide)
        {
            _mixScrims.logger?.LogWarning("ChooseStartingSide: ignored, match state is {State} (expected PickingStartingSide).", state);
            return;
        }

        var player = ResolveConnectedPlayer(steamId, nameof(ChooseStartingSide));
        if (player == null) return;

        // HandleCaptainSideChoice owns eligibility (winning captain, or winning-team
        // membership + vote tallying when DisableCaptains is set) — same entry point
        // !stay / !switch and the built-in menu use.
        _mixScrims.HandleCaptainSideChoice(player, stay ? "Stay" : "Switch");
    }

    private IPlayer? ResolveConnectedPlayer(ulong steamId, string caller)
    {
        if (steamId == 0)
        {
            _mixScrims.logger?.LogWarning("{Caller}: ignored, SteamID is 0.", caller);
            return null;
        }

        var player = _mixScrims.GetPlayerBySteamId(steamId);
        if (player == null || !_mixScrims.IsPlayerValid(player))
        {
            _mixScrims.logger?.LogWarning("{Caller}: ignored, no connected player with SteamID {SteamId}.", caller, steamId);
            return null;
        }
        return player;
    }

    private bool IsPlayerOnTeam(IPlayer player, Team team)
    {
        try { return player.PlayerPawn != null && player.PlayerPawn.TeamNum == (int)team; }
        catch { return false; }
    }

    // =========================================================================
    // Original v1.x surface (preserved verbatim)
    // =========================================================================

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
            RaiseMatchStateChanged(previous, state);
        }
    }

    public void SetPluginState(PluginState state)
    {
        var previous = _mixScrims.PluginState;
        _mixScrims.PluginState = state;
        if (previous != state)
        {
            _mixScrims.logger.LogInformation("SetPluginState: {Previous} -> {New}", previous, state);
            RaisePluginStateChanged(previous, state);
        }
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
        // SafeSteamId on roster entries so a disposed IPlayer ref can't throw from the
        // RemoveAll predicate (same crash class fixed in Events.cs / ForceReady.cs).
        _mixScrims.pickedCtPlayers.RemoveAll(p => _mixScrims.SafeSteamId(p) == steamId);
    }

    public void RemovePlayerFromPickedTPlayers(ulong steamId)
    {
        _mixScrims.pickedTPlayers.RemoveAll(p => _mixScrims.SafeSteamId(p) == steamId);
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
        // SafeSteamId on roster entries so a disposed IPlayer ref can't throw from the
        // RemoveAll predicate.
        _mixScrims.playingCtPlayers.RemoveAll(p => _mixScrims.SafeSteamId(p) == steamId);
    }

    public void RemovePlayerFromPlayingTPlayers(ulong steamId)
    {
        _mixScrims.playingTPlayers.RemoveAll(p => _mixScrims.SafeSteamId(p) == steamId);
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
