using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixScrims.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Plugins;

namespace MixScrims;

[PluginMetadata(
    Id = "MixScrims",
    Version = "1.11.0",
    Name = "MixScrims",
    Author = "Shmitzas",
    Description = "A plugin for PUGS style matches, with in-game match management."
)]

public partial class MixScrims : BasePlugin
{
    public static new ISwiftlyCore Core { get; internal set; } = null!;
    internal ILogger<MixScrims> logger = null!;

    // Backed by IOptionsMonitor so edits to the jsonc files land on every read
    // site without a plugin reload. The fallback instance preserves the old
    // "defaults if the loader threw" behaviour, since each Load*Config() swallows
    // its exception and would otherwise leave the monitor null.
    private IOptionsMonitor<MainConfig>? cfgMonitor;
    private IOptionsMonitor<DiscordConfig>? discordConfigMonitor;
    private IOptionsMonitor<MapsConfig>? mapsConfigMonitor;
    private readonly MainConfig cfgFallback = new();
    private readonly DiscordConfig discordConfigFallback = new();
    private readonly MapsConfig mapsConfigFallback = new();

    internal MainConfig cfg => cfgMonitor?.CurrentValue ?? cfgFallback;
    internal DiscordConfig discordConfig => discordConfigMonitor?.CurrentValue ?? discordConfigFallback;
    internal MapsConfig mapsConfig => mapsConfigMonitor?.CurrentValue ?? mapsConfigFallback;

    internal MixScrimsService mixScrimsService = null!;
    internal MatchState MatchState { get; set; } = MatchState.Warmup;
    internal PluginState PluginState { get; set; } = PluginState.Production;

    // Built-in presentation suppression toggles (v2.0.0 contract). Seeded from
    // cfg in LoadMainConfig(); the shared API surface (SetBuiltInMenusSuppressed /
    // SetBuiltInCenterHtmlSuppressed) mutates them at runtime, and the config
    // seeding does NOT re-run on IOptions reload so a runtime override is sticky
    // until plugin unload.
    internal bool suppressBuiltInMenus = false;
    internal bool suppressBuiltInCenterHtml = false;

    public MixScrims(ISwiftlyCore core) : base(core)
    {
        mixScrimsService = new MixScrimsService(this);
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<IMixScrims, MixScrimsService>("MixScrims.API", mixScrimsService);
    }

    public override void Load(bool hotReload)
    {
        Core = base.Core;

        LoadMainConfig();
        LoadDiscordConfig();
        LoadMapsConfig();
        RegisterListeners();
        ResetVariables();
        RegisterCommands();
        mixScrimsService.SetPluginState(cfg.TestMode ? PluginState.Staging : PluginState.Production);
        StartWarmup();
    }

    public override void Unload()
    {
        try
        {
            // Unsubscribe explicit OnMapLoad handlers registered in RegisterListeners().
            // [GameEventHandler]-attributed handlers are torn down by the framework on
            // plugin unload, but these delegate-based subscriptions are not.
            Core.Event.OnMapLoad -= WarmupHandleOnMapStart;
            Core.Event.OnMapLoad -= AddPickedMapToPlayedMaps;
            Core.Event.OnMapLoad -= HandleStateAgnosticMapLoad;
            Core.Event.OnClientPutInServer -= HandleClientPutInServer;
            Core.Event.OnClientDisconnected -= OnPlayerDisconnect;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "MixScrims.Unload: failed to unsubscribe lifecycle events.");
        }

