<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" />
  <h1><strong>MixScrims</strong></h1>
  <h3>A SwiftlyS2 plugin for PUGS-style CS2 matches managed entirely in-game</h3>
  <p>No external panels or integrations required - everything happens on the server</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/Shmitzas/MixScrims-SwiftlyS2/total" alt="Downloads">
  <img src="https://img.shields.io/github/stars/Shmitzas/MixScrims-SwiftlyS2?style=flat&logo=github" alt="Stars">
  <img src="https://img.shields.io/github/license/Shmitzas/MixScrims-SwiftlyS2" alt="License">
</p>

---

<div align="center">
  <h3>💖 Support Development</h3>
  <p><em>This plugin is and will remain completely free! If you find it useful and want to support development, tips are always appreciated &lt;3</em></p>
  <a href='https://ko-fi.com/J3J21Q4PP2' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>
</div>

## 📋 Table of Contents

- [📋 Table of Contents](#-table-of-contents)
- [✨ Features](#-features)
- [🎮 Match Flow](#-match-flow)
- [🎯 Commands](#-commands)
  - [Player Commands](#player-commands)
    - [Command Details](#command-details)
  - [Admin Commands](#admin-commands)
    - [Admin Command Details](#admin-command-details)
- [⚙️ Configuration](#️-configuration)
  - [Configuration Options](#configuration-options)
  - [Map Configuration](#map-configuration)
  - [Player Leave Punishment Configuration](#player-leave-punishment-configuration)
    - [Sensitivity Levels](#sensitivity-levels)
    - [Grace Period](#grace-period)
    - [Command Variables](#command-variables)
  - [Discord Webhook Variables](#discord-webhook-variables)
- [📦 Installation](#-installation)
  - [Prerequisites](#prerequisites)
  - [Steps](#steps)
- [🔨 Building](#-building)
  - [Build from Source](#build-from-source)
  - [Development](#development)
- [🌍 Localization](#-localization)
  - [Translation Files](#translation-files)
  - [Adding New Language](#adding-new-language)
  - [Color Codes](#color-codes)
- [🤝 Contributing](#-contributing)
  - [Code Style](#code-style)
- [🤝 Get Help](#-get-help)

---

## ✨ Features

- **Full In-Game Match Management** - No external tools needed
- **Automated Match Flow** - Warmup → Map Voting → Captain Selection → Team Picking → Knife Round → Match
- **Team Timeout System** - Configurable team timeouts with voting
- **Player Leave Punishment** - Automatically punish players who leave during critical match phases
- **Discord Integration** - Send player invites via webhook
- **Map Voting System** - Democratic map selection with revote support
- **Captain System** - Random, manual, or volunteer captain assignment
- **Knife Round** - Winner picks starting side (stay/switch)
- **Auto-Configuration** - Different configs for each match phase
- **Highly Configurable** - Extensive JSON configuration with phase skipping options
- **Multi-Language Support** - Built-in localization system
    
---

## 🎮 Match Flow

The plugin manages matches through distinct states. Some phases can be skipped via configuration:

```
┌─────────────────────────────────────────────────────────────┐
│                        1. WARMUP                            │
│  • Players join and ready up using !ready                   │
│  • Minimum 10 players required (configurable)               │
│  • RequireAllConnectedPlayersToBeReady determines if ALL    │
│    connected players must ready or just minimum count       │
│  • Server announces ready status periodically               │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                     2. MAP VOTING                           │
│  • All ready players vote for a map                         │
│  • 30 second voting window (configurable)                   │
│  • Players can revote using !revote                         │
│  • Map with most votes wins                                 │
│  • Recent maps excluded (DisallowVotePreviousMaps config)   │
│  • Can be skipped with SkipMapVoting config                 │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                   3. MAP LOADING                            │
│  • Selected map loads with warmup config                    │
│  • Players ready up again after map change                  │
│  • Same ready requirements as initial warmup                │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                   4. CAPTAIN SELECTION                      │
│  • Two captains randomly selected (CT & T)                  │
│  • Admins can manually set captains with !captain           │
│  • Players can volunteer with !volunteer_captain if enabled │
│  • Team names set to captain names                          │
│  • Can be disabled with DisableCaptains config              │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                    5. TEAM PICKING                          │
│  • Captains alternate picking players                       │
│  • Random captain starts first                              │
│  • Players moved to spectator until picked                  │
│  • Continues until all players assigned                     │
│  • Can be skipped with SkipTeamPicking config               │
│  • MoveOverflowPlayersToSpec handles extra players          │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                     6. KNIFE ROUND                          │
│  • Knife-only warmup round                                  │
│  • Winning team captain chooses starting side               │
│  • Options: !stay or !switch                                │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                       7. MATCH                              │
│  • Competitive match begins                                 │
│  • Team timeouts available (!timeout)                       │
│  • Standard CS2 competitive rules                           │
│  • FaceitLikeDamageControl config affects friendly fire     │
│  • Match ends naturally or via admin commands               │
└─────────────────────────────────────────────────────────────┘
```

---

## 🎯 Commands

### Player Commands

All player commands support aliases defined in the configuration.

| Command | Aliases | Description | Available During |
|---------|---------|-------------|------------------|
| `!ready` | `!r` | Mark yourself as ready to start | Warmup, Map Chosen |
| `!unready` | `!u`, `!ur` | Mark yourself as not ready | Warmup, Map Chosen |
| `!revote` | `!rv` | Change your map vote | Map Voting |
| `!timeout` | `!pause` | Call a team timeout (requires team vote) | Match |
| `!invite` | `!inv` | Send Discord invite to get more players | Anytime |
| `!volunteer_captain` | `!volcap`, `!selfcapt` | Volunteer to be captain (if enabled) | Warmup, Map Loading, Map Chosen |
| `!stay` | `!st` | Keep current sides after knife round | Picking Starting Side |
| `!switch` | `!swap` | Switch sides after knife round | Picking Starting Side |

#### Command Details

**`!ready` / `!r`**
- Marks you as ready to participate in the match
- Server announces when players ready up
- Match proceeds based on `MinimumReadyPlayers` and `RequireAllConnectedPlayersToBeReady` config
- Periodic reminders sent to unready players
- Available in Warmup and Map Chosen (after map loads) states

**`!unready` / `!u` / `!ur`**
- Removes your ready status
- Use if you need to step away temporarily
- Works in same states as !ready

**`!revote` / `!rv`**
- Opens the map voting menu again during voting phase
- Change your vote before time expires
- Previous vote is automatically removed
- Only during Map Voting state

**`!timeout` / `!pause`**
- Initiates a team vote for timeout
- Requires majority of team to agree
- Teams have 3 timeouts by default (configurable via `Timeouts`)
- Timeout lasts 60 seconds by default (configurable via `TimeoutDurationSeconds`)
- Can be called at start of any round during match
- Only during Match state

**`!invite` / `!inv`**
- Sends webhook message to configured Discord channels
- Has cooldown to prevent spam (5 minutes default via `DiscordInviteDelayMinutes`)
- Shows how many players needed
- Only works if server is not full
- Requires `EnableDiscordInvites` to be true

**`!volunteer_captain` / `!volcap` / `!selfcapt`**
- Volunteer yourself as a team captain
- Usage: `!volunteer_captain <t/ct>` or `!volcap ct` or `!selfcapt t`
- Only works if `AllowVolunteerCaptains` is enabled in config
- Must specify which team (CT or T) you want to captain for
- Available during Warmup, Map Loading, or Map Chosen state before automatic captain selection
- Does not work if `DisableCaptains` is true

**`!stay` / `!st`**
- Winning knife round captain keeps current team sides
- Only available to winning captain
- Alternative to menu selection
- Only during Picking Starting Side state

**`!switch` / `!swap`**
- Winning knife round captain swaps team sides
- Only available to winning captain
- Alternative to menu selection
- Only during Picking Starting Side state

---

### Admin Commands

Admin commands require the `managemix` permission.

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!mix_reset` | `!reset` | `managemix` | Force reset entire match to warmup |
| `!mix_start` | `!start` | `managemix` | Force start match (skip to next phase) |
| `!forceready` | `!fr` | `managemix` | Mark all players as ready |
| `!forceunready` | `!fur` | `managemix` | Mark all players as unready |
| `!captain <t/ct>` | `!cap`, `!capt` | `managemix` | Open menu to select team captain |
| `!map <mapname>` | `!changemap` | `managemix` | Change to specified map |
| `!maps` | `!maplist` | `managemix` | List all voteable maps |
| `!maplist_all` | `!allmaps`, `!maps_all` | `managemix` | List all configured maps |

#### Admin Command Details

**`!mix_reset` / `!reset`**
- Immediately resets the entire match state
- Returns to warmup phase
- Clears all team assignments and votes
- Changes map to first map in configuration (de_mirage by default)
- Use when match needs to restart
- Available in any state

**`!mix_start` / `!start`**
- Bypasses warmup and ready checks
- Advances match to next appropriate phase:
  - From Warmup: Starts Map Voting (or Team Picking if SkipMapVoting is true)
  - Uses same logic as CheckReadyPlayersToStart
- Use when enough players are present but not readied
- Works in Warmup and Map Chosen states

**`!forceready` / `!fr`**
- Marks all connected players as ready
- Advances match to next phase automatically
- Only works in Warmup or Map Chosen states
- Useful for testing or when players forget to ready

**`!forceunready` / `!fur`**
- Marks all players as unready
- Clears the ready list
- Only works in Warmup or Map Chosen states
- Useful for resetting ready status without full match reset

**`!captain <t/ct>` / `!cap` / `!capt`**
- Opens menu to manually select captain for specified team
- Usage: `!captain ct` or `!captain t`
- Shows list of available players on that team
- Only works during Warmup, Map Loading, or Map Chosen states
- Does not work if `DisableCaptains` is true
- Useful for selecting specific captains instead of random selection

**`!map <mapname>` / `!changemap`**
- Immediately changes to specified map
- Usage: `!map mirage` or `!map de_mirage`
- Map must exist in configuration
- Resets match state on map change
- Works with both MapName and DisplayName
- Available in any state

**`!maps` / `!maplist`**
- Shows maps that can currently be voted for
- Excludes recently played maps based on `DisallowVotePreviousMaps` config
- Only shows maps with `CanBeVoted` set to true
- Available in any state

**`!maplist_all` / `!allmaps` / `!maps_all`**
- Shows all maps in configuration
- Includes non-voteable maps (`CanBeVoted` false)
- Shows all maps regardless of recent play history
- Available in any state

---

## ⚙️ Configuration

Configuration is stored in `config.jsonc` (generated on first load).

### Configuration Options

```jsonc
{
  "MixScrims": {
    // Debug and Testing
    "TestMode": false,                // Enable staging configs (adds bots, etc.)
    "DetailedLogging": true,          // Enable verbose logging for debugging
    
    // Discord webhook configuration for player invites
    "EnableDiscordInvites": true,     // Enable/disable Discord invite system
    "DiscordInviteWebhooks": [
      {
        "Message": "<@&role_id> +{0} players needed ||| `connect {1}`",
        "WebhookUrl": "https://discord.com/api/webhooks/..."
      }
    ],
    "DiscordInviteDelayMinutes": 5,   // Cooldown between invites
    
    // Match Ready Requirements
    "MinimumReadyPlayers": 10,        // Minimum players needed to start
    "RequireAllConnectedPlayersToBeReady": true, // All players must ready (not just minimum)
    
    // Match Settings
    "FaceitLikeDamageControl": true,  // Enable FACEIT-style damage control
    "MoveOverflowPlayersToSpec": true, // Move extra players to spectator
    "DisallowVotePreviousMaps": 2,    // Number of recent maps to exclude from voting
    "DefaultVoteTimeSeconds": 30,     // Map voting duration
    "TimeoutDurationSeconds": 60,     // Duration of team timeouts
    "Timeouts": 3,                    // Number of timeouts per team
    
    // Phase Control Options
    "SkipTeamPicking": false,         // Skip team picking phase
    "AllowVolunteerCaptains": false,  // Allow players to volunteer as captains
    "SkipMapVoting": false,           // Skip map voting (uses random or first map)
    "DisableCaptains": false,         // Disable captain system entirely
    
    // Announcement Timers (seconds)
    "ChatAnnouncementTimers": {
      "PlayersReadyStatus": 30,       // How often to announce ready status
      "CaptainsAnnouncements": 30,    // How often to announce captains
      "CommandReminders": 320         // How often to show command reminders
    },
    
    // Command Reminders (uses localization keys)
    "CommandRemindersLocalization": [
      "timeout",
      "ready",
      "invite"
    ],
    
    // Player Leave Punishment
    "PunishPlayerLeaves": false,      // Enable punishment system
    "PlayerLeavePunishment": {
      "ServerCommand": "sw_ban {steamId} {duration} {reason}",
      "BanDurationMinutes": 15,
      "BanReason": "Leaving during a MixScrims match",
      "Sensitivity": 2,               // 0=Match only, 1=+Knife, 2=+TeamPicking
      "WaitBeforePunishmentSeconds": 300  // Grace period before punishment
    },
    
    // Command Configuration (permission and aliases)
    "Commands": {
      // Admin Commands
      "mix_reset": { "Permission": "managemix", "Aliases": ["reset"] },
      "mix_start": { "Permission": "managemix", "Aliases": ["start"] },
      "forceready": { "Permission": "managemix", "Aliases": ["fr"] },
      "forceunready": { "Permission": "managemix", "Aliases": ["fur"] },
      "captain": { "Permission": "managemix", "Aliases": ["cap", "capt"] },
      "map": { "Permission": "managemix", "Aliases": ["changemap"] },
      "maps": { "Permission": "managemix", "Aliases": ["maplist"] },
      "maplist_all": { "Permission": "managemix", "Aliases": ["allmaps", "maps_all"] },
      
      // Player Commands
      "ready": { "Permission": "", "Aliases": ["r"] },
      "unready": { "Permission": "", "Aliases": ["u", "ur"] },
      "revote": { "Permission": "", "Aliases": ["rv"] },
      "timeout": { "Permission": "", "Aliases": ["pause"] },
      "invite": { "Permission": "", "Aliases": ["inv"] },
      "stay": { "Permission": "", "Aliases": ["st"] },
      "switch": { "Permission": "", "Aliases": ["swap"] },
      "volunteer_captain": { "Permission": "", "Aliases": ["volcap", "selfcapt"] }
    },
    
    // Map Pool Configuration
    "Maps": [
      {
        "MapName": "de_mirage",
        "DisplayName": "Mirage",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_dust2",
        "DisplayName": "Dust2",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_inferno",
        "DisplayName": "Inferno",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_anubis",
        "DisplayName": "Anubis",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_overpass",
        "DisplayName": "Overpass",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_ancient",
        "DisplayName": "Ancient",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_ancient_night",
        "DisplayName": "Ancient Night",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_nuke",
        "DisplayName": "Nuke",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      },
      {
        "MapName": "de_vertigo",
        "DisplayName": "Vertigo",
        "WorkshopId": "",
        "CanBeVoted": true,
        "IsWorkshopMap": false
      }
    ]
  }
}
```

### Map Configuration

Each map entry supports:

| Property | Type | Description |
|----------|------|-------------|
| `MapName` | string | Technical map name (e.g., "de_dust2") |
| `DisplayName` | string | Friendly display name (e.g., "Dust2") |
| `WorkshopId` | string | Workshop ID if workshop map |
| `CanBeVoted` | bool | Can be voted for in map voting |
| `IsWorkshopMap` | bool | Whether it's a workshop map |

### Player Leave Punishment Configuration

The plugin can automatically punish players who leave during critical match phases to discourage abandonment.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PunishPlayerLeaves` | bool | `false` | Enable/disable punishment system |
| `ServerCommand` | string | `sw_ban {steamId} {duration} {reason}` | Command template to execute |
| `BanDurationMinutes` | int | `15` | Duration of punishment in minutes |
| `BanReason` | string | `Leaving during a MixScrims match` | Reason displayed to player |
| `Sensitivity` | int | `2` | When punishment triggers (0-2) |
| `WaitBeforePunishmentSeconds` | int | `300` | Grace period before punishment applies (5 minutes) |

#### Sensitivity Levels

- **0 (Lowest)** - Only punish during active match
  - `Match` - Competitive match is in progress
  - `Timeout` - Team timeout is active
- **1 (Medium)** - Punish during knife round, side selection, and match
  - `KnifeRound` - Knife round phase
  - `PickingStartingSide` - Winner choosing sides
  - `Match` - Competitive match is in progress
  - `Timeout` - Team timeout is active
- **2 (Highest)** - Punish during team picking, knife round, side selection, and match
  - `PickingTeam` - Captains picking players
  - `KnifeRound` - Knife round phase
  - `PickingStartingSide` - Winner choosing sides
  - `Match` - Competitive match is in progress
  - `Timeout` - Team timeout is active

#### Grace Period

The `WaitBeforePunishmentSeconds` setting provides a grace period before punishment is applied. This allows players who accidentally disconnect or have brief connection issues to rejoin without being punished. Default is 300 seconds (5 minutes).

- If a player disconnects and doesn't return within the grace period, they will be punished
- The grace period only applies if punishment is enabled and sensitivity conditions are met
- Useful to avoid punishing players with temporary connection issues

#### Command Variables

The `ServerCommand` supports these placeholders:
- `{steamId}` - Player's SteamID64
- `{reason}` - Configured ban reason
- `{duration}` - Ban duration in minutes

**Example commands:**
```jsonc
// Swiftly ban command
"ServerCommand": "sw_ban {steamId} {duration} {reason}"

// CSS ban command
"ServerCommand": "css_ban {steamId} {duration} {reason}"

// Custom command
"ServerCommand": "custom_punish {steamId} {duration}"
```

### Discord Webhook Variables

The Discord message supports these variables:
- `{0}` - Number of players needed
- `{1}` - Server connection command (auto-populated)

---

## 📦 Installation

### Prerequisites

- Counter-Strike 2 Dedicated Server
- [SwiftlyS2](https://github.com/swiftly-solution/swiftly) plugin framework installed
- .NET 10 Runtime

### Steps

1. **Download Latest Release**
   ```bash
   # Download from releases page or build from source
   ```

2. **Extract Plugin**
   ```
   # Extract to your Swiftly plugins directory
   └── csgo/
       └── addons/
           └── swiftly/
               └── plugins/
                   └── MixScrims/
   ```

3. **Configure Plugin**
   - Start server to generate `config.jsonc`
   - Edit configuration file with your settings
   - Add Discord webhook URLs (optional)
   - Configure map pool
   - Adjust phase control options (SkipMapVoting, SkipTeamPicking, etc.)

4. **Add Server Configs**
   - Place CS2 config files in `csgo/cfg/mixscrims/` directory
   - Required configs:
     - `warmup.cfg` - Initial warmup and post-map-change warmup settings
     - `teampick.cfg` - Team picking phase settings
     - `knife_round.cfg` - Knife round settings
     - `match_start.cfg` - Competitive match settings
     - `staging_overrides.cfg` - TestMode overrides (adds bots, etc.)
     - `production_overrides.cfg` - Production environment overrides
   - Example configs are included in the plugin build

5. **Set Permissions**
   ```
   # Grant managemix permission to admins
   # (Refer to Swiftly documentation for permission management)
   ```

6. **Restart Server**

---

## 🔨 Building

### Build from Source

1. **Clone Repository**
   ```bash
   git clone https://github.com/Shmitzas/MixScrims-SwiftlyS2.git
   cd MixScrims-SwiftlyS2
   ```

2. **Build Project**
   ```bash
   dotnet build -c Release
   ```

3. **Publish Plugin**
   ```bash
   dotnet publish -c Release
   ```

4. **Output**
   - Built files located in `MixScrims/build/`
   - Published files located in `MixScrims/build/publish/MixScrims/`
   - Configs automatically copied to `cfg/mixscrims/`
   - Translations copied to `resources/translations/`
   - Contract DLL exported to `resources/exports/`

### Development

- Open solution in Visual Studio 2022, Rider, or VS Code
- Ensure .NET 10 SDK installed
- C# 13.0 features supported
- Hot reload supported by Swiftly

---

## 🌍 Localization

The plugin supports multiple languages through JSON localization files.

### Translation Files

Located in `resources/translations/`:
- `en.jsonc` - English (default)
- `pt-BR.jsonc` - Portuguese (Brazilian)
- Add more languages by creating new files (e.g., `lt.jsonc`, `pl.jsonc`)

### Adding New Language

1. Copy `en.jsonc` to new language file (e.g., `de.jsonc` for German)
2. Translate all string values
3. Keep keys unchanged
4. Swiftly will auto-detect available languages

### Color Codes

Messages support color codes:
```
[default], [darkred], [green], [lightyellow], [lightblue], 
[olive], [lime], [red], [purple], [grey], [yellow], [gold], 
[silver], [blue], [darkblue], [bluegrey], [magenta], [lightred]
```

Example:
```json
"server_prefix": "[ [darkred]MyServer [default]]"
```

---

## 🤝 Contributing

Contributions are welcome! Please follow these guidelines:

1. **Fork the Repository**
2. **Create Feature Branch**
   ```bash
   git checkout -b feature/amazing-feature
   ```
3. **Commit Changes**
   ```bash
   git commit -m 'Add amazing feature'
   ```
4. **Push to Branch**
   ```bash
   git push origin feature/amazing-feature
   ```
5. **Open Pull Request**

### Code Style

- Follow existing C# conventions
- Use meaningful variable/method names
- Add XML documentation comments
- Test thoroughly in TestMode before submitting
- Verify configuration options work as expected

---

## 🤝 Get Help

- **Issues**: [GitHub Issues](https://github.com/Shmitzas/MixScrims-SwiftlyS2/issues)
- **Discussions**: [GitHub Discussions](https://github.com/Shmitzas/MixScrims-SwiftlyS2/discussions)

---

<div align="center">
<p>Made with ❤️ by Shmitzas</p>
<p>To play in my CS2 servers go to <a href="https://rampage.lt">Rampage.lt</a></p>
<p>⭐ Star this repository if you find it useful!</p>
</div>
