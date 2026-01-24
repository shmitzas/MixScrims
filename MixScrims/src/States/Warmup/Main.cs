using Microsoft.Extensions.Logging;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Executes warmup configuration and restarts the game. Execued when a new match needs to be started.
    /// </summary>
    private void StartWarmup()
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
    private void LoadWarmupConfig()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Loading warmup configuration");

        Core.Scheduler.NextTick(() =>
        {
            Core.Engine.ExecuteCommand("exec mixscrims/warmup.cfg");
        });

        var pluginState = mixScrimsService.GetCurrentPluginState();

        if (pluginState == PluginState.Staging)
        {
            var token = Core.Scheduler.DelayBySeconds(3, () => Core.Engine.ExecuteCommand("exec mixscrims/staging_overrides.cfg"));
            Core.Scheduler.StopOnMapChange(token);
        }
        else
        {
            var token = Core.Scheduler.DelayBySeconds(3, () => Core.Engine.ExecuteCommand("exec mixscrims/production_overrides.cfg"));
            Core.Scheduler.StopOnMapChange(token);
        }

        canPlayerBeRespawned = true;

        StartAnnouncementTimers();
    }
}
