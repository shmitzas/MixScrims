using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixScrims.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace MixScrims;

[PluginMetadata(
    Id = "MixScrims",
    Version = "1.1.0",
    Name = "MixScrims",
    Author = "Shmitzas",
    Description = "A plugin for PUGS style matches, with in-game match management."
)]

public partial class MixScrims : BasePlugin
{
    public static new ISwiftlyCore Core { get; private set; } = null!;
    private ILogger<MixScrims> logger = null!;
    private IOptions<Config> cfgOptions = null!;
    private Config cfg = new();
    private MixScrimsService mixScrimsService = null!;

    public MixScrims(ISwiftlyCore core) : base(core)
    {
        mixScrimsService = new MixScrimsService();
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<IMixScrims, MixScrimsService>("MixScrims.API", mixScrimsService);
    }

    public override void Load(bool hotReload)
    {
        Core = base.Core;
        Core.Registrator.Register(this);

        LoadConfig();
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
    private void RegisterListeners()
    {
        RegisterWarmupListeners();
        RegisterMapChosenListeners();
        RegisterStateAgnosticListeners();
    }

    /// <summary>
    /// Registers available command handlers and their aliases with the command system.
    /// </summary>
    private void RegisterCommands()
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
    private void UnregisterCommands()
    {
        var commandNames = cfg.Commands.Keys.ToList();
        if (cfg.AllowVolunteerCaptains)
        {
            commandNames.Add("volunteer_captain");
        }
        foreach (var commandName in commandNames)
        {
            Core.Command.UnregisterCommand(commandName);
        }
    }

    /// <summary>
    /// Loads the configuration and initializes dependency injection services
    /// </summary>
    private void LoadConfig()
    {
        try
        {
            const string fileName = "config.jsonc";
            const string section = "MixScrims";

            Core.Configuration
                .InitializeJsonWithModel<Config>(fileName, section)
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
                .AddOptionsWithValidateOnStart<Config>()
                .BindConfiguration(section);

            var provider = services.BuildServiceProvider();

            logger = provider.GetRequiredService<ILogger<MixScrims>>();
            cfgOptions = provider.GetRequiredService<IOptions<Config>>();
            cfg = cfgOptions.Value;
            mixScrimsService.SetPluginState(cfg.TestMode ? PluginState.Staging : PluginState.Production);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MixScrims configuration/services.");
        }
    }
}
