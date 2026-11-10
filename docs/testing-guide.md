# MixScrims Manual Testing Guide

A structured reference for verifying correct plugin behaviour across every match state, configuration permutation, and edge case. Work through sections at your own pace — each section is self-contained.

---

## How to Use This Guide

- **Start small.** Sections 1–3 cover the minimum setup and sanity checks. Complete them before any other section.
- **Work top-to-bottom within each section.** Each numbered step depends on the ones above it.
- **Use the checkbox format.** Copy the section you are testing and tick boxes as you go.
- **Console shortcut** — add bots fast: `bot_add_ct; bot_add_ct; bot_add_ct; bot_add_ct; bot_add_ct; bot_add_t; bot_add_t; bot_add_t; bot_add_t; bot_add_t`
- **Recommended baseline config** (override per section as noted):

```jsonc
{
  "TestMode": true,
  "DetailedLogging": true,
  "MinimumReadyPlayers": 4,
  "RequireAllConnectedPlayersToBeReady": false,
  "SkipMapVoting": false,
  "SkipTeamPicking": false,
  "DisableCaptains": false,
  "AllowVolunteerCaptains": false,
  "FaceitLikeDamageControl": true,
  "MoveOverflowPlayersToSpec": true,
  "Timeouts": 3,
  "TimeoutDurationSeconds": 15,
  "DefaultVoteTimeSeconds": 15,
  "PunishPlayerLeaves": false,
  "PreventNotPickedPlayersFromJoiningOngoingMatch": false,
  "KickPlayersNotInMatch": false
}
```

---

## Section 1 — Server Startup & Plugin Load

**Goal:** Confirm the plugin loads without errors and warmup begins.

- [ ] Start CS2 server with the plugin installed.
- [ ] Console shows no `[ERROR]` or `[CRITICAL]` lines during load.
- [ ] `DetailedLogging` lines appear if enabled (`LoadWarmupConfig`, `Starting warmup`).
- [ ] `warmup.cfg` executes (confirm with `mp_warmuptime` being unlimited via `mp_warmuptime` in console).
- [ ] `staging_overrides.cfg` executes when `TestMode: true`; `production_overrides.cfg` executes when `TestMode: false`.
- [ ] Plugin state = `Warmup` (check via log or API if available).
- [ ] Periodic ready-status announcements start after `ChatAnnouncementTimers.PlayersReadyStatus` seconds.
- [ ] Command reminder announcements start after `ChatAnnouncementTimers.CommandReminders` seconds.
- [ ] If `ShowReadyStatusInCenterHtml: true` — center HTML overlay appears and updates each second.

---

## Section 2 — Warmup State

**Goal:** Verify the ready system, player joining, and auto-balance.

### 2a — Ready / Unready commands

- [ ] Player types `!ready` (or alias `!r`) → chat confirms they are ready. Scoreboard prefix updates if `ShowReadyStatusInScoreboard: true`.
- [ ] Same player types `!ready` again → error "already ready" (no duplicate in list).
- [ ] Player types `!unready` (or aliases `!u`, `!ur`) → chat confirms unready. Scoreboard prefix removed.
- [ ] Player types `!unready` when already unready → error "already unready".
- [ ] CenterHTML overlay (if enabled) updates within 1 second to reflect new count.

### 2b — Minimum player threshold (`MinimumReadyPlayers`)

**With `RequireAllConnectedPlayersToBeReady: false`:**
- [ ] With `MinimumReadyPlayers: 4`, ready exactly 4 players → voting / next state triggers immediately.
- [ ] With 3 ready players, count stays at 3/4 — no state transition.
- [ ] Admin command `!forceready` (`!fr`) marks all connected players ready and triggers transition.
- [ ] Admin command `!forceunready` (`!fur`) unmarks everyone.

**With `RequireAllConnectedPlayersToBeReady: true`:**
- [ ] Even with minimum count met, transition does not happen until **every** connected player is ready.
- [ ] A new player joining resets the gate (they must also ready up).

### 2c — Player connect during Warmup

- [ ] Player joins → automatically placed in spectator first (after 2-second delay).
- [ ] Then assigned to the team with fewer players (CT or T, alternating).
- [ ] Player is respawned on assignment.
- [ ] If `MoveOverflowPlayersToSpec: true` and both teams have `MinimumReadyPlayers / 2` players, additional joiners stay in spectator.
- [ ] Joining player can `!ready` and be counted.

### 2d — Team auto-balance on join (tie-breaker)

- [ ] With 2 CT and 2 T players, next joiner alternates to whichever team the **previous** auto-join did NOT go to (CT → T → CT …).

### 2e — `!maps` and `!maplist_all` admin commands

- [ ] `!maps` (`!maplist`) prints the voteable map pool.
- [ ] `!maplist_all` (`!allmaps`, `!maps_all`) prints every configured map, including the recently played ones `!maps` omits.

---

