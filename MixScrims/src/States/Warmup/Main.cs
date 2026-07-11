using Microsoft.Extensions.Logging;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Executes warmup configuration and restarts the game. Execued when a new match needs to be started.
    /// </summary>
    internal void StartWarmup()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Starting warmup");
        mixScrimsService.SetMatchState(MatchState.Warmup);

        UnpauseMatch();
        LoadWarmupConfig();
    }

    /// <summary>
    /// Loads the warmup configuration for the server and executes overrides based on the current plugin state
    /// state.
    internal void LoadWarmupConfig()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Loading warmup configuration");

        Core.Scheduler.NextTick(() =>
        {
            if (Core.Engine is { } engine)
                engine.ExecuteCommand("exec mixscrims/warmup.cfg");
            else
                logger.LogWarning("LoadWarmupConfig: Core.Engine unavailable; skipping warmup.cfg.");
        });

        // Single point of truth for "warmup cvars have just been re-applied". Set here
        // (not inside the NextTick) so the flag reflects committed intent even if the
        // exec itself is skipped due to a null engine — the LogWarning above already
        // surfaces that diagnostic path.
        warmupCvarsDirty = false;

        var pluginState = mixScrimsService.GetCurrentPluginState();

        if (pluginState == PluginState.Staging)
        {
            var token = Core.Scheduler.DelayBySeconds(3, () => 
            {
                Core.Scheduler.NextTick(() =>
                {
                    if (Core.Engine is { } engine)
                        engine.ExecuteCommand("exec mixscrims/staging_overrides.cfg");
                    else
                        logger.LogWarning("LoadWarmupConfig: Core.Engine unavailable; skipping staging_overrides.cfg.");
                });
            });
            Core.Scheduler.StopOnMapChange(token);
        }
        else
        {
            var token = Core.Scheduler.DelayBySeconds(3, () => 
            {
                Core.Scheduler.NextTick(() =>
                {
                    if (Core.Engine is { } engine)
                        engine.ExecuteCommand("exec mixscrims/production_overrides.cfg");
                    else
                        logger.LogWarning("LoadWarmupConfig: Core.Engine unavailable; skipping production_overrides.cfg.");
                });
            });
            Core.Scheduler.StopOnMapChange(token);
        }

        canPlayerBeRespawned = true;

        StartAnnouncementTimers();
    }
}
