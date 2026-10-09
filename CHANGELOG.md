# Changelog

Notable changes to MixScrims, newest first.

<!-- Release notes are taken from the section matching PluginMetadata.Version in
     MixScrims/src/Main.cs, so every release needs a "## [x.y.z]" heading here. -->

## [1.12.0] - 2026-10-09

Must update to this version in order to avoid random crashes.

**API consumers must update (contract 2.8.0).** `CastMapVote`, `CastTimeoutVote`,
`CastSurrenderVote`, `CastVoteKickVote`, `PickPlayerForTeam`, `PickPlayerForTeamBySlot`
and `ChooseStartingSide` now take effect on the next game tick instead of immediately, so
a snapshot read on the line after one of these calls still returns the old state — read it
from the matching event instead. Call the API from the game thread too: a built-in
SwiftlyS2 menu click (`Core.MenusAPI`) arrives on a thread-pool thread.
See [Thread affinity](https://github.com/shmitzas/MixScrims/wiki/API-Integration#thread-affinity)
for the pattern and a code example. Contract 2.8.0 also adds flow-control members that let a
consumer hold the match at its current phase and drive the transitions itself, and set which
captain picks first instead of leaving it to the coin toss — see
[Flow control](https://github.com/shmitzas/MixScrims/wiki/API-Reference#flow-control).

- Fixed a server crash when clicking any in-game menu button — map voting was the
  common one, but side pick, team pick, surrender, timeout, vote kick and admin
  captain menus could all trigger it.
- Fixed two team picks made in the same instant both being applied. The draft could put a
  player on the wrong side, skip a captain's turn, or end with lopsided teams. The second
  pick is now dropped and logged.
- A consumer plugin can set which captain picks first for the next draft, instead of
  leaving it to the coin toss.
- Fixed any player being able to decide the starting side for everyone. The knife round's
  winning captain is now the only one whose `!stay` / `!switch` counts, matching what the
  chat commands already enforced.
- The match state now reports `Ended` when a match finishes, so a plugin can tell a
  completed match from one still in progress.
- A consumer plugin can hold MixScrims at its current phase and drive the transitions
  itself.
- `!mix_start` now starts the match itself instead of launching a knife round first.
- New `SkipKnifeRoundWhenPickingAndCaptainsDisabled` option (off by default) starts the
  match straight after teams are assigned, with no knife round. It only applies when
  `DisableCaptains` and `SkipTeamPicking` are both on; any other setup still plays the
  knife round. Turning it on also gives up the whole-team starting-side vote that
  normally follows the knife round, so starting sides are whatever team assignment
  produced and nothing re-decides them.
- Servers running a consumer plugin that drives the match flow no longer log
  `No players picked for CT team` after the teams were already locked in. The rosters
  are now sealed once per match, so a second attempt leaves them untouched instead of
  replacing them with whoever currently happens to be on each side.
- Players who connect while map voting is already running now get the vote menu and
  can vote.
- A disconnect no longer abandons its own cleanup half-way, so players who leave
  are reliably removed from rosters, the ready list, open votes and pick menus.
- Map vote reads now return a copy, so another plugin reading them while a vote is
  being counted cannot fail.
- A map vote abandoned before it finishes — because everyone left, an admin ran
  `!mix_reset`, or the map was changed — no longer changes the map on its own once
  the vote timer runs out.
- Picking captains automatically no longer logs an error every time. That error is
  now reserved for a captain who was actually named but turned out to be invalid.
- Players who reconnect after an abandoned map vote are no longer offered a vote
  slot that silently never opens.

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
