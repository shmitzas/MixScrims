using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Reset the plugin state to initial values
    ///</summary>
    private void ResetPluginState()
    {
        Core.Scheduler.NextTick(() =>
        {
            LoadSelectedMap(cfg.Maps.First());
            ResetVariables();
        });
    }

    /// <summary>
    /// Resets all match-related variables and state to their initial values.
    /// </summary>
    private void ResetVariables()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("ResetPluginState");

        mixScrimsService.SetMatchState(MatchState.Warmup);
        readyPlayers.Clear();
        playingCtPlayers.Clear();
        playingTPlayers.Clear();
        captainCt = null;
        captainT = null;
        SetTeamName(Team.CT);
        SetTeamName(Team.T);
        pickedCtPlayers.Clear();
        pickedTPlayers.Clear();
        votedMaps.Clear();
        timeoutCountCt = 3;
        timeoutCountT = 3;
        timeoutPending = TimeoutPending.None;
        canPlayerBeRespawned = true;
        surrenderVoteYesCount = 0;
        surrenderVoteNoCount = 0;
        surrenderVoteTeam = Team.None;
        StopAllAnnouncmentTimers();
    }
}
