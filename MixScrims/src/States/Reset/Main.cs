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
        canPlayerBeRespawned = true;
        isMovingPlayersToTeams = false;
        isFreezeTime = false;
        playerColors.Clear();
        recentlyDisconnectedPlayers.Clear();
        freshlyJoinedPlayers.Clear();
        foreach (var token in _punishmentTimers.Values) token.Cancel();
        _punishmentTimers.Clear();
        playersWaitingForPunishment.Clear();
        reservedCtSlots.Clear();
        reservedTSlots.Clear();
        forcedToSpectator.Clear();
        resetMixOnFirstJoin = false;
        CancelAutoResetOnLeaveTimer(announce: false);
        StopAllAnnouncmentTimers();
    }
}
