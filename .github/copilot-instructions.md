# MixScrims CS2 Plugin - AI Coding Agent Instructions

## Project Overview

MixScrims is a **SwiftlyS2 plugin** that implements FACEIT-style PUG matches with in-game management. It's a **state machine-driven plugin** that progresses through match phases: Warmup → MapVoting → MapChosen → PickingTeam → KnifeRound → PickingStartingSide → Match.

- **Plugin Framework**: [SwiftlyS2](https://swiftlys2.net) - CS2 server modification framework for .NET 10.0
- **Plugin Version**: 1.6.1
- **Architecture**: State-based partial classes with shared service layer
- **Key Components**: Main plugin (`MixScrims`), Contract API (`MixScrims.Contract`), state handlers, shared services, announcement system

**Documentation:**
- [Project Wiki](https://github.com/shmitzas/MixScrims-SwiftlyS2/wiki) - Comprehensive guides for installation, configuration, features, and contributing
- [SwiftlyS2 API Reference](https://swiftlys2.net/llms-full.txt) - Framework API documentation

**Jump to:** [Quick Reference](#quick-reference) • [Match Flow](#match-flow-overview) • [Architecture](#critical-architecture-patterns) • [Development](#development-workflows) • [Config](#config-features-by-category) • [API](#shared-api-pattern) • [Commands](#available-commands) • [Testing](#testing--debugging) • [Pitfalls](#common-pitfalls)

## Quick Reference

**When you need to:**
- **Modify state logic**: Check `src/States/{StateName}/` (Main.cs or Events.cs)
- **Add cross-state features**: Edit `src/StateAgnostic/Main.cs` or `Announcements.cs`
- **Add new commands**: Update `src/Commands/` + `Config.Commands` dictionary
- **Extend public API**: Add methods to `IMixScrims.cs` + implement in `MixScrimsService.cs`
- **Change announcements**: Modify `resources/translations/{lang}.jsonc`
- **Adjust match flow**: Edit CFG files in `csgo_configs/` + config.jsonc

**Critical Rules:**
- Always use `mixScrimsService.SetMatchState()` - never modify state directly
- Check player validity: `if (!player.IsValid || !player.IsConnected || player.IsBot)`
- Store scheduler tokens: `Core.Scheduler.StopOnMapChange(token)`

## Match Flow Overview

**Full Sequence:** Warmup → Map Voting → Map Chosen → Team Picking → Knife Round → Side Selection → Match → Ended

**Key Transitions:**
- **Warmup**: Players `!ready` until `MinimumReadyPlayers` met → advances to Map Voting (or Map Chosen if `SkipMapVoting`)
- **Map Voting**: 30s vote window, highest votes wins → map change → Map Chosen state
- **Map Chosen**: Players `!ready` again after map load → Captains selected (random/volunteer/manual) → Team Picking
- **Team Picking**: Captains alternate picking players via menu (random captain picks first) → Knife Round when complete
- **Knife Round**: Knife-only round, winning team captain gets menu → Side Selection
- **Side Selection**: Winner chooses `!stay` or `!switch` sides → Match starts with `match_start.cfg`
- **Match**: Standard MR12 competitive, team timeouts (vote-based, 3 per team), surrender voting → Surrender (if team votes) OR Ended → resets to Warmup

**Config Shortcuts:** `SkipMapVoting` (uses current map), `SkipTeamPicking` (random teams), `DisableCaptains` (team voting for side selection)

## Critical Architecture Patterns

### Partial Class Structure
The main `MixScrims` class is split across **multiple partial class files** organized by concern:
- `src/Main.cs` - Core plugin lifecycle, DI setup, command registration
- `src/StateAgnostic/Main.cs` - Cross-state logic (ready checks, team management, player punishment)
- `src/StateAgnostic/Announcements.cs` - Ready status announcements, scoreboard/center HTML display
- `src/StateAgnostic/Events.cs` - Cross-state event handlers
- `src/States/{StateName}/Main.cs` - State-specific logic (when needed)
- `src/States/{StateName}/Events.cs` - State-specific event handlers

**State Folder Structure:**

| State | Main.cs | Events.cs | Extra Files | Purpose |
|-------|---------|-----------|-------------|---------|  
| KnifeRound | ✅ | ✅ | — | Knife round logic & round end handling |
| MapChosen | ❌ | ✅ | — | Post-map-vote ready check |
| MapVoting | ✅ | ❌ | — | Vote collection & map selection |
| Match | ✅ | ✅ | `VoteKick.cs` | Active match logic, round events & votekick |
| PickingTeam | ✅ | ❌ | — | Captain team selection menus |
| Reset | ✅ | ❌ | — | Plugin state reset & cleanup |
| Surrender | ✅ | ❌ | — | Team surrender voting |
| Timeout | ✅ | ✅ | — | Pause/resume logic & timer |
| VoteKick | ❌ | ❌ | — | Reserved folder (logic lives in Match/VoteKick.cs) |
| Warmup | ✅ | ✅ | — | Initial ready check & player join handling |

When modifying functionality, **always check if related code exists in other partial files** before adding new methods.

### State Machine Management
State transitions are managed through `MixScrimsService.SetMatchState(MatchState state)`:
```csharp
// States defined in MixScrims.Contract/MatchState.cs
mixScrimsService.SetMatchState(MatchState.MapVoting);
```
**Never modify state directly** - always use the service. State changes trigger phase transitions that execute CFG files and update game rules.

### Command Registration Pattern
Commands are **config-driven** with dynamic aliases:
```csharp
// Config defines: command name → permissions + aliases
{ "ready", new() { Permission = "", Aliases = ["r"] } }

// Registration uses config lookup
Core.Command.RegisterCommand(commandName, handler, true, commandInfo.Permission);
foreach (var alias in commandInfo.Aliases)
    Core.Command.RegisterCommandAlias(commandName, alias);
```
When adding commands: (1) Add handler to `commandHandlers` dict in `RegisterCommands()`, (2) Add config entry to `Config.Commands`.

### Event Handling Pattern
Events use **two registration patterns**:
```csharp
// 1. Delegate subscription (lifecycle events)
Core.Event.OnClientPutInServer += HandleClientPutInServer;
Core.Event.OnMapLoad += WarmupHandleOnMapStart;

// 2. Attribute-based hooks (game events)
[ClientCommandHookHandler]
public HookResult OnClientCommand(int playerId, string commandLine) { ... }

[GameEventHandler]
public HookResult HandleRoundEnd(EventRoundEnd @event) { ... }
```
Game event handlers **must return `HookResult.Continue`** to allow event propagation. Register listeners in `RegisterXListeners()` methods per state.

### Configuration & Localization
- **Config**: Three separate JSONC files, each bound via `IOptions<T>`:
  - `config.jsonc` → `MainConfig` (section `MixScrims`) — match settings, timers, commands
  - `maps.jsonc` → `MapsConfig` (section `MapsConfig`) — map pool definitions
  - `discord_config.jsonc` → `DiscordConfig` (section `DiscordConfig`) — Discord webhook settings
- **Translations**: Localized strings in `resources/translations/{lang}.jsonc`, accessed via `Core.Localizer["key"]`
- Config reloads on change but **requires plugin reload** for structural changes (maps, commands)

### CS2 Server Control
Match phases execute **CFG files to configure game rules**, loaded from `csgo_configs/`:
```csharp
Core.Engine.ExecuteCommand("exec mixscrims/warmup.cfg");      // mp_warmuptime unlimited
Core.Engine.ExecuteCommand("exec mixscrims/knife_round.cfg");  // mp_give_player_c4 0, mp_ct_default_grenades "weapon_knife"
```
CFG files **must exist on CS2 server** at `csgo/cfg/mixscrims/`. Environment-specific overrides use `staging_overrides.cfg` or `production_overrides.cfg` (controlled by `cfg.TestMode`).

## Development Workflows

### Common Development Tasks

**Adding a New Command:**
1. Add handler method to `src/Commands/Admin/` or `src/Commands/Player/` (one file per command)
2. Register in `commandHandlers` dict in `Main.cs` `RegisterCommands()`
3. Add config entry to `MainConfig.Commands` with aliases & permissions
4. Add localization key to `resources/translations/en.jsonc`

**Adding State-Specific Behavior:**
1. Navigate to `src/States/{StateName}/`
2. Add logic to `Main.cs` (if it exists) or create `Events.cs`
3. For events, use `[GameEventHandler]` attribute
4. Register listeners in `Register{StateName}Listeners()` in Main.cs

**Modifying Ready System Display:**
- **Chat announcements**: Edit `PrintReadyAndNotReadyPlayers()` in `StateAgnostic/Announcements.cs`
- **Scoreboard**: Edit `ShowReadyAndNotReadyPlayersInScoreboard()` in same file
- **Center HTML**: Edit `DisplayReadyAndNotReadyPlayersInCenterHtml()` in same file
- **Localization**: Update keys in `resources/translations/en.jsonc`

**Extending the Public API:**
1. Add method signature to `MixScrims.Contract/IMixScrims.cs`
2. Implement method in `MixScrims/src/Shared/MixScrimsService.cs`
3. Add internal logic to appropriate partial class file
4. Update this documentation's API section

### Building
```powershell
# Standard build
dotnet build MixScrims-SwiftlyS2.sln

# Release build for deployment
dotnet publish MixScrims/MixScrims.csproj -c Release
```
Output: `MixScrims/build/publish/MixScrims/` contains deployable plugin files.

### Project Structure for New Features
1. **State-specific logic**: Add to `src/States/{StateName}/Main.cs` or `Events.cs`
2. **Cross-state logic**: Add to `src/StateAgnostic/Main.cs`
3. **Shared utilities**: Add to `src/Shared/Helpers.cs` or create new service
4. **API contracts**: Extend `MixScrims.Contract/IMixScrims.cs` (shared with other plugins)

### Dependency Notes
- **SwiftlyS2.CS2**: NuGet package providing framework API (use wildcard version `*`)
  - **Must use** `ExcludeAssets="runtime" PrivateAssets="all"` to avoid bundling
  - Framework provides `ISwiftlyCore` accessed via `Core` property in plugins
- **MixScrims.Contract**: Separate project for plugin API exposure
  - Set `<Private>false</Private>` in ProjectReference to prevent .dll export
- Microsoft.Extensions.* packages: All require `ExcludeAssets="runtime"` pattern

## Project-Specific Conventions

### Player Management
Players are tracked via `IPlayer` from SwiftlyS2. **Four lifecycle stages**:
1. `readyPlayers` - Warmup ready check (`!ready` typed)
2. `pickedCtPlayers`/`pickedTPlayers` - Captain selections during team picking
3. `playingCtPlayers`/`playingTPlayers` - Active match rosters (knife → match end)
4. `playersWaitingForPunishment` - Players queued for punishment after disconnect

Lists clear at phase transitions (e.g., `readyPlayers.Clear()` when knife starts). **Always check player validity**:
```csharp
if (!player.IsValid || !player.IsConnected || player.IsBot) return;
```

**Captain Selection:** Volunteers (`!volunteer_captain t/ct` if enabled) take priority, else random from ready players. Team names set to captain names unless `DisableCaptains` is true.

**Punishment System:** When `PunishPlayerLeaves` is enabled, players who disconnect during active phases are added to `playersWaitingForPunishment`. After `WaitBeforePunishmentSeconds` (default 300s), the configured `ServerCommand` executes (e.g., `sw_ban {steamId} {duration} {reason}`). Sensitivity levels determine which match states trigger punishment.

### Timer/Scheduler Pattern
Use `Core.Scheduler` (ISchedulerService) for async operations:
```csharp
var token = Core.Scheduler.DelayBySeconds(5, () => StartMatch());
Core.Scheduler.StopOnMapChange(token); // Auto-cleanup on map change
```
Available methods: `DelayBySeconds()`, `RepeatBySeconds()`, `NextTick()`, `NextWorldUpdate()`. **Always store CancellationTokenSource** for manual cleanup.

**Announcement Timers:** Plugin uses repeating timers for periodic announcements:
- `playerStatusTimer` - Prints ready/not ready status to chat (interval: `cfg.ChatAnnouncementTimers.PlayersReadyStatus`, default 30s)
- `playerStatusTimerCenterHtml` - Updates center HTML ready display every 1s (only if `ShowReadyStatusInCenterHtml` enabled)
- `commandRemindersTimer` - Shows command help reminders (interval: `cfg.ChatAnnouncementTimers.CommandReminders`, default 320s)
- `captainsAnnouncementsTimer` - Announces captain names (interval: `cfg.ChatAnnouncementTimers.CaptainsAnnouncements`, default 30s)

All timers are created in `StartAnnouncementTimers()` and automatically stopped on map change.

### Logging Strategy
- `cfg.DetailedLogging` flag enables verbose logs
- Use structured logging: `logger.LogInformation("Message {Param}", value)`
- State transitions should always log

### Team Enum Gotcha
`Team` enum: `None = 0, Spectator = 1, T = 2, CT = 3`
**Never assume numeric values** - always use enum names.

### Config Features by Category

**Display & UI:**
- `GlobalServerPrefix` - Customizable message prefix (default: `[ [darkred]MixScrims [default]]`)
- `ShowReadyStatusInChat` - Print ready status to chat (default true)
- `ShowReadyStatusInScoreboard` - Scoreboard ready/not ready prefix
- `ShowReadyStatusInCenterHtml` - Center HTML overlay (1s updates)
- `HideReadyStatusInCenterWhenReady` - Hide center HTML once all required players are ready

**Match Flow Control:**
- `SkipMapVoting` - Use current map, skip voting phase
- `SkipTeamPicking` - Random teams instead of captain selection
- `DisableCaptains` - Team voting for side selection instead of captain menu
- `AllowVolunteerCaptains` - Players can volunteer with `!volunteer_captain`

**Player Management:**
- `MinimumReadyPlayers` - Minimum to start (default 10)
- `RequireAllConnectedPlayersToBeReady` - ALL players must ready (not just minimum)
- `MoveOverflowPlayersToSpec` - Auto-move beyond 10 players to spectator
- `MovePlayersToSpecDuringTeamPicking` - Move unpicked players to spectator during team picking
- `PunishPlayerLeaves` - Punish disconnecting players (see below)
- `PreventNotPickedPlayersFromJoiningOngoingMatch` - Block non-picked players from joining
- `KickPlayersNotInMatch` - Kick players not in the active match roster

**Gameplay:**
- `FaceitLikeDamageControl` - Reflect friendly fire damage to shooter
- `Timeouts` - Number of timeouts per team (default 3)
- `TimeoutDurationSeconds` - Timeout length (default 60s)

**Map Settings** (`config.jsonc`):
- `DisallowVotePreviousMaps` - Exclude N recent maps from voting
- `DefaultVoteTimeSeconds` - Map voting duration (default 30s)

**Map Pool** (`maps.jsonc` → `MapsConfig.Maps`):
- Configured separately in `maps.jsonc` as a list of `MapDetails` objects
- Each entry: `MapName`, `DisplayName`, `WorkshopId`, `CanBeVoted`, `IsWorkshopMap`

**Vote Kick** (`config.jsonc` → `VoteKick`):
- `VoteKick.Enabled` - Enable/disable vote kick feature (default true)
- `VoteKick.VoteKickTime` - Voting window in seconds (default 30)

**Announcement Timers:**
- `ChatAnnouncementTimers.PlayersReadyStatus` - Ready status chat interval (default 30s)
- `ChatAnnouncementTimers.CaptainsAnnouncements` - Captain announcement interval (default 30s)
- `ChatAnnouncementTimers.CommandReminders` - Command help interval (default 320s)
- `CommandRemindersLocalization` - List of localization keys shown in command reminder messages (default: `["timeout", "ready", "invite"]`)

**Environment:**
- `TestMode` - Loads `staging_overrides.cfg` (bots, lower reqs), sets PluginState to Staging
- `DetailedLogging` - Verbose debug logs

**Player Leave Punishment:**
- `PunishPlayerLeaves` - Enable/disable punishment system
- `PlayerLeavePunishment.Sensitivity` - Which states trigger punishment:
  - `0` = Match/Timeout only (lenient)
  - `1` = + KnifeRound/PickingStartingSide (moderate)
  - `2` = + PickingTeam (strict)
- `PlayerLeavePunishment.ServerCommand` - Command template (e.g., `sw_ban {steamId} {duration} {reason}`)
- `PlayerLeavePunishment.BanDurationMinutes` - Ban length (default 15)
- `PlayerLeavePunishment.BanReason` - Reason shown to player
- `PlayerLeavePunishment.WaitBeforePunishmentSeconds` - Grace period before punishment (default 300s)

## Key Files Reference

**Core Components:**
- [MixScrims/src/Main.cs](MixScrims/src/Main.cs) - Plugin entry point (v1.6.1), DI, lifecycle, command registration
- [MixScrims/src/StateAgnostic/Main.cs](MixScrims/src/StateAgnostic/Main.cs) - Ready system, team logic, player punishment, timers
- [MixScrims/src/StateAgnostic/Announcements.cs](MixScrims/src/StateAgnostic/Announcements.cs) - Ready announcements, scoreboard/center HTML display
- [MixScrims/src/StateAgnostic/Events.cs](MixScrims/src/StateAgnostic/Events.cs) - Cross-state event handlers
- [MixScrims/src/Shared/MainConfig.cs](MixScrims/src/Shared/MainConfig.cs) - Main config model (match settings, commands, punishment, timers)
- [MixScrims/src/Shared/MapsConfig.cs](MixScrims/src/Shared/MapsConfig.cs) - Maps config model (map pool, loaded from `maps.jsonc`)
- [MixScrims/src/Shared/DiscordConfig.cs](MixScrims/src/Shared/DiscordConfig.cs) - Discord webhook config model (loaded from `discord_config.jsonc`)
- [MixScrims/src/Shared/MixScrimsService.cs](MixScrims/src/Shared/MixScrimsService.cs) - API service implementation
- [MixScrims/src/Shared/Helpers.cs](MixScrims/src/Shared/Helpers.cs) - Utility functions

**Contract API:**
- [MixScrims.Contract/IMixScrims.cs](MixScrims.Contract/IMixScrims.cs) - Public API interface (39 methods)
- [MixScrims.Contract/MatchState.cs](MixScrims.Contract/MatchState.cs) - Match state enum (`Ended`, `KnifeRound`, `MapChosen`, `MapLoading`, `MapVoting`, `Match`, `PickingStartingSide`, `PickingTeam`, `Timeout`, `Reset`, `Warmup`)
- [MixScrims.Contract/PluginState.cs](MixScrims.Contract/PluginState.cs) - Plugin state enum (Staging/Production)

**Commands:**
- [MixScrims/src/Commands/Admin/](MixScrims/src/Commands/Admin/) - Admin command handlers (one file per command: Captain, ForceReady, ForceUnready, Map, MaplistAll, Maps, MixReset, MixStart)
- [MixScrims/src/Commands/Player/](MixScrims/src/Commands/Player/) - Player command handlers (one file per command: Invite, Ready, Revote, Stay, Surrender, Switch, Timeout, Unready, VolunteerCaptain, VoteKick)

**Localization:**
- [MixScrims/resources/translations/en.jsonc](MixScrims/resources/translations/en.jsonc) - English localization keys
- [MixScrims/resources/translations/pt-BR.jsonc](MixScrims/resources/translations/pt-BR.jsonc) - Portuguese (Brazil) localization

## Available Commands

**Admin Commands** (require `managemix` permission):
- `!mix_reset` / `!reset` - Reset plugin state to warmup
- `!mix_start` / `!start` - Force start match
- `!forceready` / `!fr` - Force all players ready
- `!forceunready` / `!fur` - Force all players unready
- `!captain <name> <t/ct>` - Set captain manually
- `!map <mapname>` - Change to specific map
- `!maps` - List voteable maps
- `!maplist_all` - List all configured maps

**Player Commands:**
- `!ready` / `!r` - Mark yourself ready
- `!unready` / `!u` / `!ur` - Mark yourself not ready
- `!revote` / `!rv` - Change map vote
- `!timeout` / `!pause` - Vote for team timeout
- `!surrender` / `!gg` - Vote to surrender
- `!invite` / `!inv` - Send Discord invite
- `!stay` / `!st` - Stay on current side (knife winner)
- `!switch` / `!swap` - Switch sides (knife winner)
- `!volunteer_captain <t/ct>` - Volunteer as captain (if `AllowVolunteerCaptains` enabled)
- `!votekick <name>` / `!vk` - Vote to kick a player (if `VoteKick.Enabled`, during match)

## Integration Points

### Shared API Pattern
This plugin exposes `IMixScrims` for other plugins via Swiftly's shared interface system:
```csharp
// Provider (this plugin)
interfaceManager.AddSharedInterface<IMixScrims, MixScrimsService>("MixScrims.API", mixScrimsService);

// Consumer (other plugins)
var mixScrims = interfaceManager.GetSharedInterface<IMixScrims>("MixScrims.API");
var matchState = mixScrims.GetCurrentMatchState();
var pluginState = mixScrims.GetCurrentPluginState(); // Staging or Production
mixScrims.AddPlayerToPickedCtPlayers(steamId);
mixScrims.KickNotPlayingPlayers("Not participating");
```

**API Methods** (v1.6.1, 39 total):
- **State Management**: `GetCurrentMatchState()`, `SetMatchState()`, `GetCurrentPluginState()`, `SetPluginState()`
- **Team Names**: `SetCounterTerroristsTeamName()`, `SetTerroristsTeamName()`
- **Phase Control**: `StartWarmup()`, `StartMapVoting()`, `StartTeamPicking()`, `StartKnifeRound()`, `StartMatch()`, `CancelMatch()`
- **Timeout/Surrender**: `StartTimeoutCt()`, `StartTimeoutT()`, `StopTimeout()`, `SurrenderCt()`, `SurrenderT()`
- **Map Control**: `ChangeMap(string mapName = "", string workshopId = "")`
- **Ready System**: `ForceAllPlayersToReady()`, `ForceAllPlayersToUnready()`
- **Picked Players**: `GetPickedCtPlayers()`, `GetPickedTPlayers()`, `AddPlayerToPickedCtPlayers(steamId)`, `AddPlayerToPickedTPlayers(steamId)`, `RemovePlayerFromPickedCtPlayers(steamId)`, `RemovePlayerFromPickedTPlayers(steamId)`
- **Playing Players**: `GetPlayingCtPlayers()`, `GetPlayingTPlayers()`, `AddPlayerToPlayingCtPlayers(steamId)`, `AddPlayerToPlayingTPlayers(steamId)`, `RemovePlayerFromPlayingCtPlayers(steamId)`, `RemovePlayerFromPlayingTPlayers(steamId)`
- **Punishment System**: `GetPlayersWaitingForPunishment(ulong steamId)`, `AddPlayerToWaitingForPunishmentList(steamId)`, `RemovePlayerFromWaitingForPunishmentList(steamId)`
- **Captain Management**: `SetCtCaptain(steamId)`, `SetTCaptain(steamId)`
- **Player Control**: `KickNotPlayingPlayers(reason)`, `KickNotPickedPlayers(reason)`, `PreventNewPlayersJoining(bool)`

### Discord Webhooks
Optional Discord notifications via `DiscordService.cs` — sends rich embed messages to configured webhooks when players use `!invite`. Configured in `discord_config.jsonc` (`DiscordConfig` section): `EnableDiscordInvites`, `DiscordInviteDelayMinutes`, and a list of `Invites` (each with `WebhookUrl`, `Username`, `AvatarUrl`, `Content`, and an `Embed` with title/description/color/fields).

## Testing & Debugging

**Quick Test Setup:**
```csharp
// In config.jsonc
"MinimumReadyPlayers": 4,  // Lower for bot testing
"DetailedLogging": true,    // Verbose state transitions
"TestMode": true            // Loads staging_overrides.cfg with bots
```

**Console Commands:**
```
bot_add_ct; bot_add_ct; bot_add_t; bot_add_t  // Add 4 bots for testing
```

**Key Test Scenarios:** Default flow (all features), `SkipMapVoting`, `SkipTeamPicking`, `DisableCaptains`, edge cases (player disconnects during picking, odd player counts). See [Testing Guide](https://github.com/shmitzas/MixScrims-SwiftlyS2/wiki/%F0%9F%A7%AA-Testing-Guide) for comprehensive scenarios.

## Common Pitfalls

1. **Forgetting partial class scope** - Methods may already exist in other partial files
2. **Direct state modification** - Always use `mixScrimsService.SetMatchState()`
3. **Missing config entries** - New commands need both code + config entries
4. **CFG file deployment** - Changes to `.cfg` files require manual server deployment
5. **Player validity checks** - Always verify `IsValid` and `IsConnected` before operations
6. **ExcludeAssets confusion** - SwiftlyS2 packages must not bundle runtime assemblies
7. **Side selection modes** - Captain mode (`DisableCaptains: false`) shows menu to winner captain; team mode (`DisableCaptains: true`) requires majority vote from winning team
