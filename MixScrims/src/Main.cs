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
    Version = "1.6.1",
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
    /// Registers available command handlers and their aliases with the command system.
    /// </summary>
    internal void RegisterCommands()
    {
        // Define command mappings
        var commandHandlers = new Dictionary<string, ICommandService.CommandListener>
        {
            { "mix_reset", OnResetPlugin },
            { "mix_start", OnForceMatchStart },
            { "forceready", OnForceReady },
            { "forceunready", OnForceUnready },
            { "captain", OnCaptain },
            { "map", OnGoToMap },
            { "maps", OnListVoteableMaps },
            { "maplist_all", OnListAllMaps },
            { "ready", OnReady },
            { "unready", OnUnReady },
            { "revote", OnRevote },
            { "timeout", OnTimeout },
            { "surrender", OnSurrender },
            { "invite", OnInvite },
            { "stay", OnStay },
            { "switch", OnSwitch }
        };

        if (cfg.AllowVolunteerCaptains)
        {
            commandHandlers["volunteer_captain"] = OnCaptainVolunteer;
        }

        if (cfg.VoteKick.Enabled)
        {
            commandHandlers["votekick"] = OnVoteKick;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("Registering commands and aliases...");


        foreach (var (commandName, handler) in commandHandlers)
        {
            if (!cfg.Commands.TryGetValue(commandName, out var commandInfo))
            {
                if (cfg.DetailedLogging)
                    logger.LogWarning("Command '{CommandName}' not found in config, skipping registration", commandName);
                continue;
            }

            // Register command with permission from config
            Core.Command.RegisterCommand(commandName, handler, true, commandInfo.Permission);

            // Register aliases
            foreach (var alias in commandInfo.Aliases)
            {
                Core.Command.RegisterCommandAlias(commandName, alias);
            }
        }
    }

    /// <summary>
    /// Unregisters all commands currently configured in the application, including the volunteer captain command if
    /// enabled.
    /// </summary>
    internal void UnregisterCommands()
    {
        var commandNames = cfg.Commands.Keys.ToList();
        if (cfg.AllowVolunteerCaptains)
        {
            commandNames.Add("volunteer_captain");
        }

        if (cfg.VoteKick.Enabled)
        {
            commandNames.Add("votekick");
        }
        foreach (var commandName in commandNames)
        {
            Core.Command.UnregisterCommand(commandName);
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
