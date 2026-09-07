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

    // Map that match_base.cfg was last applied on.
    private string? matchBaseCfgMap;

    /// <summary>
    /// Applies the ~90 match cvars that no phase cfg ever overrides, once per map.
    /// </summary>
    /// <remarks>
    /// These used to live in match_start.cfg. Exec'ing all ~112 cvars there froze the server
    /// for 2-4s at every single match start — the engine skipped 130-240 ticks and every
    /// client reported <c>high frame misdelivery</c>. The three smaller phase cfgs (36-41
    /// cvars) never did. Doing it during warmup keeps the cost where nobody is playing.
    /// Safe to run only once per map because warmup.cfg / teampick.cfg / knife_round.cfg
    /// never set any of these to a different value — anything they do change is restored by
    /// match_start.cfg instead.
    /// </remarks>
    internal void LoadMatchBaseConfig()
    {
        // Deferred like every other exec here: StartWarmup runs during plugin load, where
        // Core.Engine exists but GlobalVars does not yet and throws on access.
        Core.Scheduler.NextTick(() =>
        {
            if (Core.Engine is not { } engine)
            {
                logger.LogWarning("LoadMatchBaseConfig: Core.Engine unavailable; skipping match_base.cfg.");
                return;
            }

            string map;
            try
            {
                map = engine.GlobalVars.MapName.ToString();
            }
            catch (Exception ex)
            {
                // Engine not up yet (plugin load). The next LoadWarmupConfig — OnMapLoad at the
                // latest — retries, so no map ever ends up without the base cvars.
                logger.LogDebug(ex, "LoadMatchBaseConfig: GlobalVars unavailable; deferring match_base.cfg.");
                return;
            }

            if (string.IsNullOrEmpty(map) || map == matchBaseCfgMap)
                return;

            matchBaseCfgMap = map;

            if (cfg.DetailedLogging)
                logger.LogInformation("LoadMatchBaseConfig: applying match_base.cfg on {Map}", map);

            engine.ExecuteCommand("exec mixscrims/match_base.cfg");
        });
    }

    /// <summary>
    /// Loads the warmup configuration for the server and executes overrides based on the current plugin state
    /// state.
    internal void LoadWarmupConfig()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Loading warmup configuration");

        LoadMatchBaseConfig();

        Core.Scheduler.NextTick(() =>
        {
            if (Core.Engine is { } engine)
                engine.ExecuteCommand("exec mixscrims/warmup.cfg");
            else
                logger.LogWarning("LoadWarmupConfig: Core.Engine unavailable; skipping warmup.cfg.");
        });

        // warmup.cfg no longer ends with `mp_restartgame 1` (its CleanUpMap() culled
        // plugin-spawned entities mid-warmup, and it is the crash-prone RestartRound
        // branch). `mp_warmup_start 1` inside the cfg still (re-)enters warmup; only the
        // complete reset needs replacing, and TerminateRound is a no-op during warmup.
        // Deferred so the cfg's own mp_startmoney/mp_maxmoney have landed first.
        var resetToken = Core.Scheduler.DelayBySeconds(0.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.Warmup) return;
            ResetWarmupState();
        });
        Core.Scheduler.StopOnMapChange(resetToken);

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
