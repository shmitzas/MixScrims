using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Starts timeout if one is pending when a new round enter freezetime
    /// </summary>
    [GameEventHandler (HookMode.Pre)]
    public HookResult HandleTimeoutEventRoundPrestart(EventRoundPrestart @event)
    {
        isFreezeTime = true;

        if (cfg.DetailedLogging)
        {
            logger.LogInformation("HandleTimeoutEventRoundPrestart: Freeze time started. timeoutPending: {Pending}, isTimeoutActive: {IsActive}",
                timeoutPending, isTimeoutActive);
        }

        if (timeoutPending == TimeoutPending.CT)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("HandleTimeoutEventRoundPrestart: CT timeout is pending, calling StartTimeout");
            }
            StartTimeout(Team.CT);
        }

        if (timeoutPending == TimeoutPending.T)
        {
            if (cfg.DetailedLogging)
            {
                logger.LogInformation("HandleTimeoutEventRoundPrestart: T timeout is pending, calling StartTimeout");
            }
            StartTimeout(Team.T);
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Handles the end of the round freeze period when a timeout event occurs.
    /// </summary>
    [GameEventHandler(HookMode.Post)]
    public HookResult HandleTimeoutEventRoundFreezeEnd(EventRoundFreezeEnd @event)
    {
        isFreezeTime = false;
        return HookResult.Continue;
    }
}
