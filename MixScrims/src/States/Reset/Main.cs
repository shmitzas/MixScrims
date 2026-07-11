using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Reset the plugin state to initial values
    ///</summary>
    internal void ResetPluginState()
    {
        Core.Scheduler.NextTick(() =>
        {
            ResetVariables();
            StartWarmup();
        });
    }

    /// <summary>
    /// Resets all match-related variables and state to their initial values.
    /// </summary>
    internal void ResetVariables()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("ResetPluginState");

        mixScrimsService.SetMatchState(MatchState.Warmup);
        readyPlayers.Clear();
        playingCtPlayers.Clear();
        playingTPlayers.Clear();
        // Strip captain tags before dropping the captain refs so any player carrying one
        // (from an aborted PickingTeam / KnifeRound flow) gets it cleared here as well.
        RemoveCaptainClanTagsFromAllPlayers();
        captainCt = null;
        captainT = null;
        winnerCaptain = null;
        SetTeamName(Team.CT);
        SetTeamName(Team.T);
        pickedCtPlayers.Clear();
        pickedTPlayers.Clear();
        votedMaps.Clear();
        sideVotes.Clear();
        sideVoteWinnerTeam = Team.None;
        timeoutCountCt = cfg.Timeouts;
        timeoutCountT = cfg.Timeouts;
        timeoutPending = TimeoutPending.None;
        timeoutQueue.Clear();
        isTimeoutActive = false;
        isTimeoutVoteInProgress = false;
        timeoutVoteTeam = Team.None;
        timeoutVoteTimer?.Cancel();
        timeoutVoteTimer = null;
        isSurrenderVoteInProgress = false;
        surrenderVoteTimer?.Cancel();
        surrenderVoteTimer = null;
        surrenderVoteYesCount = 0;
        surrenderVoteNoCount = 0;
        surrenderVoteTeam = Team.None;
        ResetVoteKickState(Team.CT);
        ResetVoteKickState(Team.T);
        ResetRtvState();
        canPlayerBeRespawned = true;
        isMovingPlayersToTeams = false;
        isFreezeTime = false;
        playerColors.Clear();
        recentlyDisconnectedPlayers.Clear();
        freshlyJoinedPlayers.Clear();
        foreach (var token in _punishmentTimers.Values) token.Cancel();
        _punishmentTimers.Clear();
        playersWaitingForPunishment.Clear();
        forcedToSpectator.Clear();
        resetMixOnFirstJoin = false;
        // Assume cvars might be dirty across any reset path (plugin load, explicit reset,
        // auto-reset-on-leave). The immediate StartWarmup() -> LoadWarmupConfig() that
        // follows every ResetVariables() call clears the flag once warmup.cfg is scheduled
        // for exec, so the steady-state after a clean boot is false.
        warmupCvarsDirty = true;
        stateBeforeMapLoading = null;
        mapLoadedFromMatchFlow = false;
        CancelAutoResetOnLeaveTimer(announce: false);
        StopAllAnnouncmentTimers();
    }
}
