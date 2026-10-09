using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal List<IPlayer> playingCtPlayers { get; set; } = [];
    internal List<IPlayer> playingTPlayers { get; set; } = [];
    internal IPlayer? winnerCaptain { get; set; } = null;
    internal Dictionary<int, string> sideVotes { get; set; } = new();
    internal Team sideVoteWinnerTeam { get; set; } = Team.None;

    // Keeps CS2's own post-knife-round restart parked for the whole PickingStartingSide phase.
    internal CancellationTokenSource? startingSideRestartHoldTimer;
    internal CancellationTokenSource? startingSidePickTimeoutTimer;

    // The built-in Stay/Switch menu for the current phase, closed when the phase ends.
    internal IMenuAPI? sidePickMenu;

    // Matches the DisableCaptains vote window below; a captain who never picks must not leave
    // the parked restart (and the server) frozen indefinitely.
    private const int StartingSidePickTimeoutSeconds = 30;

    // Set while StartKnifeRound's own TerminateRound is in flight so
    // HandleRoundEndOnKnifeRound doesn't read it as "the knife round ended".
    internal bool pendingKnifeRoundStart = false;

    // Latched the moment a starting-side decision is committed for the current
    // PickingStartingSide phase. Both SwitchStartingSides and StayStartingSides defer
    // their work by 0.2s and neither leaves PickingStartingSide until StartMatch runs
    // inside that callback, so every entry point (!stay / !switch, the built-in menu,
    // ChooseStartingSide, the disconnect fallback, the DisableCaptains vote timer)
    // still passes its own guard during that window. Without this a second choice
    // re-runs the whole pipeline — and on Switch that swaps the teams straight back.
    internal bool startingSideCommitted = false;

    /// <summary>
    /// Initiates the knife round phase of the match.
    /// </summary>
    internal void StartKnifeRound()
    {
        // Captured before the state write below; by the decision point the state reads KnifeRound.
        var previousState = mixScrimsService.GetCurrentMatchState();

        mixScrimsService.SetMatchState(MatchState.KnifeRound);
        mixScrimsService.RaiseKnifeRoundStarted();
        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.knife_round"]);

        // Only the pick phase seals. A consumer running its own knife round earlier (captain
        // election before picking) would otherwise seal empty picked lists and latch
        // teamPickingFinalized, making the real CompleteTeamPicking a no-op that drops the picks.
        if (previousState == MatchState.PickingTeam)
            FinalizeTeamPicking();
        else if (cfg.DetailedLogging)
            logger.LogInformation("StartKnifeRound: entered from {State}, not the pick phase; leaving the pick rosters unsealed.", previousState);

        UnpauseMatch();

        // Symmetric to StartMatch: prime CCSGameRules limits before the restart below.
        // Defense in depth — the knife-round transition currently has zero pending team
        // changes, and Stay/Switch crash evidence (see StartMatch comment + repo memory
        // `mixscrims-mp-restartgame-team-limits-segv.md`) proved team-limit reconciliation
        // is NOT the actual crash class. Kept for consistency with StartMatch and to
        // survive any future refactor that adds team moves here.
        RelaxEngineTeamLimits("StartKnifeRound");

        // knife_round.cfg is cvars only now - the plugin drives the round transition, same
        // as StartMatch and StartTeamPickingPhase (repo memory
        // `mixscrims-mp-restartgame-team-limits-segv.md`). The 0.5s delay lets the preceding
        // UnpauseMatch's mp_pause 0 command drain before the exec.
        //   T+0.5s  exec knife_round.cfg (ends with mp_warmup_end)
        //   T+1.0s  TerminateRound(GameCommencing, 1.0f) -> RestartRound at T+2.0s
        var kCfgToken = Core.Scheduler.DelayBySeconds(0.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.KnifeRound)
            {
                logger.LogWarning("StartKnifeRound: state changed before cfg exec (now {State}); skipping knife_round.cfg.", mixScrimsService.GetCurrentMatchState());
                return;
            }

            if (Core.Engine is not { } knifeEngine)
            {
                logger.LogWarning("StartKnifeRound: Core.Engine unavailable; skipping knife_round.cfg.");
                return;
            }

            try
            {
                var gameRules = Core.EntitySystem.GetGameRules();
                if (gameRules is null || !gameRules.IsValid)
                {
                    logger.LogWarning("StartKnifeRound: game rules invalid before cfg exec; skipping knife_round.cfg.");
                    return;
                }

                knifeEngine.ExecuteCommand("exec mixscrims/knife_round.cfg");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "StartKnifeRound: exception dispatching knife_round.cfg exec");
                return;
            }

            // Deferred so the cfg's mp_warmup_end has landed - TerminateRound is a no-op
            // while WarmupPeriod is set.
            var kRestartToken = Core.Scheduler.DelayBySeconds(0.5f, () =>
            {
                if (mixScrimsService.GetCurrentMatchState() != MatchState.KnifeRound)
                {
                    logger.LogWarning("StartKnifeRound: state changed before TerminateRound (now {State}); skipping manual restart.", mixScrimsService.GetCurrentMatchState());
                    return;
                }

                // Armed before dispatch: TerminateRound fires round_end synchronously in
                // some paths, and HandleRoundEndOnKnifeRound would read it as the knife
                // round having been won.
                pendingKnifeRoundStart = true;
                if (!RestartRoundManually("StartKnifeRound", RoundEndReason.GameCommencing, 1.0f))
                {
                    pendingKnifeRoundStart = false;
                    logger.LogWarning("StartKnifeRound: restart did not dispatch; knife round starts on the current round.");
                }
            });
            Core.Scheduler.StopOnMapChange(kRestartToken);
        });
        Core.Scheduler.StopOnMapChange(kCfgToken);

        if (cfg.KickPlayersNotInMatch)
        {
            mixScrimsService.KickNotPlayingPlayers(Core.Localizer["info.kick_reason.not_picked"]);
        }
    }

    /// <summary>
    /// Prompts the winning team's captain to choose the starting side for the match.
    /// </summary>
    internal void PromptWinnerTCaptainoChoseStartingSide(Team winnerTeam)
    {
        mixScrimsService.SetMatchState(MatchState.PickingStartingSide);
        startingSideCommitted = false;
        BeginStartingSideRestartHold("PickingStartingSide");
        mixScrimsService.RaiseKnifeRoundWon(winnerTeam);

        // Captains may hold stale/disposed IPlayer references after reconnects or map changes.
        // Re-validate (and re-pick if needed) before accessing controller properties below.
        EnsureCaptainsAlive();

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptWinnerTCaptainoChoseStartingSide: Captains disabled, initiating team vote.");

            sideVotes.Clear();
            sideVoteWinnerTeam = winnerTeam;
            var winningTeamPlayers = winnerTeam == Team.CT ? playingCtPlayers : playingTPlayers;
            var teamName = winnerTeam == Team.CT ? "CT" : "T";

            PrintMessageToAllPlayers(Core.Localizer[$"announcement.knife_round.winner.{teamName.ToLower()}"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.team_vote_started"]);

            foreach (var player in winningTeamPlayers)
            {
                if (player != null && IsPlayerValid(player) && !IsBot(player))
                {
                    if (!suppressBuiltInMenus)
                        Core.MenusAPI.OpenMenuForPlayer(player, sidePickMenu ??= BuildSidePickingMenu());
                }
            }

            var sideVoteToken = Core.Scheduler.DelayBySeconds(30, () =>
            {
                if (mixScrimsService.GetCurrentMatchState() == MatchState.PickingStartingSide)
                {
                    ProcessTeamSideVotes();
                }
            });
            Core.Scheduler.StopOnMapChange(sideVoteToken);
            return;
        }

        var pickTimeoutToken = Core.Scheduler.DelayBySeconds(StartingSidePickTimeoutSeconds, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingStartingSide)
                return;

            logger.LogWarning("PromptWinnerTCaptainoChoseStartingSide: no pick after {Seconds}s; defaulting to Stay.", StartingSidePickTimeoutSeconds);
            StayStartingSides(winnerCaptain);
        });
        Core.Scheduler.StopOnMapChange(pickTimeoutToken);
        startingSidePickTimeoutTimer = pickTimeoutToken;

        if (winnerTeam == Team.CT)
        {
            if (captainCt == null || !IsPlayerValid(captainCt))
            {
                logger.LogError("PromptWinnerTCaptainoChoseStartingSide: CT Captain is null or invalid; defaulting to Stay.");
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.ct"]);
                StayStartingSides(null);
                return;
            }

            winnerCaptain = captainCt;
            {
                ulong sid; try { sid = captainCt.SteamID; } catch { sid = 0; }
                if (sid != 0)
                    mixScrimsService.RaisePickingStartingSideStarted(sid);
            }

            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.ct"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.ct", captainCt.Name]);

            // Bot captain: auto "Switch"
            if (IsBot(captainCt))
            {
                HandleCaptainSideChoice(captainCt, "Switch");
                return;
            }

            if (IsPlayerValid(captainCt) && !suppressBuiltInMenus)
            {
                Core.MenusAPI.OpenMenuForPlayer(captainCt, sidePickMenu ??= BuildSidePickingMenu());
            }
        }

        if (winnerTeam == Team.T)
        {
            if (captainT == null || !IsPlayerValid(captainT))
            {
                logger.LogError("PromptWinnerTCaptainoChoseStartingSide: T Captain is null or invalid; defaulting to Stay.");
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.t"]);
                StayStartingSides(null);
                return;
            }

            winnerCaptain = captainT;
            {
                ulong sid; try { sid = captainT.SteamID; } catch { sid = 0; }
                if (sid != 0)
                    mixScrimsService.RaisePickingStartingSideStarted(sid);
            }

            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.t"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.t", captainT.Name]);

            // Bot captain: auto "Switch"
            if (IsBot(captainT))
            {
                HandleCaptainSideChoice(captainT, "Switch");
                return;
            }

            if (IsPlayerValid(captainT) && !suppressBuiltInMenus)
            {
                Core.MenusAPI.OpenMenuForPlayer(captainT, sidePickMenu ??= BuildSidePickingMenu());
            }
        }
    }

    /// <summary>
    /// Builds and returns a menu that allows the user to choose between switching or staying on their current side.
    /// </summary>
    internal IMenuAPI BuildSidePickingMenu()
    {
        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.side_picking"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .DisableExit()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var switchBtn = new ButtonMenuOption("Switch");
        switchBtn.Click += async (sender, args) =>
        {
            HandleCaptainSideChoice(args.Player, "Switch");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(switchBtn);

        var stayBtn = new ButtonMenuOption("Stay");
        stayBtn.Click += async (sender, args) =>
        {
            HandleCaptainSideChoice(args.Player, "Stay");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(stayBtn);

        return builder.Build();
    }

    /// <summary>
    /// Handles the captain's choice regarding starting sides in the game.
    /// </summary>
    internal void HandleCaptainSideChoice(IPlayer captain, string choice)
    {
        if (captain == null)
        {
            logger.LogError("HandleCaptainSideChoice: Captain is null.");
            return;
        }

        // SwiftlyS2 dispatches built-in menu clicks through Task.Run, so callers reach us on a
        // thread-pool thread where every native read below is an uncatchable AV. PlayerID is a
        // plain managed field, so it is the only thing safe to carry across the hop.
        var slot = captain.PlayerID;
        Core.Scheduler.NextTick(() => HandleCaptainSideChoiceOnGameThread(slot, choice));
    }

    private void HandleCaptainSideChoiceOnGameThread(int slot, string choice)
    {
        var captain = Core.PlayerManager.GetPlayer(slot);
        if (captain is null || !IsPlayerValid(captain))
        {
            logger.LogWarning("HandleCaptainSideChoice: slot {Slot} left before its {Choice} side pick could be applied.", slot, choice);
            return;
        }

        CloseMenuForPlayer(captain);

        if (cfg.DisableCaptains)
        {
            var playerTeam = (captain.PlayerPawn?.TeamNum == 3) ? Team.CT : Team.T;
            if (sideVoteWinnerTeam != Team.None && playerTeam != sideVoteWinnerTeam)
            {
                PrintMessageToPlayer(captain, Core.Localizer["error.not_winner_team"]);
                return;
            }

            sideVotes[captain.PlayerID] = choice;
            PrintMessageToPlayer(captain, Core.Localizer["command.side_vote.recorded", choice]);

            TryCloseTeamSideVote(playerTeam);
            return;
        }

        // The !stay / !switch commands have always checked this; the built-in menu and the
        // ChooseStartingSide driver did not, so any connected player could decide the side
        // for everyone. It is also what lets SwitchStartingSides / StayStartingSides keep
        // deriving the winning side from the caller's own TeamNum.
        if (!IsSamePlayer(captain, winnerCaptain))
        {
            logger.LogWarning("HandleCaptainSideChoice: {Player} is not the winning captain; ignoring their {Choice}.", SafePlayerName(captain), choice);
            PrintMessageToPlayer(captain, Core.Localizer["error.not_captain"]);
            return;
        }

        if (string.Equals(choice, "Switch", StringComparison.OrdinalIgnoreCase))
        {
            SwitchStartingSides(captain);
            return;
        }
        if (string.Equals(choice, "Stay", StringComparison.OrdinalIgnoreCase))
        {
            StayStartingSides(captain);
            return;
        }

        logger.LogError("HandleCaptainSideChoice: Invalid choice made by captain.");
    }

    /// <summary>
    /// Closes the <c>DisableCaptains</c> side vote once every winning-team player still present
    /// has answered. <paramref name="fallbackTeam"/> is only consulted when
    /// <see cref="sideVoteWinnerTeam"/> was never set; <paramref name="leaver"/> is excluded up
    /// front because a disconnecting player still reads as valid while the event is handled.
    /// </summary>
    internal void TryCloseTeamSideVote(Team fallbackTeam = Team.None, IPlayer? leaver = null)
    {
        var votingTeam = sideVoteWinnerTeam != Team.None ? sideVoteWinnerTeam : fallbackTeam;
        if (votingTeam is not (Team.CT or Team.T)) return;

        var roster = votingTeam == Team.CT ? playingCtPlayers : playingTPlayers;
        // Two sets on purpose: membership decides whose vote survives, validity decides who we
        // are still waiting on. Purging on validity would discard a vote from a team member
        // whose pawn happens to read null at that instant.
        var onTeam = new HashSet<int>();
        var eligible = new HashSet<int>();
        foreach (var player in roster)
        {
            if (leaver != null && IsSamePlayer(player, leaver)) continue;
            var slot = SafePlayerId(player);
            if (slot < 0) continue;
            onTeam.Add(slot);
            if (!IsBot(player) && IsPlayerValid(player)) eligible.Add(slot);
        }

        // A vote from someone no longer on the team counts toward both the quorum below and
        // the Switch/Stay tally, so leaving it in lets a departed player close and decide the vote.
        foreach (var slot in sideVotes.Keys.Where(k => !onTeam.Contains(k)).ToList())
            sideVotes.Remove(slot);

        if (sideVotes.Count < eligible.Count) return;

        ProcessTeamSideVotes();
    }

    /// <summary>
    /// Processes team votes for side selection when captains are disabled.
    /// </summary>
    internal void ProcessTeamSideVotes()
    {
        var switchVotes = sideVotes.Values.Count(v => string.Equals(v, "Switch", StringComparison.OrdinalIgnoreCase));
        var stayVotes = sideVotes.Values.Count(v => string.Equals(v, "Stay", StringComparison.OrdinalIgnoreCase));

        if (cfg.DetailedLogging)
            logger.LogInformation("ProcessTeamSideVotes: Switch={SwitchVotes}, Stay={StayVotes}", switchVotes, stayVotes);

        PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.vote_results", switchVotes, stayVotes]);

        var firstVoter = GetPlayers().FirstOrDefault(p => sideVotes.ContainsKey(p.PlayerID));

        if (switchVotes > stayVotes)
        {
            SwitchStartingSides(firstVoter);
        }
        else
        {
            StayStartingSides(firstVoter);
        }

        sideVotes.Clear();
    }

    /// <summary>
    /// Switches the starting sides of the Counter-Terrorist and Terrorist teams, including their players and captains.
    /// </summary>
    internal void SwitchStartingSides(IPlayer? captain)
    {
        if (!TryCommitStartingSide(nameof(SwitchStartingSides))) return;

        // Whole body runs on the main game thread — native schema reads (captain.PlayerPawn, TeamNum, Controller) and StartMatch downstream are not safe on the menu Click dispatch thread.
        var switchSidesToken = Core.Scheduler.DelayBySeconds(0.2f, () =>
        {
            if (captain != null && captain.PlayerPawn == null)
            {
                logger.LogError("SwitchStartingSides: Captain PlayerPawn is null.");
                return;
            }

            if (captain?.PlayerPawn?.TeamNum == 3)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.ct", captain.Name]);
            }

            if (captain?.PlayerPawn?.TeamNum == 2)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.t", captain.Name]);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("SwitchStartingSides: Switching sides...");

            var oldCtCaptain = captainCt;
            var oldTCaptain = captainT;
            var oldPlayingCtPlayers = playingCtPlayers.ToList();
            var oldPlayingTPlayers = playingTPlayers.ToList();

            playingCtPlayers = oldPlayingTPlayers;
            playingTPlayers = oldPlayingCtPlayers;
            // Fire captain events via AssignCaptain — the CT slot now holds the old T
            // captain and vice versa. AssignCaptain emits CaptainRemoved then
            // CaptainAssigned per slot.
            AssignCaptain(Team.CT, oldTCaptain);
            AssignCaptain(Team.T, oldCtCaptain);

            // Team-name cvars only; the player moves themselves happen inside
            // StartMatch → MovePlayersToDesignatedTeamsPreMatch below. The older code
            // wrapped a second `ChangeTeamAsync` loop here inside NextWorldUpdate,
            // which the engine no-op'd (log shows no paired `ChangeTeam() CTMDBG`),
            // but the managed calls raced batch 1's still-in-flight pawn transitions
            // and are the strongest remaining suspect for the 50/50 Switch-only
            // crash (Stay path never fires this code, Stay never crashes). SetTeamName
            // itself internally schedules NextTick for its cvar exec, so no wrapper is
            // needed here.
            // Fire StartingSideChosen with the WINNING team's post-swap side. For Switch,
            // that's the opposite of the deciding captain's pre-swap side (captured before
            // this delayed callback ran). captain?.PlayerPawn.TeamNum was 3 (CT) or 2 (T)
            // pre-swap; the winning team ends up on the opposite side.
            var preSwapTeam = captain?.PlayerPawn?.TeamNum;
            if (preSwapTeam == 3)
                mixScrimsService.RaiseStartingSideChosen(Team.T);
            else if (preSwapTeam == 2)
                mixScrimsService.RaiseStartingSideChosen(Team.CT);

            SetTeamName(Team.CT, IsPlayerValid(captainCt) ? captainCt!.Name : null);
            SetTeamName(Team.T, IsPlayerValid(captainT) ? captainT!.Name : null);

            StartMatch();
        });
        Core.Scheduler.StopOnMapChange(switchSidesToken);
    }

    /// <summary>
    /// Keeps the teams on their starting sides based on the captain's current team.
    /// </summary>
    /// <remarks>
    /// The <c>StartingSideChosen</c> team is read off <paramref name="captain"/>, so every
    /// caller must pass a winning-side player — enforced in HandleCaptainSideChoiceOnGameThread
    /// and, for the <c>DisableCaptains</c> vote, by TryCloseTeamSideVote purging foreign votes.
    /// </remarks>
    internal void StayStartingSides(IPlayer? captain)
    {
        if (!TryCommitStartingSide(nameof(StayStartingSides))) return;

        if (IsPlayerValid(captain))
        {
            if (captain!.PlayerPawn?.TeamNum == 3)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.ct", captain.Name]);
                mixScrimsService.RaiseStartingSideChosen(Team.CT);
            }
            else if (captain.PlayerPawn?.TeamNum == 2)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.t", captain.Name]);
                mixScrimsService.RaiseStartingSideChosen(Team.T);
            }
        }

        // Defer StartMatch so native schema reads inside it run on the main game thread (mirrors SwitchStartingSides).
        var stayToken = Core.Scheduler.DelayBySeconds(0.2f, () => StartMatch());
        Core.Scheduler.StopOnMapChange(stayToken);
    }

    /// <summary>
    /// Single funnel for "the starting side is now decided". Returns false when a
    /// decision was already committed for this phase.
    /// </summary>
    private bool TryCommitStartingSide(string callSite)
    {
        if (startingSideCommitted)
        {
            logger.LogWarning("{Site}: starting side already decided this phase; ignoring duplicate.", callSite);
            return false;
        }
        startingSideCommitted = true;
        return true;
    }

    /// <summary>
    /// Takes ownership of CS2's pending round restart for the duration of the pick, so the phase
    /// never races the engine's <c>mp_round_restart_delay</c> timer. Re-asserted on a tick rather
    /// than written once: a <c>round_end</c> Pre hook can run before the engine writes
    /// <c>m_flRestartRoundTime</c>, and any other plugin can re-arm it mid-phase.
    /// </summary>
    internal void BeginStartingSideRestartHold(string callSite)
    {
        HoldPendingRoundRestart(callSite);

        startingSideRestartHoldTimer?.Cancel();
        startingSideRestartHoldTimer = Core.Scheduler.DelayAndRepeatBySeconds(0.5f, 0.5f, () =>
        {
            if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingStartingSide)
                return;

            HoldPendingRoundRestart("PickingStartingSideTick");
        });
        Core.Scheduler.StopOnMapChange(startingSideRestartHoldTimer);
    }

    /// <summary>
    /// Stops re-asserting the hold and drops the phase's auto-Stay timer. Does NOT hand the
    /// restart back — the caller decides whether this exit is the match start (which releases it
    /// deliberately) or an abort (which must).
    /// </summary>
    internal void EndStartingSideRestartHold()
    {
        startingSideRestartHoldTimer?.Cancel();
        startingSideRestartHoldTimer = null;
        startingSidePickTimeoutTimer?.Cancel();
        startingSidePickTimeoutTimer = null;
    }

    /// <summary>
    /// Closes the built-in Stay/Switch menu for every player still showing it. Main thread only.
    /// </summary>
    internal void CloseSidePickMenu()
    {
        if (sidePickMenu is not { } menu) return;
        sidePickMenu = null;
        try
        {
            Core.MenusAPI.CloseMenu(menu);
        }
        catch (Exception ex)
        {
            // Runs inside SetMatchState's phase exit; a failed close must not skip the restart release after it.
            logger.LogWarning(ex, "CloseSidePickMenu: failed to close the side-pick menu.");
        }
    }

    /// <summary>
    /// Assigns players to their designated teams before the match begins.
    /// </summary>
    internal void MovePlayersToDesignatedTeamsPreMatch()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch");
        
        isMovingPlayersToTeams = true;
        
        var players = GetPlayingPlayers();
        var playingPlayerIds = new HashSet<int>(playingCtPlayers.Select(p => p.PlayerID).Concat(playingTPlayers.Select(p => p.PlayerID)));
        players.RemoveAll(p => playingPlayerIds.Contains(p.PlayerID));

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Player is a bot, skipping move to SPEC");
                continue;
            }

            // ChangeTeam kills the player and recreates the pawn entity, which is expensive.
            // Skip the call if the player is already on Spectator to avoid unnecessary entity churn.
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.Spectator)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on SPEC, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to SPEC", player.Controller!.PlayerName);
            player.ChangeTeamAsync(Team.Spectator);
        }

        var playingCtPlayerIds = new HashSet<int>(playingCtPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!playingCtPlayerIds.Contains(player.PlayerID))
                continue;

            // Skip if the player is already on CT — ChangeTeam kills/respawns the pawn,
            // and doing this unnecessarily for every player on phase transitions causes
            // ~1s server-frame stalls (combined with mp_restartgame in match_start.cfg).
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.CT)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on CT, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to CT", player.Controller!.PlayerName);
            if (IsBot(player))
            {
                player.SwitchTeamAsync(Team.CT);
            }
            else if (currentTeam == (int)Team.CT || currentTeam == (int)Team.T)
            {
                // Side swap: SwitchTeam moves the player in place and keeps the pawn alive, which
                // is what the engine itself does at halftime. ChangeTeam kills and recreates each
                // pawn, so a 10-man swap queues ten destroy/create pairs that the match-start
                // restart can land in the middle of.
                player.SwitchTeamAsync(Team.CT);
            }
            else
            {
                // From Spectator/unassigned there is no pawn to move - only ChangeTeam spawns them.
                player.ChangeTeamAsync(Team.CT);
            }
        }
        

        var playingTPlayerIds = new HashSet<int>(playingTPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!playingTPlayerIds.Contains(player.PlayerID))
                continue;

            // Same reasoning as the CT loop above — avoid redundant pawn destroy/create.
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.T)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on T, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to T", player.Controller!.PlayerName);
            if (IsBot(player))
            {
                player.SwitchTeamAsync(Team.T);
            }
            else if (currentTeam == (int)Team.CT || currentTeam == (int)Team.T)
            {
                player.SwitchTeamAsync(Team.T);
            }
            else
            {
                player.ChangeTeamAsync(Team.T);
            }
        }

        isMovingPlayersToTeams = false;
    }
}
