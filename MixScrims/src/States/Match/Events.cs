using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Handles the end of a match and transitions the system to a fresh match state.
    /// </summary>
    [GameEventHandler (HookMode.Pre)]
    public HookResult HandleMatchEnd(EventCsWinPanelMatch @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Match)
        {
            Core.Scheduler.DelayBySeconds(10, () =>
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Match ended, transitioning to Fresh match state.");
                ResetPluginState();
            });
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Handles the halftime event during a match by preparing for team swap.
    /// </summary>
    [GameEventHandler (HookMode.Pre)]
    public HookResult HandleMatchHalftime(EventRoundAnnounceLastRoundHalf @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Match)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Match halftime announced - disabling team validation");
            
            isMovingPlayersToTeams = true;
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Handles the round start event, checking if halftime swap needs to be processed.
    /// </summary>
    [GameEventHandler(HookMode.Post)]
    public HookResult HandleRoundStart(EventRoundStart @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Match && isMovingPlayersToTeams)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Round started after halftime - updating team lists");
            
            Core.Scheduler.DelayBySeconds(1f, () =>
            {
                var oldPlayingCtPlayers = playingCtPlayers.ToList();
                var oldPlayingTPlayers = playingTPlayers.ToList();
                playingCtPlayers = oldPlayingTPlayers;
                playingTPlayers = oldPlayingCtPlayers;

                if (cfg.DetailedLogging)
                    logger.LogInformation($"Halftime team lists updated - CT: {playingCtPlayers.Count}, T: {playingTPlayers.Count}");

                Core.Scheduler.DelayBySeconds(1f, () =>
                {
                    isMovingPlayersToTeams = false;
                    if (cfg.DetailedLogging)
                        logger.LogInformation("Halftime complete - team validation re-enabled");
                });
            });
        }

        return HookResult.Continue;
    }
}