## Section 3 — Admin Captain Assignment (pre-state)

**Goal:** Confirm `!captain` sets captains before team picking starts.

- [ ] Admin `!captain PlayerName t` → chat confirms "PlayerName set as T captain". `captainT` field populated.
- [ ] Admin `!captain PlayerName ct` → confirms "PlayerName set as CT captain". `captainCt` field populated.
- [ ] Setting captain for a player not on the server returns an error (invalid player).
- [ ] Captains set this way survive through the MapChosen → TeamPicking transition (they are inserted into picked lists on state start).

---

## Section 4 — Map Voting State

**Goal:** Verify the map vote menu, vote registration, revote, and winner selection.

### 4a — Vote menu display

- [ ] State transition shows `announcement.state_changed.map_voting` chat message.
- [ ] Each connected non-bot player receives the map voting menu.
- [ ] Menu contains every configured map that is not excluded by `DisallowVotePreviousMaps`.
- [ ] Map order in menu is randomised on each vote start.
- [ ] Bots do **not** receive the menu (they are skipped).

### 4b — Voting

- [ ] Player clicks a map → chat shows "[Player] voted for [Map] [1 votes]".
- [ ] Second player votes for the same map → "[Map] [2 votes]".
- [ ] Player uses `!revote` (alias `!rv`) → previous vote removed, menu reopened.
- [ ] After revote, player picks a different map → vote transferred correctly.
- [ ] Player attempts `!revote` outside MapVoting state → error "invalid state".

### 4c — Vote resolution

- [ ] Vote timer expires (`DefaultVoteTimeSeconds`) → map with most votes announced.
- [ ] All players close their menu on announcement.
- [ ] Map load begins (state transitions to `MapLoading`).
- [ ] "Map loading" announcement fires every 15 seconds until map loads.
- [ ] **Tie scenario:** When two maps share the highest vote count, one is selected (random or first-found is acceptable — just confirm no crash).
- [ ] **Zero votes:** Nobody votes → a random eligible map is selected, no crash.

### 4d — `DisallowVotePreviousMaps`

- [ ] Play two matches ending on `de_mirage` and `de_dust2` (with `DisallowVotePreviousMaps: 2`).
- [ ] Third vote: menu does **not** include `de_mirage` or `de_dust2`.
- [ ] Fourth vote: `de_mirage` reappears (oldest entry rolls off the history list).
- [ ] Play a match on a **workshop** map → it is kept out of later votes too. History records the configured entry rather than the engine's internal map name.
- [ ] With `DisallowVotePreviousMaps: 0` → all maps always available; `playedMaps` cleared to prevent out-of-range errors.

### 4e — Current map excluded

- [ ] Open a vote during warmup, before any match has run on this map → the menu does **not** list the map currently loaded.
- [ ] Repeat on a **workshop** map, with its `Id` written either bare (`"3070596702"`) or prefixed (`"ws:3070596702"`) → excluded in both cases. The engine names a workshop map by an internal name, so the match is made on the workshop ID instead.
- [ ] Repeat on a stock map (`de_dust2`) → still excluded, matched by name.
- [ ] With a single-entry `maps.jsonc` → that one map is still offered; the vote does not collapse to `Reset`.

---

## Section 5 — MapChosen / Post-Map-Load Ready State

**Goal:** Verify the second ready state after the map loads.

- [ ] After map load, state = `MapChosen`. Warmup config executes again.
- [ ] Ready list is **empty** (previous warmup ready list cleared on reset).
- [ ] Players must `!ready` again to start team picking.
- [ ] `MinimumReadyPlayers` and `RequireAllConnectedPlayersToBeReady` rules apply identically to Warmup.
- [ ] CaptainSelectionDelaySeconds countdown fires: "Selecting captains in X seconds" messages appear.
- [ ] After delay, captains are announced (randomly from ready list unless pre-set or volunteers exist).
- [ ] If only `AllowVolunteerCaptains: true` captains exist, they are used. Otherwise, random selection.

---

## Section 6 — Team Picking State (captain mode)

**Goal:** Validate the pick menu flow, alternation, and edge cases.

**Config:** `SkipTeamPicking: false`, `DisableCaptains: false`

### 6a — State start

- [ ] `teampick.cfg` executes.
- [ ] Game pauses.
- [ ] Players are moved to spectator or their designated team side (`MovePlayersToDesignatedTeamsPrePick`).
- [ ] Team names updated to captain names (e.g., `team_Alice`, `team_Bob`).
- [ ] One captain receives the pick menu first (random CT or T to start).

### 6b — Picking flow

- [ ] Captain selects a player → player added to that captain's picked list, menu reopens for opponent captain.
- [ ] Picks alternate CT ↔ T after each selection.
- [ ] A captain cannot pick the opposing captain (they are already in the other picked list).
- [ ] When all non-captain players are distributed, Knife Round starts automatically.
- [ ] If `MoveOverflowPlayersToSpec: true` and team is full (`MinimumReadyPlayers / 2` per side), extra players go to spectator.

