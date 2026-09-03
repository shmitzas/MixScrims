using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using System.Numerics;

namespace MixScrims;

public partial class MixScrims
{
    internal List<IPlayer> pickedCtPlayers = [];
    internal List<IPlayer> pickedTPlayers = [];
    internal IPlayer? captainCt { get; set; }
    internal IPlayer? captainT { get; set; }
    // Snapshot fields for IMixScrims consumers (v2.0.0+). Reset in StartTeamPickingPhase
    // and cleared when the phase ends or the plugin resets.
    internal Team? activePickingTeam = null;
    internal int currentPickIndex = 0;

    /// <summary>
    /// Initiates the team-picking phase of the match, assigning captains to teams and prompting the first captain to
    /// pick a player.
    /// </summary>
    internal void StartTeamPickingPhase()
    {
        StopPreMatchAnnouncementTimers();

        RemoveReadyClanTagsFromAllPlayers();

        // Clear captains whose IPlayer reference is now disposed (e.g. set before the
        // map change and the player has reconnected as a new IPlayer instance).
        EnsureCaptainsAlive();

        if (!cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Current captains - CT: {CT}, T: {T}", captainCt?.Name ?? "null", captainT?.Name ?? "null");

            PickCaptains();

            if (captainCt == null || captainT == null)
            {
                logger.LogError("StartTeamPickingPhase: One or both captains are null.");
                logger.LogError("captainCt: {Name}", captainCt != null ? captainCt.Name ?? "(no name)" : "null");
                logger.LogError("captainT: {Name}", captainT != null ? captainT.Name ?? "(no name)" : "null");
                logger.LogError("StartTeamPickingPhase: Valid players in the server: {Count}", GetPlayers().Count);
                logger.LogError("StartTeamPickingPhase: Aborting team picking phase.");
                PrintMessageToAllPlayers(Core.Localizer["error.captain.selection_failed"]);
                ResetPluginState();
                return;
            }

            if (cfg.DetailedLogging)
            {
                // Cache captain SteamIDs once and use SafeSteamId on roster entries; picked lists
                // can hold disposed IPlayer refs and a raw .SteamID read on those throws.
                var captainCtIdLog = SafeSteamId(captainCt);
                var captainTIdLog = SafeSteamId(captainT);
                logger.LogInformation("StartTeamPickingPhase: Before validation - pickedCt: {CtCount}, pickedT: {TCount}", pickedCtPlayers.Count, pickedTPlayers.Count);
                logger.LogInformation("StartTeamPickingPhase: CT captain in picked list: {InList}", captainCtIdLog != 0 && pickedCtPlayers.Any(p => SafeSteamId(p) == captainCtIdLog));
                logger.LogInformation("StartTeamPickingPhase: T captain in picked list: {InList}", captainTIdLog != 0 && pickedTPlayers.Any(p => SafeSteamId(p) == captainTIdLog));
            }

            // Ensure captains are in picked lists (handles captains set during Warmup state)
            if (captainCt != null && IsPlayerValid(captainCt))
            {
                var captainCtId = captainCt.SteamID;
                if (!pickedCtPlayers.Any(p => SafeSteamId(p) == captainCtId))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("StartTeamPickingPhase: Adding CT Captain {PlayerName} to pickedCtPlayers.", captainCt.Controller.PlayerName);
                    pickedCtPlayers.Add(captainCt);
                }
            }

            if (captainT != null && IsPlayerValid(captainT))
            {
                var captainTId = captainT.SteamID;
                if (!pickedTPlayers.Any(p => SafeSteamId(p) == captainTId))
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("StartTeamPickingPhase: Adding T Captain {PlayerName} to pickedTPlayers.", captainT.Controller.PlayerName);
                    pickedTPlayers.Add(captainT);
                }
            }

