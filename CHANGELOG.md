# Changelog

Notable changes to MixScrims, newest first.

<!-- Release notes are taken from the section matching PluginMetadata.Version in
     MixScrims/src/Main.cs, so every release needs a "## [x.y.z]" heading here. -->

## [1.12.0] - 2026-10-10

Must update to this version in order to avoid random crashes.

## API consumers must update (contract 2.9.0).

- This API change also improved async <> main-thread safety. Check [documentation](https://github.com/shmitzas/MixScrims/wiki/API-Reference#thread-affinity) to find what needs to be updated for all plugins that use MixScrimsAPI.

## New maps.jsonc config style - matches [MapChooser](https://github.com/SwiftlyS2-Plugins/MapChooser)

Example new config:
```jsonc
{
  "MapsConfig": {
    "Maps": [
      {
        "Name": "Mirage",
        "Id": "de_mirage",
        "CanBeVoted": true
      },
      {
        "Name": "Workshop Map",
        "Id": "3375020",
        "CanBeVoted": false
      },
      ...
    ]
  }
}
```

## Full summary:

- Fixed a server crash when clicking any in-game menu button — map voting was the
  common one, but every other menu could trigger it too.
- Fixed two team picks landing in the same instant both being applied, which could
  put a player on the wrong side or end the draft with lopsided teams.
- Map changes no longer load the wrong map when another plugin, an admin or the
  game's own end-of-match vote has already queued a next map.
- Map entries are one name and one ID instead of three fields. The ID may be an
  official map name, a workshop ID, or `ws:` plus a workshop ID, and omitting it
  falls back to the name. `IsWorkshopMap` is gone.
- Only the knife round's winning captain can decide the starting side, matching what
  `!stay` / `!switch` already enforced.
- `!mix_start` now starts the match itself instead of launching a knife round first.
- New `SkipKnifeRoundWhenPickingAndCaptainsDisabled` (off by default) starts the
  match straight after teams are assigned. Applies only with `DisableCaptains` and
  `SkipTeamPicking` both on, and gives up the whole-team starting-side vote.
- Changing map after a match now comes up in warmup, where `!ready` and RTV work
  again, instead of carrying the finished match's state across.
- `ChangeMapAfterMatch(map)` does the same for a consumer driving its own post-match
  flow, without players being dropped into warmup on the map about to be discarded.
  `ChangeMap()` is unchanged.
- The team-pick menu now opens on the round the pick phase restarts into, so captains
  pick once everyone has respawned on their new sides.
- Players who connect while map voting is running now get the vote menu.
- A disconnect no longer abandons its own cleanup half-way, so leavers are reliably
  removed from rosters, the ready list, open votes and pick menus.
- A map vote abandoned before it finishes no longer changes the map on its own.
- Map votes no longer offer the map currently being played, workshop maps included. A
  one-map pool still offers that map, because the alternative is an empty vote.
- `DisallowVotePreviousMaps` now also keeps played workshop maps out of later votes.
- `StartDeferredMapVote(seconds)` runs the usual ballot and menu without handing the
  match over to the voting phase; the winner is announced but not loaded, and the
  caller keeps the result. `EndDeferredMapVote()` closes a running one early.
- `SetServerWideVoteKick(true)` puts `!votekick` to every human on the server instead
  of one team, and lets it run in any state bar a map load or the post-match teardown.
  Off by default — vote kick is otherwise unchanged.
- A server-wide vote kick passes on the same threshold `!rtv` uses instead of requiring
  unanimity. Bots neither vote nor count towards it, and it closes early once the
  remaining ballots cannot reach the threshold.
- The ready-count centre HUD and the ready-status chat announcements now stop when the
  knife round starts, as they already did on every other way out of warmup.

## [1.11.7] - 2026-10-08

- Votes no longer hang when a player leaves mid-vote. Surrender, timeout and vote
  kick re-count who is still on the team and close as soon as the rest have decided
  it — a unanimous surrender used to sit open until the timer ran out, then announce
  itself as failed.
- A vote kick ends immediately when its target disconnects.
- The starting-side vote closes once the remaining winners have answered, and votes
  left behind by players who already left no longer count toward the result.
- Leaving during map voting now withdraws your vote; the next player to connect used
  to inherit it.
- The team pick menu is rebuilt when a player it lists disconnects, so captains can
  no longer click a name that is already gone. If nobody is left to pick, the knife
  round starts instead of stalling.
- Two players with the same name can no longer be picked as the same person, which
  left the other unpickable for the rest of the draft.
- Surrender vote progress now shows the correct total in replacement menus — it read
  100% one vote before the team had agreed.

## [1.11.6] - 2026-10-07

Must update to this version in order to avoid random crashes.

- Fixed a server crash on team changes — reconnects, spectator moves and the
  halftime side swap could all trigger it. The plugin was reading player names
  out of game memory that had already been torn down.
- Servers running with `DetailedLogging` enabled were the most exposed, but one
  path could crash with it off too.

## [1.11.5] - 2026-10-06

Scoreboard tag fixes. Worth updating if you use ready tags or captains.

- `[READY]` / `[NOT READY]` tags stay visible now — VIP, guild and rank tags from
  other plugins were overwriting them within seconds of being set.
- Players sitting on the team-select screen now get a `[NOT READY]` tag.
- Ready tags keep updating after a map vote, not just during warmup.
- `[Captain]` tags no longer linger into a live match.

## [1.11.4] - 2026-09-26

- Knife round no longer freezes the match when both remaining players stay alive
  (for example when someone runs the round timer down on purpose).
- Side pick menu now closes on match start even if the captain never picked a side.
- Smoother VoteKick handling when a vote fails.