        // Cancel every long-lived timer stored as an instance field so they cannot fire
        // against a disposed plugin instance after a hot reload.
        try
        {
            playerStatusTimer?.Cancel();
            playerStatusTimerCenterHtml?.Cancel();
            commandRemindersTimer?.Cancel();
            captainsAnnouncementsTimer?.Cancel();
            autoResetOnLeaveTimer?.Cancel();
            timeoutVoteTimer?.Cancel();
            surrenderVoteTimer?.Cancel();

            foreach (var (steamId, cts) in _punishmentTimers)
            {
                try { cts?.Cancel(); }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "MixScrims.Unload: failed to cancel punishment timer for {SteamId}.", steamId);
                }
            }
            _punishmentTimers.Clear();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "MixScrims.Unload: failed to cancel one or more timers.");
        }

        UnregisterCommands();
        logger?.LogInformation("MixScrims unloading.");
    }

    /// <summary>
    /// Registers all listeners used by the plugin.
    /// </summary>
    internal void RegisterListeners()
    {
        RegisterWarmupListeners();
        RegisterMapChosenListeners();
        RegisterStateAgnosticListeners();
    }

    /// <summary>
    /// Registers command aliases from configuration. Primary commands are registered automatically
    /// via the [Command] attribute on their handler methods.
    /// </summary>
    internal void RegisterCommands()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Registering command aliases...");

        foreach (var (commandName, commandInfo) in cfg.Commands)
        {
            // Skip aliases for commands gated by feature flags when those features are disabled.
            if (commandName == "volunteer_captain" && !cfg.AllowVolunteerCaptains)
                continue;
            if (commandName == "votekick" && !cfg.VoteKick.Enabled)
                continue;
            if (commandName == "rtv" && !cfg.Rtv.Enabled)
                continue;

            foreach (var alias in commandInfo.Aliases)
            {
                Core.Command.RegisterCommandAlias(commandName, alias, false);
            }
        }
    }

    internal void UnregisterCommands()
    {
        // Unregister every alias we registered in RegisterCommands(). Primary commands
        // are auto-unregistered by the framework via the [Command] attribute lifecycle,
        // but aliases registered manually via RegisterCommandAlias persist on hot reload
        // and will route into a dead plugin instance unless removed here.
        if (cfg?.Commands == null)
        {
            logger?.LogWarning("UnregisterCommands: cfg.Commands is null, skipping alias cleanup.");
            return;
        }

        foreach (var (commandName, commandInfo) in cfg.Commands)
        {
            if (commandInfo?.Aliases == null)
            {
                logger?.LogWarning("UnregisterCommands: command {Command} has null aliases, skipping.", commandName);
                continue;
            }

            foreach (var alias in commandInfo.Aliases)
            {
                try
                {
                    Core.Command.UnregisterCommand(alias);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "UnregisterCommands: failed to unregister alias {Alias} for {Command}.", alias, commandName);
                }
            }
        }
    }

    /// <summary>
    /// Loads the configuration and initializes dependency injection services
    /// </summary>
    internal void LoadMainConfig()
    {
        try
        {
            const string fileName = "config.jsonc";
            const string section = "MixScrims";

            Core.Configuration
                .InitializeJsonWithModel<MainConfig>(fileName, section)
                .Configure(builder =>
                {
                    builder.AddJsonFile(
                        Core.Configuration.GetConfigPath(fileName),
                        optional: false,
                        reloadOnChange: true
                    );
                });

            ServiceCollection services = new();
            services
                .AddSwiftly(Core, addLogger: true, addConfiguration: true)
                .AddOptionsWithValidateOnStart<MainConfig>()
                .BindConfiguration(section);

            var provider = services.BuildServiceProvider();

            logger = provider.GetRequiredService<ILogger<MixScrims>>();
            cfgMonitor = provider.GetRequiredService<IOptionsMonitor<MainConfig>>();
            mixScrimsService.SetPluginState(cfg.TestMode ? PluginState.Staging : PluginState.Production);
            preventNotPickedPlayersFromJoiningOngoingMatch = cfg.PreventNotPickedPlayersFromJoiningOngoingMatch;
            // Seed built-in presentation suppression from config. Runtime SetBuiltIn*
            // calls mutate these fields directly; we deliberately don't re-seed on
            // config reload so a runtime override survives a config edit.
            suppressBuiltInMenus = cfg.SuppressBuiltInMenus;
            suppressBuiltInCenterHtml = cfg.SuppressBuiltInCenterHtml;

            cfgMonitor.OnChange(_ =>
            {
                // Only PluginState is re-derived. The three seeded bools above are
                // deliberately left alone: each has a runtime setter on the shared
                // API (SetBuiltInMenusSuppressed / SetBuiltInCenterHtmlSuppressed /
                // PreventNewPlayersJoining), so re-seeding would silently revert a
                // consumer's override on an unrelated config edit.
                mixScrimsService.SetPluginState(cfg.TestMode ? PluginState.Staging : PluginState.Production);
                logger.LogInformation("MixScrims: config.jsonc reloaded (command names stay bound until plugin reload).");
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MixScrims configuration/services.");
        }
    }

    internal void LoadMapsConfig()
    {
        try
        {
            const string fileName = "maps.jsonc";
            const string section = "MapsConfig";

            Core.Configuration
                .InitializeJsonWithModel<MapsConfig>(fileName, section)
                .Configure(builder =>
                {
                    builder.AddJsonFile(
                        Core.Configuration.GetConfigPath(fileName),
                        optional: false,
                        reloadOnChange: true
                    );
                });

            ServiceCollection services = new();
            services
                .AddSwiftly(Core, addConfiguration: true)
                .AddOptionsWithValidateOnStart<MapsConfig>()
                .BindConfiguration(section);

            var provider = services.BuildServiceProvider();

            mapsConfigMonitor = provider.GetRequiredService<IOptionsMonitor<MapsConfig>>();
            mapsConfigMonitor.OnChange(_ => logger.LogInformation("MixScrims: maps.jsonc reloaded."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MixScrims maps configuration.");
        }
    }

    internal void LoadDiscordConfig()
    {
        try
        {
            const string fileName = "discord_config.jsonc";
            const string section = "DiscordConfig";

            Core.Configuration
                .InitializeJsonWithModel<DiscordConfig>(fileName, section)
                .Configure(builder =>
                {
                    builder.AddJsonFile(
                        Core.Configuration.GetConfigPath(fileName),
                        optional: false,
                        reloadOnChange: true
                    );
                });

            ServiceCollection services = new();
            services
                .AddSwiftly(Core, addConfiguration: true)
                .AddOptionsWithValidateOnStart<DiscordConfig>()
                .BindConfiguration(section);

            var provider = services.BuildServiceProvider();

            discordConfigMonitor = provider.GetRequiredService<IOptionsMonitor<DiscordConfig>>();
            discordConfigMonitor.OnChange(_ => logger.LogInformation("MixScrims: discord_config.jsonc reloaded."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MixScrims configuration/services.");
        }
    }
}
