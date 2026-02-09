namespace MixScrims;

public class Config
{
    // Debug settings
    public bool TestMode { get; set; } = false;
    public bool DetailedLogging { get; set; } = true;
    public string GlobalServerPrefix { get; set; } = "[ [darkred]MixScrims [default]]";

    // Discord invite settings
    public List<DiscordInvite> DiscordInviteWebhooks { get; set; } = [
        new() {
            Message = "<&role_id> +{0} ||| `connect {1}`",
            WebhookUrl = "https://discord.com/api/webhooks/webhook_token"
        }
    ];
    public int DiscordInviteDelayMinutes { get; set; } = 5;
    public bool EnableDiscordInvites { get; set; } = true;

    // Match settings
    public int MinimumReadyPlayers { get; set; } = 10;
    public bool RequireAllConnectedPlayersToBeReady { get; set; } = true;
    public bool FaceitLikeDamageControl { get; set; } = true;
    public bool MoveOverflowPlayersToSpec { get; set; } = true;
    public int DisallowVotePreviousMaps { get; set; } = 2;
    public int DefaultVoteTimeSeconds { get; set; } = 30;
    public int TimeoutDurationSeconds { get; set; } = 60;
    public int Timeouts { get; set; } = 3;

    // Niche settings
    public bool PreventNotPickedPlayersFromJoiningOngoingMatch { get; set; } = false;
    public bool SkipTeamPicking { get; set; } = false;
    public bool AllowVolunteerCaptains { get; set; } = false;
    public bool SkipMapVoting { get; set; } = false;
    public bool DisableCaptains { get; set; } = false;
    public bool ShowReadyStatusInScoreboard { get; set; } = false;
    public bool ShowReadyStatusInCenterHtml { get; set; } = false;

    // Announcement timers
    public AnnouncementTimers ChatAnnouncementTimers { get; set; } = new();
    public List<string> CommandRemindersLocalization { get; set; } =
    [
        "timeout",
        "ready",
        "invite"
    ];

    // Player leave punishment settings
    public bool PunishPlayerLeaves { get; set; } = false;
    public LeavePunishment PlayerLeavePunishment { get; set; } = new();

    // Commands
    public Dictionary<string, CommandInfo> Commands { get; set; } = new()
    {
        // Admin commands
        { "mix_reset", new() { Permission = "managemix", Aliases = ["reset"] } },
        { "mix_start", new() { Permission = "managemix", Aliases = ["start"] } },
        { "forceready", new() { Permission = "managemix", Aliases = ["fr"] } },
        { "forceunready", new() { Permission = "managemix", Aliases = ["fur"] } },
        { "captain", new() { Permission = "managemix", Aliases = ["cap", "capt"] } },
        { "map", new() { Permission = "managemix", Aliases = ["changemap"] } },
        { "maps", new() { Permission = "managemix", Aliases = ["maplist"] } },
        { "maplist_all", new() { Permission = "managemix", Aliases = ["allmaps", "maps_all"] } },

        // Player commands
        { "ready", new() { Permission = "", Aliases = ["r"] } },
        { "unready", new() { Permission = "", Aliases = ["u", "ur"] } },
        { "revote", new() { Permission = "", Aliases = ["rv"] } },
        { "timeout", new() { Permission = "", Aliases = ["pause"] } },
        { "surrender", new() { Permission = "", Aliases = ["gg"] }  },
        { "invite", new() { Permission = "", Aliases = ["inv"] } },
        { "stay", new() { Permission = "", Aliases = ["st"] } },
        { "switch", new() { Permission = "", Aliases = ["swap"] } },
        { "volunteer_captain", new() { Permission = "", Aliases = ["volcap", "selfcapt"] }   },
    };

    // Map settings
    public List<MapDetails> Maps { get; set; } =
    [
        new() { MapName = "de_mirage", DisplayName = "Mirage", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_dust2", DisplayName = "Dust2", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_inferno", DisplayName = "Inferno", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_anubis", DisplayName = "Anubis", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_overpass", DisplayName = "Overpass", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_ancient", DisplayName = "Ancient", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_ancient_night", DisplayName = "Ancient Night", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_nuke", DisplayName = "Nuke", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_vertigo", DisplayName = "Vertigo", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false }
    ];
}

public class MapDetails
{
    public string MapName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WorkshopId { get; set; } = string.Empty;
    public bool CanBeVoted { get; set; } = true;
    public bool IsWorkshopMap { get; set; } = false;
}

public class VotedMap
{
    public MapDetails Map { get; set; } = new();
    public List<int> VotedBy { get; set; } = [];
    public int Votes { get; set; } = 0;
}

public class DiscordInvite
{
    public string Message { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
}

public class AnnouncementTimers
{
    public int PlayersReadyStatus { get; set; } = 30;
    public int CaptainsAnnouncements { get; set; } = 30;
    public int CommandReminders { get; set; } = 320;
}

public class LeavePunishment
{
    public string ServerCommand { get; set; } = "sw_ban {steamId} {duration} {reason}";
    public int BanDurationMinutes { get; set; } = 15;
    public string BanReason { get; set; } = "Leaving during a MixScrims match";
    public int Sensitivity = 2;
    public int WaitBeforePunishmentSeconds { get; set; } = 300;
}

public class CommandInfo
{
    public string Permission { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
}

