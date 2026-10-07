# Changelog

Notable changes to MixScrims, newest first.

<!-- Release notes are taken from the section matching PluginMetadata.Version in
     MixScrims/src/Main.cs, so every release needs a "## [x.y.z]" heading here. -->

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