### 6c — Captain disconnects during picking

- [ ] CT captain disconnects → a replacement captain is selected from remaining picked CT players and picking restarts.
- [ ] T captain disconnects → same for T side.
- [ ] Both captains disconnect simultaneously → logs errors, attempts reset if no players remain for captain.

### 6d — Non-captain player disconnects during picking

- [ ] Player in the unpicked pool disconnects → removed from `pickedCtPlayers` / `pickedTPlayers`, remaining picks continue normally.
- [ ] Player listed in the **open** pick menu disconnects → the captain's menu is rebuilt without them; no button for a player who already left.
- [ ] The **last** unpicked player disconnects while the menu is open → the knife round starts on its own, without the captain clicking anything.
- [ ] Two players connect with the **same in-game name** → both can be picked, and picking one does not consume the other's slot.

---

## Section 7 — Knife Round State

**Goal:** Confirm knife-only config, winner detection, and side-selection prompt.

### 7a — State start

- [ ] `knife_round.cfg` executes (knife only, no C4).
- [ ] `playingCtPlayers` and `playingTPlayers` populated from `pickedCtPlayers` / `pickedTPlayers`.
- [ ] Pre-match announcement timers stopped.
- [ ] Entered straight from Warmup by an API consumer (skipping PickingTeam) → the ready-count centre HUD and the ready-status chat lines stop too.
- [ ] Scoreboard "ready" clan-tags removed (if `ShowReadyStatusInScoreboard: true`).
- [ ] Any open pick menus for captains are closed.
- [ ] If `KickPlayersNotInMatch: true` → spectating non-participants are kicked.

### 7b — Knife round play

- [ ] Confirm C4 is not given to T side (check `mp_give_player_c4 0`).
- [ ] Players can only use knives; rifles/pistols not spawned.
- [ ] Round ends by team elimination.
- [ ] Let the round clock run out → "time ran out - the winner is picked at random" announcement, then the normal winner line and the side-pick prompt appear; the server does not stall (no `!mix_reset` needed).

### 7c — Side selection — Captain mode (`DisableCaptains: false`)

- [ ] Winning team's captain receives the Stay / Switch menu.
- [ ] All players see "CT/T team won the knife round" announcement.
- [ ] All players see "Waiting for [captain] to pick a side" announcement.
- [ ] **Bot captain:** automatically selects "Switch" (hardcoded fallback).
- [ ] Captain clicks **Stay** → `!stay` (`!st`) confirmed, match starts with current sides.
- [ ] Captain clicks **Switch** → `!switch` (`!swap`) confirmed, teams swap sides, match starts.
- [ ] Captain does not pick within 30 s → match starts on the current sides (Stay) and the Stay / Switch menu disappears from the captain's screen.
- [ ] Captain disconnects during side selection → `StayStartingSides` fires automatically (no hang).

### 7d — Side selection — Team vote mode (`DisableCaptains: true`)

- [ ] Winning team all receive Stay / Switch menu.
- [ ] "Team vote started" announcement shown to all.
- [ ] Players vote; majority wins (tied vote resolved by first-to-pass logic).
- [ ] 30-second timer auto-resolves if not all players vote.
- [ ] When the 30-second timer resolves the vote, the Stay / Switch menu disappears for every winning-team player who had not voted.
- [ ] Each player's vote is recorded via `sideVotes` dict; duplicate vote attempt ignored.

---

## Section 8 — Match State

**Goal:** Validate in-match behaviour, halftime, and end-of-match reset.

### 8a — Match start

- [ ] `match_start.cfg` executes.
- [ ] `announcement.state_changed.match` message shown to all.
- [ ] All playing players moved to designated CT / T teams.
- [ ] Pre-match announcement timers stopped.
- [ ] Scoreboard clan tags cleared.
- [ ] Teammate colours assigned (no duplicates within a team).
- [ ] Current map added to `playedMaps` history.
- [ ] If `KickPlayersNotInMatch: true` → spectating non-participants kicked.

### 8b — Halftime

- [ ] At halftime announcement, `isMovingPlayersToTeams` set to true.
- [ ] On first round start after halftime, `playingCtPlayers` ↔ `playingTPlayers` swap.
- [ ] After swap, `isMovingPlayersToTeams` set back to false.
- [ ] Team names update correctly after swap (check scoreboard).

### 8c — Match end / auto-reset

- [ ] Match ends normally (one team wins 13 rounds, `EventCsWinPanelMatch` fires).
- [ ] Win panel shown for ~10 seconds.
- [ ] Plugin resets to Warmup after 10-second delay.
- [ ] All variable lists cleared (ready players, picked players, captains, votes, etc.).
- [ ] Announcement timers restart.

### 8d — Friendly fire (FaceitLikeDamageControl)