            if (cfg.DetailedLogging)
            {
                logger.LogInformation("StartTeamPickingPhase: After validation - pickedCt: {CtCount}, pickedT: {TCount}", pickedCtPlayers.Count, pickedTPlayers.Count);
            }
        }

        if (cfg.SkipTeamPicking)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Team picking is disabled in configuration.");
            SkipTeamPickingPhase();
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartTeamPickingPhase: Captains is disabled in configuration, auto-assigning teams based on current positions.");
            SkipTeamPickingPhase();
            return;
        }

        mixScrimsService.SetMatchState(MatchState.PickingTeam);        

        PauseMatch();
        if (Core.Engine is { } pickEngine)
            pickEngine.ExecuteCommand("exec mixscrims/teampick.cfg");
        else
            logger.LogWarning("StartTeamPickingPhase: Core.Engine unavailable; skipping teampick.cfg.");

        MovePlayersToDesignatedTeamsPrePick();

        // Primed before the restart below so CCSGameRules::Think never observes a stale
        // limit while the players just moved onto their picked sides are reconciled.
        // Symmetric to StartMatch / StartKnifeRound.
        RelaxEngineTeamLimits("StartTeamPickingPhase");

        // teampick.cfg no longer ends with `mp_restartgame 1` - the plugin drives the round
        // transition itself, same as StartMatch (see repo memory
        // `mixscrims-mp-restartgame-team-limits-segv.md`). The pause is lifted first so the
        // queued restart isn't held by it, and HandleRoundPrestartPreKnifeRound re-applies
        // the pause on the round that lands.
        //   T+1.5s  UnpauseMatch  (also lets teampick.cfg's mp_warmup_end settle first -
        //                          TerminateRound is a no-op while WarmupPeriod is set)
        //   T+2.0s  TerminateRound(GameCommencing, 1.0f) -> RestartRound at T+3.0s
        // Both callbacks bail if the phase already ended (bot captains auto-pick, so the
        // whole ladder can complete and hand off to StartKnifeRound within a tick).
        // If the restart does not dispatch, the pause is re-applied immediately - otherwise
        // the unpause above is never compensated and the pick phase runs live.
        var pickUnpauseToken = Core.Scheduler.DelayBySeconds(1.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingTeam)
                return;
            UnpauseMatch();
        });
        Core.Scheduler.StopOnMapChange(pickUnpauseToken);

        var pickRestartToken = Core.Scheduler.DelayBySeconds(2f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingTeam)
            {
                logger.LogWarning("StartTeamPickingPhase: state changed before TerminateRound (now {State}); skipping manual restart.", mixScrimsService.GetCurrentMatchState());
                return;
            }
            if (!RestartRoundManually("StartTeamPickingPhase", RoundEndReason.GameCommencing, 1.0f))
            {
                logger.LogWarning("StartTeamPickingPhase: restart did not dispatch; re-pausing so the pick phase stays frozen.");
                PauseMatch();
            }
        });
        Core.Scheduler.StopOnMapChange(pickRestartToken);

        SetTeamName(Team.CT, captainCt == null ? null : captainCt.Controller.PlayerName);
        SetTeamName(Team.T, captainT == null ? null :  captainT.Controller.PlayerName);

        // Reset per-phase snapshot fields. currentPickIndex counts captains' implicit
        // self-picks as 1 and 2 — increment now. Raised for bot captains too so the
        // pick-index sequence a consumer observes has no gaps.
        currentPickIndex = 0;
        if (captainCt != null && IsPlayerValid(captainCt))
        {
            currentPickIndex++;
            mixScrimsService.RaisePlayerPickedForTeam(Team.CT, SafeSteamId(captainCt), currentPickIndex);
        }
        if (captainT != null && IsPlayerValid(captainT))
        {
            currentPickIndex++;
            mixScrimsService.RaisePlayerPickedForTeam(Team.T, SafeSteamId(captainT), currentPickIndex);
        }

        int teamStarting = Random.Shared.Next(2, 4);
        var startingTeam = teamStarting == 3 ? Team.CT : Team.T;
        activePickingTeam = startingTeam;
        mixScrimsService.RaiseTeamPickingStarted(startingTeam);
        if (teamStarting == 3)
        {
            PromptCaptainToPickPlayer(captainCt, Team.CT);
            return;
        }
        if (teamStarting == 2)
        {
            PromptCaptainToPickPlayer(captainT, Team.T);
            return;
        }
    }

    /// <summary>
    /// Skips the team picking phase and automatically assigns players to teams based on their current state and
    /// configuration settings.
    /// </summary>
    internal void SkipTeamPickingPhase()
    {
        mixScrimsService.SetMatchState(MatchState.PickingTeam);

        if (Core.Engine is { } skipPickEngine)
            skipPickEngine.ExecuteCommand("exec mixscrims/teampick.cfg");
        else
            logger.LogWarning("SkipTeamPickingPhase: Core.Engine unavailable; skipping teampick.cfg.");
        PauseMatch();

        var players = GetPlayingPlayers();

        if (!cfg.DisableCaptains)
        {
            if (captainCt != null && captainCt.IsValid)
            {
                // Cache captain SteamID once and use SafeSteamId on roster entries to keep the
                // RemoveAll safe against disposed IPlayer refs surfaced by GetPlayingPlayers.
                var captainCtId = captainCt.SteamID;
                players.RemoveAll(p => SafeSteamId(p) == captainCtId);
                playingCtPlayers.Add(captainCt);
            }

            if (captainT != null && captainT.IsValid)
            {
                var captainTId = captainT.SteamID;
                players.RemoveAll(p => SafeSteamId(p) == captainTId);
                playingTPlayers.Add(captainT);
            }
        }

        foreach (var player in players)
        {
            if (player != null
                && player.IsValid
                && player.PlayerPawn != null)
            {
                // Cache the live loop-var SteamID once; playingCt/TPlayers may hold disposed
                // refs so predicate reads use SafeSteamId.
                var playerId = player.SteamID;
                if ((Team)player.PlayerPawn.TeamNum == Team.T && !playingTPlayers.Any(p => SafeSteamId(p) == playerId))
                {
                    if (cfg.MoveOverflowPlayersToSpec)
                    {
                        if (playingTPlayers.Count >= cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("SkipTeamPickingPhase: Disregarding overflow T player {PlayerName}", player.Name);
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation("SkipTeamPickingPhase: Adding {PlayerName} to T picked players", player.Name);
                    playingTPlayers.Add(player);
                }

                if ((Team)player.PlayerPawn.TeamNum == Team.CT && !playingCtPlayers.Any(p => SafeSteamId(p) == playerId))
                {
                    if (cfg.MoveOverflowPlayersToSpec)
                    {
                        if (playingCtPlayers.Count >= cfg.MinimumReadyPlayers / 2)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogInformation("SkipTeamPickingPhase: Disregarding overflow CT player {PlayerName}", player.Name);
                            continue;
                        }
                    }

                    if (cfg.DetailedLogging)
                        logger.LogInformation("SkipTeamPickingPhase: Adding {PlayerName} to CT picked players", player.Name);
                    playingCtPlayers.Add(player);
                }
            }
        }

        MovePlayersToDesignatedTeamsPreMatch();
        if (!cfg.DisableCaptains && captainCt != null && captainT != null)
        {
            SetTeamName(Team.CT, captainCt.Name);
            SetTeamName(Team.T, captainT.Name);
        }
        StartKnifeRound();
    }

    /// <summary>
    /// Prompts the specified team captain to select a player for their team.
    /// </summary>
    internal void PromptCaptainToPickPlayer(IPlayer? captain, Team team)
    {
        if (captain == null)
        {
            logger.LogError("PromptCaptainToPickPlayer: Captain is null.");
            return;
        }

        // Track whose turn to pick is active so IMixScrims.GetActivePickingTeam reflects it.
        activePickingTeam = team;

        var players = GetPlayers();
        // Slot-keyed, NOT SteamID-keyed: every bot reports SteamID 0, so a SteamID compare
        // wipes the whole bot pool the moment a captain is a bot or a bot lands in either
        // picked list — which empties the pool in a TestMode lobby and skips the phase
        // entirely. Same filter as MixScrimsService.GetUnpickedPlayerSlots, so the built-in
        // menu and consumer-built menus agree on who is pickable.
        var captainSlot = SafePlayerId(captain);
        var pickedSlots = new HashSet<int>();
        foreach (var picked in pickedCtPlayers.Concat(pickedTPlayers))
        {
            var slot = SafePlayerId(picked);
            if (slot >= 0) pickedSlots.Add(slot);
        }
        players.RemoveAll(p =>
        {
            var slot = SafePlayerId(p);
            return slot < 0 || slot == captainSlot || pickedSlots.Contains(slot);
        });

        if (players.Count == 0)
        {
            logger.LogWarning("PromptCaptainToPickPlayer: No players available to pick.");
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        if (team == Team.CT)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.ct", captain.Name]);
        }

        if (team == Team.T)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.turn_to_pick.t", captain.Name]);
        }

        // Bot: auto-pick random player
        if (IsBot(captain))
        {
            var randomIndex = Random.Shared.Next(players.Count);
            var selectedPlayer = players[randomIndex];
            var selectedPlayerName = selectedPlayer.Name;
            logger.LogInformation("PromptCaptainToPickPlayer: {Team} captain {CaptainName} is a bot; auto-picking {PlayerName}.",
                team == Team.CT ? "CT" : "T", captain.Name, selectedPlayerName);
            if (team == Team.CT)
            {
                AssignPickedPlayerToTeamCt(captain, selectedPlayerName);
            }
            else
            {
                AssignPickedPlayerToTeamT(captain, selectedPlayerName);
            }
            return;
        }

        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.team_picking", team == Team.CT ? "CT" : "T"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .DisableExit()
            .SetAutoCloseDelay(0);

        foreach (var player in players)
        {
            var displayName = player.Name ?? $"#{player.PlayerID}";
            var button = new ButtonMenuOption(displayName);
            if (team == Team.CT)
            {
                button.Click += async (sender, args) =>
                {
                    AssignPickedPlayerToTeamCt(captain, displayName);
                };
            }
            else
            {
                button.Click += async (sender, args) =>
                {
                    AssignPickedPlayerToTeamT(captain, displayName);
                };
            }
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptCaptainToPickPlayer: Added option {PlayerName} for {CaptainName} ({Team})", displayName, captain.Name, team == Team.CT ? "CT" : "T");
            builder.AddOption(button);
        }

        var menu = builder.Build();
        if (IsPlayerValid(captain) && !suppressBuiltInMenus)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptCaptainToPickPlayer: Displaying picking menu to {CaptainName} for team {Team}", captain.Name, team == Team.CT ? "CT" : "T");
            Core.MenusAPI.OpenMenuForPlayer(captain, menu);
        }
    }

    /// <summary>
    /// Assigns captains for the teams if they have not already been selected.
    /// </summary>
    internal void PickCaptains()
    {
        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: Captains are disabled in configuration.");
            return;
        }

        if (captainCt == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: CT captain is null, picking now.");
            PickCtCaptain(null);
        }
        if (captainT == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PickCaptains: T captain is null, picking now.");
            PickTCaptain(null);
        }

    }

    /// <summary>
    /// Assigns a Counter-Terrorist team captain.
    /// </summary>
    internal void PickCtCaptain(IPlayer? player)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        // Slot, not SteamID: bots all share SteamID 0. -1 means "no prior captain",
        // which always announces.
        var previousCtSlot = captainCt != null ? SafePlayerId(captainCt) : -1;
        if (captainCt != null)
        {
            // Cache the outgoing captain's SteamID once and use SafeSteamId on roster entries;
            // captainCt itself and the roster items can both be disposed here (stale ref from
            // before a map change / reconnect). Skip the containment check when the outgoing
            // captain has no readable SteamID - .Remove(captainCt) below still works by
            // reference identity, but the .Any check would otherwise match every disposed
            // ghost that shares SafeSteamId == 0.
            var outgoingCtId = SafeSteamId(captainCt);
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                if (outgoingCtId != 0 && pickedCtPlayers.Any(p => SafeSteamId(p) == outgoingCtId))
                {
                    pickedCtPlayers.Remove(captainCt);
                }
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (outgoingCtId != 0 && playingCtPlayers.Any(p => SafeSteamId(p) == outgoingCtId))
                {
                    playingCtPlayers.Remove(captainCt);
                }
            }
            // Outgoing captain: strip the [Captain CT] prefix so the tag doesn't linger on
            // the player being replaced (admin re-pick / volunteer swap).
            RemoveCaptainClanTagFromPlayer(captainCt);
        }

        AssignCaptain(Team.CT, player);

        if (captainCt == null || !IsPlayerValid(captainCt))
        {
            logger.LogError("PickCtCaptain: player is invalid, picking random captain for CT team.");
            AssignCaptain(Team.CT, PickRandomCaptain(Team.CT));
        }

        if (captainCt != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                pickedCtPlayers.Add(captainCt);
            }
            if (matchState == MatchState.KnifeRound)
            {
                var newCtId = captainCt.SteamID;
                if (!playingCtPlayers.Any(p => SafeSteamId(p) == newCtId))
                    playingCtPlayers.Add(captainCt);
            }

            SetCaptainClanTag(captainCt, Team.CT);

            if (cfg.DetailedLogging)
                logger.LogInformation("PickCtCaptain: picked {PlayerName}", captainCt.Name);
            // Idempotent re-pick (repeated confirm click, EnsureCaptainsAlive re-run):
            // the roster churn above is harmless but the announcement would be a duplicate.
            if (SafePlayerId(captainCt) != previousCtSlot)
                PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.ct", captainCt.Name]);
        }
        else
        {
            logger.LogError("PickCtCaptain: Failed to pick a CT captain.");
        }
    }

    /// <summary>
    /// Assigns a Counter-Terrorist team captain.
    /// </summary>
    internal void PickTCaptain(IPlayer? player)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        // Slot, not SteamID: bots all share SteamID 0. -1 means "no prior captain",
        // which always announces.
        var previousTSlot = captainT != null ? SafePlayerId(captainT) : -1;
        if (captainT != null)
        {
            // Cache outgoing captain's SteamID once and use SafeSteamId on roster entries; same
            // disposed-ref safety as PickCtCaptain. Skip the containment check when the outgoing
            // captain has no readable SteamID so the .Any doesn't falsely match disposed ghosts.
            var outgoingTId = SafeSteamId(captainT);
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                if (outgoingTId != 0 && pickedTPlayers.Any(p => SafeSteamId(p) == outgoingTId))
                {
                    pickedTPlayers.Remove(captainT);
                }
            }
            if (matchState == MatchState.KnifeRound)
            {
                if (outgoingTId != 0 && playingTPlayers.Any(p => SafeSteamId(p) == outgoingTId))
                {
                    playingTPlayers.Remove(captainT);
                }
            }
            // Outgoing captain: strip the [Captain T] prefix so the tag doesn't linger on
            // the player being replaced (admin re-pick / volunteer swap).
            RemoveCaptainClanTagFromPlayer(captainT);
        }

        AssignCaptain(Team.T, player);

        if (captainT == null || !IsPlayerValid(captainT))
        {
            logger.LogError("PickTCaptain: player is invalid, picking random captain for T team.");
            AssignCaptain(Team.T, PickRandomCaptain(Team.T));
        }

        if (captainT != null)
        {
            if (matchState == MatchState.PickingTeam || matchState == MatchState.MapChosen)
            {
                pickedTPlayers.Add(captainT);
            }
            if (matchState == MatchState.KnifeRound)
            {
                var newTId = captainT.SteamID;
                if (!playingTPlayers.Any(p => SafeSteamId(p) == newTId))
                    playingTPlayers.Add(captainT);
            }

            SetCaptainClanTag(captainT, Team.T);

            if (cfg.DetailedLogging)
                logger.LogInformation("PickTCaptain: picked {PlayerName}", captainT.Name);
            // Idempotent re-pick (repeated confirm click, EnsureCaptainsAlive re-run):
            // the roster churn above is harmless but the announcement would be a duplicate.
            if (SafePlayerId(captainT) != previousTSlot)
                PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.captain.t", captainT.Name]);
        }
        else
        {
            logger.LogError("PickTCaptain: Failed to pick a T captain.");
        }

    }

    /// <summary>
    /// Selects a random player to serve as a captain from the list of currently playing players.
    /// </summary>
    internal IPlayer? PickRandomCaptain(Team? team = null)
    {
        // Exclude the current captains from every draw. Cache each captain's SteamID once
        // and use SafeSteamId on candidate predicates to stay safe against disposed refs
        // (captainCt/captainT can be stale from a prior round/map).
        var excludeCtId = captainCt != null ? SafeSteamId(captainCt) : 0;
        var excludeTId = captainT != null ? SafeSteamId(captainT) : 0;

        bool IsDrawable(IPlayer p)
        {
            if (!IsPlayerValid(p)) return false;
            var sid = SafeSteamId(p);
            return (excludeCtId == 0 || sid != excludeCtId)
                && (excludeTId == 0 || sid != excludeTId);
        }

        var everyone = GetPlayers().Where(IsDrawable).ToList();

        var players = team != null
            ? GetPlayersInTeam(team.Value).Where(IsDrawable).ToList()
            : new List<IPlayer>();

        // Map-vote flow lands every player in Spectator after the new map loads, so
        // GetPlayersInTeam(CT/T) is empty during the MapChosen ready burst. Fall back to
        // all valid players so a captain can still be drawn instead of returning null
        // (which would abort StartTeamPickingPhase and reset back to Warmup).
        if (players.Count == 0)
        {
            players = everyone;
        }

        // Prefer humans, and widen past the team pool to find one. warmup.cfg sets
        // sv_human_autojoin_team 1 (Spectators), so in a TestMode staging lobby the humans
        // sit in spec while bots occupy CT/T - a team-scoped draw is then bot-only even
        // though a human is connected, and handing BOTH captaincies to bots makes the pick
        // ladder auto-resolve through the IsBot branch without ever prompting anyone.
        var humans = players.Where(p => !IsBot(p)).ToList();
        if (humans.Count == 0)
            humans = everyone.Where(p => !IsBot(p)).ToList();

        var pool = humans.Count > 0 ? humans : players;
        if (pool.Count == 0)
        {
            logger.LogWarning("PickRandomCaptain: No players available to pick a captain.");
            return null;
        }

        var captainIndex = Random.Shared.Next(pool.Count);
        return pool[captainIndex];
    }

    /// <summary>
    /// Assigns the player selected by the CT captain to the CT team.
    /// </summary>
    internal void AssignPickedPlayerToTeamCt(IPlayer captain, string pickedPlayerName)
    {
        CloseMenuForPlayer(captain);
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamCt: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptCaptainToPickPlayer(captain, Team.CT);
            return;
        }

        pickedCtPlayers.Add(player);
        currentPickIndex++;
        // Raised for bots too (SteamID 0). The roster is already updated above, so a
        // consumer re-reading GetUnpickedPlayerSlots() from this event sees the pool
        // without the pick. Suppressing it for bots left consumer-built pick menus
        // showing already-picked players for the whole phase.
        mixScrimsService.RaisePlayerPickedForTeam(Team.CT, SafeSteamId(player), currentPickIndex);

        if (IsBot(player))
            player.SwitchTeamAsync(Team.CT);
        else
            player.ChangeTeamAsync(Team.CT);

        if (cfg.DetailedLogging)
            logger.LogInformation("AssignPickedPlayerToTeamCt: {CaptainName} picked {PlayerName} for CT team.", captain.Name, player.Name);
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.ct", captain.Name, player.Name]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            activePickingTeam = null;
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        PromptCaptainToPickPlayer(captainT, Team.T);
    }

    /// <summary>
    /// Assigns the player selected by the T captain to the T team.
    /// </summary>
    internal void AssignPickedPlayerToTeamT(IPlayer captain, string pickedPlayerName)
    {
        CloseMenuForPlayer(captain);
        var player = GetPlayerByName(pickedPlayerName);

        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("AssignPickedPlayerToTeamT: picked player is invalid");
            PrintMessageToPlayer(captain, Core.Localizer["error.invalid_player_picked", pickedPlayerName]);
            PromptCaptainToPickPlayer(captain, Team.T);
            return;
        }

        pickedTPlayers.Add(player);
        currentPickIndex++;
        // Raised for bots too — see the CT twin above.
        mixScrimsService.RaisePlayerPickedForTeam(Team.T, SafeSteamId(player), currentPickIndex);

        if (IsBot(player))
            player.SwitchTeamAsync(Team.T);
        else
            player.ChangeTeamAsync(Team.T);

        var currentMenu = Core.MenusAPI.GetCurrentMenu(captain);
        if (currentMenu != null)
        {
            Core.MenusAPI.CloseMenuForPlayer(captain, currentMenu);
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("AssignPickedPlayerToTeamT: {CaptainName} picked {PlayerName} for T team.", captain.Name, player.Name);
        PrintMessageToAllPlayers(Core.Localizer["announcement.team_picking.picked.member.t", captain.Name, player.Name]);

        if (pickedCtPlayers.Count + pickedTPlayers.Count >= cfg.MinimumReadyPlayers)
        {
            activePickingTeam = null;
            Core.Scheduler.NextTick(() => StartKnifeRound());
            return;
        }

        PromptCaptainToPickPlayer(captainCt, Team.CT);
    }

    /// <summary>
    /// Moves players to their designated teams before the picking phase begins.
    /// </summary>
    internal void MovePlayersToDesignatedTeamsPrePick()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("MovePlayersToDesignatedTeamsPrePick");

        var players = GetPlayingPlayers();
        var pickedPlayerIds = new HashSet<int>(pickedCtPlayers.Select(p => p.PlayerID).Concat(pickedTPlayers.Select(p => p.PlayerID)));
        players.RemoveAll(p => pickedPlayerIds.Contains(p.PlayerID));

        if (!cfg.MovePlayersToSpecDuringTeamPicking)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("MovePlayersToDesignatedTeamsPrePick: Moving players to spec during team picking is disabled in configuration.");
            return;
        }

        isMovingPlayersToTeams = true;

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Player is a bot, skipping move to SPEC");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to SPEC", player.Name);
            player.ChangeTeamAsync(Team.Spectator);
        }

        var pickedCtPlayerIds = new HashSet<int>(pickedCtPlayers.Select(p => p.PlayerID));
        // GetPlayers(), not GetPlayingPlayers(): a captain drawn while sitting in Spectator
        // (the TestMode staging default) is on neither CT nor T yet and would never be moved.
        foreach (var player in GetPlayers())
        {
            if (!pickedCtPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to CT", player.Name);
            if (IsBot(player))
                player.SwitchTeamAsync(Team.CT);
            else
                player.ChangeTeamAsync(Team.CT);
        }

        var pickedTPlayerIds = new HashSet<int>(pickedTPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayers())
        {
            if (!pickedTPlayerIds.Contains(player.PlayerID))
                continue;

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to T", player.Name);
            if (IsBot(player))
                player.SwitchTeamAsync(Team.T);
            else
                player.ChangeTeamAsync(Team.T);
        }

        isMovingPlayersToTeams = false;
    }
}
