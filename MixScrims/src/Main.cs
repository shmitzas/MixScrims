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
    Version = "1.7.2",
    Name = "MixScrims",
    Author = "Shmitzas",
    Description = "A plugin for PUGS style matches, with in-game match management."
)]

public partial class MixScrims : BasePlugin
{
    public static new ISwiftlyCore Core { get; internal set; } = null!;
    internal ILogger<MixScrims> logger = null!;
    internal MainConfig cfg = new();
    internal DiscordConfig discordConfig = new();
    internal MapsConfig mapsConfig = new();
    internal MixScrimsService mixScrimsService = null!;
    internal MatchState MatchState { get; set; } = MatchState.Warmup;
    internal PluginState PluginState { get; set; } = PluginState.Production;

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

            foreach (var alias in commandInfo.Aliases)
            {
                Core.Command.RegisterCommandAlias(commandName, alias, true);
            }
        }
    }

    internal void UnregisterCommands()
    {}

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
            var cfgOptions = provider.GetRequiredService<IOptions<MainConfig>>();
            cfg = cfgOptions.Value;
            mixScrimsService.SetPluginState(cfg.TestMode ? PluginState.Staging : PluginState.Production);
            preventNotPickedPlayersFromJoiningOngoingMatch = cfg.PreventNotPickedPlayersFromJoiningOngoingMatch;
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

            var cfgOptions = provider.GetRequiredService<IOptions<MapsConfig>>();
            mapsConfig = cfgOptions.Value;
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

            var cfgOptions = provider.GetRequiredService<IOptions<DiscordConfig>>();
            discordConfig = cfgOptions.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MixScrims configuration/services.");
        }
    }
}