**With `FaceitLikeDamageControl: true`:**
- [ ] Player deals 50 damage to a teammate → dealer receives 50 damage reflected back.
- [ ] Teammate HP is **not** reduced.

**With `FaceitLikeDamageControl: false`:**
- [ ] Standard friendly fire rules from `match_start.cfg` apply (no reflection).

---

## Section 9 — Timeouts

**Goal:** Verify timeout votes, queueing, and timer accuracy.

### 9a — Starting a timeout vote

**With team size > 2:**
- [ ] Player types `!timeout` (`!pause`) during Match → vote menu opened for all teammates except caller.
- [ ] Caller auto-votes Yes.
- [ ] Bots auto-vote Yes.
- [ ] Vote progress shown in chat: "X yes, Y no out of Z".
- [ ] Required votes shown equals team size − 1.
- [ ] Team size − 1 Yes votes reached → timeout starts immediately (vote timer cancelled).
- [ ] Two players vote No → vote fails immediately ("timeout vote failed" message), no pause.
- [ ] Vote timer expires (`DefaultVoteTimeSeconds`) below the threshold → vote fails.

**With team size ≤ 2:**
- [ ] `!timeout` auto-passes without showing a menu.

### 9b — Timeout execution

- [ ] On pass: game pauses, "CT/T timeout started" announcement shown to all.
- [ ] Remaining timeouts shown to the calling team: "X/Y timeouts remaining".
- [ ] Countdown broadcasts fire during timeout.
- [ ] After `TimeoutDurationSeconds`, timeout ends: "Timeout ended" message, game resumes.
- [ ] State reverts from `Timeout` → `Match`.

### 9c — Timeout counter enforcement

**With `Timeouts: 3`:**
- [ ] Use all 3 timeouts on one team → fourth `!timeout` attempt shows "0/3 remaining" error, vote not started.
- [ ] Other team's timeout counter is independent.

### 9d — Timeout queueing

- [ ] CT timeout active; T team calls `!timeout` → T timeout queued: "T timeout queued" message.
- [ ] CT timeout ends → if in freeze time, T timeout starts immediately; otherwise marked pending for next freeze time.
- [ ] "T timeout pending" announcement shown.
- [ ] Round starts → pending timeout fires.
- [ ] Both teams queue simultaneously (queue is FIFO, handles duplicates).

### 9e — `!timeout` outside Match state

- [ ] During Warmup, `!timeout` → error "invalid state for command".

---

## Section 10 — Surrender

**Goal:** Verify surrender vote mechanics and state transition.

### 10a — Starting a surrender vote

**With team size > 2:**
- [ ] Player types `!surrender` (`!gg`) during Match → surrender menu opened for teammates.
- [ ] Caller auto-votes Yes (1 yes from the start).
- [ ] Bots auto-vote Yes.
- [ ] Vote progress: "X yes, Y no out of Z" shown to team.
- [ ] Required votes shown equals the full team size.
- [ ] All Yes → `Surrender` state entered immediately, opposing team wins.
- [ ] Any No → vote fails and announced, match continues.
- [ ] Vote timer expires (`DefaultVoteTimeSeconds`) below unanimous → vote fails.

**With team size ≤ 2:**
- [ ] `!surrender` auto-passes without menu.

### 10b — Surrender state

- [ ] Chat announces which team surrendered.
- [ ] Plugin transitions to Warmup after the standard post-match delay.

### 10c — `!surrender` outside Match state

- [ ] During Warmup / MapVoting / PickingTeam → error "invalid state".

---

## Section 11 — Vote Kick

**Goal:** Verify the vote kick mechanics inside an active match.

### 11a — Starting a vote kick

- [ ] During Match, `!votekick PlayerName` (`!vk PlayerName`) → chat announces "vote started against PlayerName".
- [ ] All eligible teammates (excluding caller and target) receive Yes/No menu.
- [ ] Caller auto-votes Yes (1 yes from the start).
- [ ] Bots auto-vote Yes.
- [ ] CenterHTML progress overlay appears immediately and updates after each vote.

### 11b — All Yes — kick passes

- [ ] All eligible voters click Yes → target is kicked from server.
- [ ] Chat to team: "PlayerName was kicked by vote".
- [ ] Vote timer cancelled.
- [ ] Vote state cleaned up (no stale `isVoteKickInProgress*` flag).

### 11c — Any No — immediate failure

- [ ] One voter clicks No → vote cancelled immediately (no waiting for timer).
- [ ] Chat: "vote to kick PlayerName failed".
- [ ] All open Yes/No menus closed for the team.
- [ ] Target is **not** kicked.

### 11d — Timer expiry

- [ ] Nobody else votes; wait `VoteKickTime` seconds → vote fails.
- [ ] Chat: "vote to kick PlayerName failed".
- [ ] Target not kicked, state cleaned up.

### 11e — Guard conditions (should all produce errors, no vote started)

- [ ] `!votekick` against self → error "cannot kick yourself".
- [ ] `!votekick` during Warmup / MapVoting / PickingTeam / KnifeRound → error "match only".
- [ ] `!votekick` while another vote for that team is already in progress → error "vote already in progress".
- [ ] `!votekick` targeting a player on the **opposite** team → error (caller's team scope check).
- [ ] Spectator or bot calling `!votekick` → rejected by player validity check.

### 11f — Only one eligible voter

- [ ] Caller is the only teammate besides the target → vote auto-passes immediately (no menu shown), target kicked.

### 11g — Simultaneous CT and T votes

- [ ] Start a vote on CT side; simultaneously start a vote on T side.
- [ ] Both run independently, CenterHTML for each team shows only their own progress.
- [ ] Resolving one vote (pass or fail) does not affect the other.

### 11h — Disabled config

- [ ] Set `VoteKick.Enabled: false`, reload/restart.
- [ ] `!votekick` → command not registered (error or "unknown command").
- [ ] `!vk` alias also unavailable.

---

## Section 12 — Player Disconnect Scenarios

**Goal:** Verify graceful handling of disconnects in every state.

### 12a — Disconnect during Warmup / MapVoting / MapChosen

- [ ] Ready player disconnects → removed from `readyPlayers`, ready count decrements.
- [ ] Count falls below threshold → state does **not** regress (no automatic un-start).
- [ ] Player rejoins → treated as new joiner, must `!ready` again.
- [ ] Player who already voted for a map disconnects → that map's tally drops by one.
- [ ] A new player connects into the leaver's slot and votes → their vote is counted as a first vote, not as a change of mind.

### 12b — Disconnect during Team Picking

- [ ] Non-captain player disconnects → removed from `pickedCtPlayers` / `pickedTPlayers` if they were picked; picking continues.
- [ ] **CT captain disconnects** → `captainCt = null`, `StartTeamPickingPhase()` restarts immediately with new random captain.
- [ ] **T captain disconnects** → same for T side.
- [ ] See 6d for the open-pick-menu cases.

### 12c — Disconnect during Knife Round

- [ ] Non-captain playing player disconnects → removed from `playingCtPlayers` / `playingTPlayers`, match continues.
- [ ] **CT captain disconnects** → `captainCt` reassigned to next available CT player from `playingCtPlayers`.
- [ ] **T captain disconnects** → same for T side.

### 12d — Disconnect during PickingStartingSide

- [ ] Winning captain (knife winner) disconnects → `StayStartingSides` auto-fires, match proceeds with current sides (no hang).
- [ ] `DisableCaptains: true`, the last winning-team player who had not voted disconnects → the vote resolves immediately instead of running the 30s timer out.
- [ ] `DisableCaptains: true`, a player votes and then disconnects → their vote no longer counts toward Switch/Stay or toward closing the vote.

### 12e — Disconnect during Match / Timeout

- [ ] Player disconnects → removed from `playingCtPlayers` / `playingTPlayers`.
- [ ] If `PunishPlayerLeaves: true` and `Sensitivity ≥ 0` → player added to punishment queue (see Section 16).
- [ ] Match continues normally with fewer players.

### 12f — Rapid disconnect/reconnect

- [ ] Player disconnects and reconnects within 1 second → `recentlyDisconnectedPlayers` deduplication prevents double-handling.
- [ ] No duplicate removal from team lists.

---

## Section 13 — Player Change Team Scenarios

**Goal:** Confirm `jointeam` interception works correctly in each state.

### 13a — During Warmup / MapVoting / MapChosen

- [ ] Player opens team selection (F3/F4 or console `jointeam 2`/`jointeam 3`) → `HandlePlayerChangeTeam` fires.
- [ ] Player is auto-balanced to the smaller team.
- [ ] Player is respawned immediately after assignment.
- [ ] Manually trying `jointeam 9` (invalid) → blocked, logged as error.

### 13b — During PickingTeam

- [ ] A non-picked spectating player tries `jointeam 2` → intercepted, blocked or redirected based on current state logic.
- [ ] Captain trying to change sides → blocked (captain must remain on their assigned side).

### 13c — During Match

- [ ] **`PreventNotPickedPlayersFromJoiningOngoingMatch: false`** (default) → any player can join any open team slot normally.
- [ ] **`PreventNotPickedPlayersFromJoiningOngoingMatch: true`** → player not in `playingCtPlayers` or `playingTPlayers` or picked lists is immediately kicked with "not picked" reason.
- [ ] A legitimate playing player who reconnects and is in the playing list is **not** kicked.

### 13d — During Match with `KickPlayersNotInMatch: true`

- [ ] On `StartMatch()`, all non-playing spectators are kicked immediately.
- [ ] On `StartKnifeRound()`, same kick logic applies.
- [ ] Legitimate playing players are **not** kicked.

---

## Section 14 — Admin Force Commands

**Goal:** Validate all admin-only commands work correctly.

- [ ] `!mix_reset` (`!reset`) — resets plugin to Warmup from any state. All lists cleared. Warmup CFG reloaded.
- [ ] `!mix_start` (`!start`) — force-starts the match from any state (jumps to `StartMatch()`). Confirm no crash if called from Warmup.
- [ ] `!forceready` (`!fr`) — adds all connected non-bot players to ready list, triggers `CheckReadyPlayersToStart`.
- [ ] `!forceunready` (`!fur`) — removes all players from ready list.
- [ ] `!captain PlayerName ct` — sets CT captain. Valid name → success. Invalid name → error.
- [ ] `!captain PlayerName t` — sets T captain. Valid name → success. Invalid name → error.
- [ ] `!map mapname` (`!changemap`) — changes to specified map regardless of current state.
- [ ] `!maps` (`!maplist`) — prints voteable map list to admin.
- [ ] `!maplist_all` (`!allmaps`, `!maps_all`) — prints all configured maps to admin.
- [ ] Non-admin player attempts any admin command → denied by permission check (`managemix`).
- [ ] Admin commands work from console (server/RCON) as well as in-game chat.

---

## Section 15 — Volunteer Captains

**Config:** `AllowVolunteerCaptains: true`

- [ ] During Warmup or MapChosen, player types `!volunteer_captain ct` (`!volcap ct`, `!selfcapt ct`) → "you volunteered as CT captain" message.
- [ ] Player types `!volunteer_captain t` → "you volunteered as T captain" message.
- [ ] When team picking starts, volunteer captains take priority over random selection.
- [ ] If only one volunteer exists, that slot is filled from volunteers; the other is chosen randomly.
- [ ] Two players volunteer for the same side → the first volunteer is used, second is ignored or superseded (verify consistent behaviour).
- [ ] `!volunteer_captain` with no argument → error "specify t or ct".
- [ ] `!volunteer_captain` outside Warmup / MapChosen → check whether error or silently ignored.

**With `AllowVolunteerCaptains: false`:**
- [ ] `!volunteer_captain` → command not registered.

---

## Section 16 — Player Leave Punishment

**Config:** `PunishPlayerLeaves: true`

### 16a — Sensitivity levels

**Sensitivity 0 (Match / Timeout only):**
- [ ] Player leaves during Match → added to punishment queue.
- [ ] Player leaves during Timeout → added to punishment queue.
- [ ] Player leaves during KnifeRound → **not** punished.
- [ ] Player leaves during PickingTeam → **not** punished.

**Sensitivity 1 (+ KnifeRound / PickingStartingSide):**
- [ ] Player leaves during KnifeRound → added to punishment queue.
- [ ] Player leaves during PickingStartingSide → added to punishment queue.
- [ ] Player leaves during PickingTeam → **not** punished.

**Sensitivity 2 (+ PickingTeam):**
- [ ] Player leaves during PickingTeam → added to punishment queue.
- [ ] All states from lower sensitivities also trigger punishment.

### 16b — Punishment execution

- [ ] After `WaitBeforePunishmentSeconds` (default 300), the configured `ServerCommand` fires with `{steamId}`, `{duration}`, `{reason}` substitutions.
- [ ] Player reconnects within the grace period → removed from punishment queue, punishment **not** issued.
- [ ] `PlayerLeavePunishment.BanDurationMinutes` substituted into `{duration}`.
- [ ] `PlayerLeavePunishment.BanReason` substituted into `{reason}`.

**With `PunishPlayerLeaves: false`:**
- [ ] Player leaves during any state → `PunishOnLeave` logs "punishment disabled", no command issued.

---

## Section 17 — Display & Announcement Settings

### 17a — `ShowReadyStatusInChat`

**`true` (default):**
- [ ] Periodic chat messages list ready and not-ready players.
- [ ] Interval matches `ChatAnnouncementTimers.PlayersReadyStatus`.

**`false`:**
- [ ] No periodic ready-status chat messages.

### 17b — `ShowReadyStatusInScoreboard`

**`true` (default):**
- [ ] Ready players have a "[R]" or configured prefix in their clan tag.
- [ ] Unready players have a "[NR]" or configured prefix.
- [ ] Tags cleared at Knife Round start and Match start.

**`false`:**
- [ ] No clan tag modification at any point.

### 17c — `ShowReadyStatusInCenterHtml`

**`true` (default):**
- [ ] Center HTML overlay visible to all players during Warmup / MapChosen.
- [ ] Updates every 1 second.

**`false`:**
- [ ] No center HTML overlay.

### 17d — `HideReadyStatusInCenterWhenReady`

**`true`:**
- [ ] Player marks ready → their own center HTML display disappears (others still see it).

**`false` (default):**
- [ ] Center HTML visible to all regardless of ready status.

### 17e — `GlobalServerPrefix`

- [ ] Change prefix in config (e.g., `"[ [green]TestServer [default]]"`) → all plugin chat messages use new prefix.
- [ ] Empty string → messages appear without prefix.

### 17f — Announcement timer intervals

- [ ] `ChatAnnouncementTimers.PlayersReadyStatus: 10` → ready-status chat fires every 10 seconds.
- [ ] `ChatAnnouncementTimers.CommandReminders: 30` → command reminders fire every 30 seconds.
- [ ] `ChatAnnouncementTimers.CaptainsAnnouncements: 15` → captain announcements during MapChosen fire every 15 seconds.
- [ ] After Knife Round starts, **all three** pre-match timers are stopped (`StopPreMatchAnnouncementTimers`).

---

## Section 18 — Shortcut / Skip Flags

### 18a — `SkipMapVoting: true`

- [ ] After enough players ready in Warmup, state goes directly to `MapChosen` (no voting menu).
- [ ] Server stays on the current map.
- [ ] MapChosen ready state starts normally.
- [ ] `playedMaps` history still updated when match begins.

### 18b — `SkipTeamPicking: true`

- [ ] After MapChosen ready state, captains are still selected (random/volunteer).
- [ ] No pick menu shown; all players on CT/T at map start remain on those sides.
- [ ] `MoveOverflowPlayersToSpec` still applies (excess players in spectator).
- [ ] Knife round starts immediately after "team picking" state.

### 18c — `DisableCaptains: true`

- [ ] No captain selection at any point.
- [ ] Team names remain `COUNTER-TERRORISTS` / `TERRORISTS`.
- [ ] `SkipTeamPickingPhase` assigns players based on current team membership.
- [ ] Side selection after knife round uses **team vote** instead of captain menu.
- [ ] All players on winning knife-round team receive Stay/Switch menu.
- [ ] Majority determines side; tie resolved by auto-Stay (or first-to-reach logic — confirm no hang).

### 18d — Combination: `SkipMapVoting: true` + `SkipTeamPicking: true`

- [ ] Warmup → immediately to MapChosen → immediately to Knife Round → Match.
- [ ] No menus shown at all. No crash.

### 18e — Combination: `SkipMapVoting: true` + `DisableCaptains: true`

- [ ] Warmup ready → MapChosen ready → auto-assign teams → Knife Round → team vote for sides → Match.

---

## Section 19 — Map Configuration

### 19a — Commenting an entry out

- [ ] A map whose entry is commented out in `maps.jsonc` does not appear in the voting menu.
- [ ] Neither `!maps` nor `!maplist_all` lists it.
- [ ] Admin `!map de_commented_map` is **refused** — a commented entry is not in the pool at all, unlike the removed `CanBeVoted: false` which still allowed it.

### 19b — Workshop map (numeric `Id`)

- [ ] Map appears in vote menu with its `Name`.
- [ ] On selection, `nextlevel <Id>` then `host_workshop_map <Id>` are executed (check console).
- [ ] Same entry written as `"ws:<Id>"` loads the identical map.
- [ ] An official map instead executes `nextlevel <Id>` then `changelevel <Id>`.
- [ ] An entry omitting `Id` loads by its `Name`.
- [ ] A leftover `"CanBeVoted": false` on an entry is ignored — the map is in the vote like any other.
- [ ] A `maps.jsonc` still written as `MapName` / `DisplayName` / `WorkshopId` logs an error at startup naming the file and the expected shape.

### 19c — Empty map pool

- [ ] Comment out every entry in `maps.jsonc` → `StartMapVotingPhase` logs error, state resets to `Reset`, plugin resets to Warmup.

---

## Section 20 — `TestMode` and `DetailedLogging`

### 20a — `TestMode: true`

- [ ] `staging_overrides.cfg` executed (verify via console — should configure bot counts etc.).
- [ ] `PluginState` reported as `Staging` via API or log.

### 20b — `TestMode: false`

- [ ] `production_overrides.cfg` executed.
- [ ] `PluginState` reported as `Production`.

### 20c — `DetailedLogging: true`

- [ ] Console is verbose: every state transition, player action, and list change is logged.
- [ ] Logs include method names, player names, and counts.

### 20d — `DetailedLogging: false`

- [ ] Console shows only warnings and errors. No verbose info lines.

---

## Section 21 — Discord Integration

**Config:** `DiscordInviteWebhooks` populated with at least one valid webhook URL.

- [ ] Player types `!invite` (`!inv`) → Discord webhook fires, invite message sent to configured channel.
- [ ] Webhook URL is invalid or unreachable → plugin logs error, does **not** crash. Player sees appropriate message.
- [ ] `lastDiscordInviteSentAt` cooldown: sending `!invite` twice quickly → second message may be rate-limited (verify no duplicate webhook spam).

---

## Section 22 — State Machine Integrity (edge cases)

**Goal:** Confirm no state can get stuck or enter an invalid transition.

- [ ] `!mix_reset` called during MapVoting → timers cancelled, menus closed, Warmup starts cleanly.
- [ ] `!mix_reset` called during PickingTeam (mid-pick) → captain menus closed, lists cleared, Warmup starts.
- [ ] `!mix_reset` called during active Timeout → timeout timer cancelled, game unpaused, Warmup starts.
- [ ] `!mix_reset` called during active surrender vote → vote timer cancelled, match state cleared, Warmup starts.
- [ ] `!mix_reset` called during active vote kick → vote kick state cleaned up for both teams, Warmup starts.
- [ ] `!mix_start` called during MapVoting → jumps directly to `StartMatch()` (teams may be empty — log warning expected, no crash).
- [ ] Rapid `!ready` spam from many players simultaneously → no duplicate entries in `readyPlayers`.
- [ ] Map change triggered externally (non-plugin `map` command) mid-match → all `StopOnMapChange` tokens fire, timers stopped, plugin starts fresh on new map.

---

## Section 23 — Command Aliases and Permissions

**Goal:** Confirm all aliases work and permissions are enforced.

### Player commands (no permission required)

| Command | Aliases to test |
|---------|-----------------|
| `!ready` | `!r` |
| `!unready` | `!u`, `!ur` |
| `!revote` | `!rv` |
| `!timeout` | `!pause` |
| `!surrender` | `!gg` |
| `!invite` | `!inv` |
| `!stay` | `!st` |
| `!switch` | `!swap` |
| `!volunteer_captain` | `!volcap`, `!selfcapt` |
| `!votekick` | `!vk` |

- [ ] Each alias produces the same result as the primary command.
- [ ] Player without `managemix` permission cannot use admin aliases.

### Admin commands (require `managemix`)

| Command | Aliases to test |
|---------|-----------------|
| `!mix_reset` | `!reset` |
| `!mix_start` | `!start` |
| `!forceready` | `!fr` |
| `!forceunready` | `!fur` |
| `!captain` | `!cap`, `!capt` |
| `!map` | `!changemap` |
| `!maps` | `!maplist` |
| `!maplist_all` | `!allmaps`, `!maps_all` |

- [ ] Each alias works for an admin.
- [ ] Each command is blocked for a non-admin with an appropriate denial message.

### Custom aliases (config override)

- [ ] Change an alias in `config.jsonc` (e.g., `"ready": { "Aliases": ["go"] }`), reload config.
- [ ] New alias `!go` works. Old alias `!r` no longer works (if replaced).

---

## Section 24 — Localization

**Goal:** Confirm all keys render correctly in both supported languages.

- [ ] Set server to `en` — confirm all chat, menu, and center HTML messages appear in English.
- [ ] Set server to `pt-BR` — confirm all messages appear in Portuguese (Brazil).
- [ ] No raw key strings (e.g., `"command.ready"`) appear in chat — all keys resolve to translated strings.
- [ ] Keys that include arguments (player name, count) substitute correctly (e.g., "[Alice] is ready").
- [ ] New votekick keys present in both files: `command.votekick.started`, `command.votekick.passed.ct`, `command.votekick.passed.t`, `command.votekick.failed`, `menu.votekick`, `info.kick_reason.votekicked`.

---

## Section 25 — Full End-to-End Run (Regression)

**Goal:** Complete one full match from startup to post-match reset, using default config, verifying every state transition in sequence.

- [ ] **1. Startup** — Plugin loads, Warmup begins, `warmup.cfg` executes.
- [ ] **2. Warmup** — 4+ players ready. State transitions to MapVoting.
- [ ] **3. MapVoting** — Players vote. Timer expires or all vote. Winner announced. Map loads.
- [ ] **4. MapChosen** — Players ready again. Captains selected after delay.
- [ ] **5. PickingTeam** — Captains alternate picks until teams are full. State transitions to KnifeRound.
- [ ] **6. KnifeRound** — `knife_round.cfg` active. Round played to completion.
- [ ] **7. PickingStartingSide** — Winner captain receives menu. Selects Stay or Switch.
- [ ] **8. Match** — `match_start.cfg` active. MR12 match plays. Timeout and surrender available.
- [ ] **9. Match End** — Win panel shown. After 10 seconds, plugin auto-resets.
- [ ] **10. Warmup (again)** — All state clean. Announcement timers restarted. No stale data.

---

## Quick Reference: Console Snippets

```
// Add 10 bots quickly
bot_add_ct; bot_add_ct; bot_add_ct; bot_add_ct; bot_add_ct
bot_add_t; bot_add_t; bot_add_t; bot_add_t; bot_add_t

// Check current map
host_map

// Verify warmup config loaded
mp_warmuptime

// Verify knife round config loaded
mp_give_player_c4

// Force a round end (for testing transitions)
mp_restartgame 1

// Switch to a specific map manually
map de_mirage
```
