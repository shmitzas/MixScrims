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
        mixScrimsService.SetMatchState(MatchState.KnifeRound);
        mixScrimsService.RaiseKnifeRoundStarted();
        // Team picking is over — clear the per-phase snapshot fields so IMixScrims
        // consumers reading GetActivePickingTeam / GetCurrentPickIndex don't see stale
        // values during the knife round.
        activePickingTeam = null;
        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.knife_round"]);

        // Drop any stale (disposed) captain references that survived a reconnect/map change
        // before we use them below to seed playingCtPlayers/playingTPlayers.
        EnsureCaptainsAlive();

        if (pickedCtPlayers.Count == 0)
        {
            logger.LogWarning("StartKnifeRound: No players picked for CT team. Setting current CT players as playingCtPlayers");
            var currentCtPlayers = GetPlayersInTeam(Team.CT);
            playingCtPlayers = currentCtPlayers.ToList();
        }
        else
        {
            playingCtPlayers = pickedCtPlayers.ToList();
            pickedCtPlayers.Clear();
        }

        if (pickedTPlayers.Count == 0)
        {
            logger.LogWarning("StartKnifeRound: No players picked for T team. Setting current T players as playingTPlayers");
            var currentTPlayers = GetPlayersInTeam(Team.T);
            playingTPlayers = currentTPlayers.ToList();
        }
        else
        {
            playingTPlayers = pickedTPlayers.ToList();
            pickedTPlayers.Clear();
        }

        if (captainCt != null && IsPlayerValid(captainCt))
        {
            // Cache captain SteamID once; playingCtPlayers can carry disposed IPlayer refs so
            // predicate reads use SafeSteamId to avoid ObjectDisposedException per iteration.
            var captainCtId = captainCt.SteamID;
            if (!playingCtPlayers.Any(p => SafeSteamId(p) == captainCtId))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Adding manually-set CT Captain {PlayerName} to playingCtPlayers.", captainCt.Controller.PlayerName);
                playingCtPlayers.Add(captainCt);
            }
        }

        if (captainT != null && IsPlayerValid(captainT))
        {
            var captainTId = captainT.SteamID;
            if (!playingTPlayers.Any(p => SafeSteamId(p) == captainTId))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Adding manually-set T Captain {PlayerName} to playingTPlayers.", captainT.Controller.PlayerName);
                playingTPlayers.Add(captainT);
            }
        }

        if (captainCt == null && playingCtPlayers.Count > 0)
        {
            AssignCaptain(Team.CT, playingCtPlayers[0]);
            if (cfg.DetailedLogging)
                logger.LogInformation("StartKnifeRound: CT Captain not set, assigning {PlayerName} as CT Captain.", captainCt!.Controller.PlayerName);
        }

        if (captainT == null && playingTPlayers.Count > 0)
        {
            AssignCaptain(Team.T, playingTPlayers[0]);
            if (cfg.DetailedLogging)
                logger.LogInformation("StartKnifeRound: T Captain not set, assigning {PlayerName} as T Captain.", captainT!.Controller.PlayerName);
        }

        readyPlayers.Clear();

        StopPreMatchAnnouncementTimers();

        if (cfg.ShowReadyStatusInScoreboard)
            RemoveReadyClanTagsFromAllPlayers();

        // Close any open team picking menus for captains
        if (captainCt != null && IsPlayerValid(captainCt))
        {
            var ctMenu = Core.MenusAPI.GetCurrentMenu(captainCt);
            if (ctMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(captainCt, ctMenu);
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Closed open menu for CT captain {PlayerName}", captainCt.Controller.PlayerName);
            }
        }

        if (captainT != null && IsPlayerValid(captainT))
        {
            var tMenu = Core.MenusAPI.GetCurrentMenu(captainT);
            if (tMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(captainT, tMenu);
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Closed open menu for T captain {PlayerName}", captainT.Controller.PlayerName);
            }
        }

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
                    var menu = BuildSidePickingMenu();
                    if (!suppressBuiltInMenus)
                        Core.MenusAPI.OpenMenuForPlayer(player, menu);
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
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.ct", captainCt.Controller.PlayerName]);

            // Bot captain: auto "Switch"
            if (IsBot(captainCt))
            {
                HandleCaptainSideChoice(captainCt, "Switch");
                return;
            }

            var menu = BuildSidePickingMenu();
            if (IsPlayerValid(captainCt) && !suppressBuiltInMenus)
            {
                Core.MenusAPI.OpenMenuForPlayer(captainCt, menu);
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
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.t", captainT.Controller.PlayerName]);

            // Bot captain: auto "Switch"
            if (IsBot(captainT))
            {
                HandleCaptainSideChoice(captainT, "Switch");
                return;
            }

            var menu = BuildSidePickingMenu();
            if (IsPlayerValid(captainT) && !suppressBuiltInMenus)
            {
                Core.MenusAPI.OpenMenuForPlayer(captainT, menu);
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

            var winningTeamPlayers = sideVoteWinnerTeam != Team.None
                ? (sideVoteWinnerTeam == Team.CT ? playingCtPlayers : playingTPlayers)
                : (playerTeam == Team.CT ? playingCtPlayers : playingTPlayers);
            var validPlayers = winningTeamPlayers.Count(p => p != null && IsPlayerValid(p) && !IsBot(p));

            if (sideVotes.Count >= validPlayers)
            {
                ProcessTeamSideVotes();
            }
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
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.ct", captain.Controller.PlayerName]);
            }

            if (captain?.PlayerPawn?.TeamNum == 2)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.t", captain.Controller.PlayerName]);
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

            SetTeamName(Team.CT, IsPlayerValid(captainCt) ? captainCt!.Controller.PlayerName : null);
            SetTeamName(Team.T, IsPlayerValid(captainT) ? captainT!.Controller.PlayerName : null);

            StartMatch();
        });
        Core.Scheduler.StopOnMapChange(switchSidesToken);
    }

    /// <summary>
    /// Keeps the teams on their starting sides based on the captain's current team.
    /// </summary>
    internal void StayStartingSides(IPlayer? captain)
    {
        if (!TryCommitStartingSide(nameof(StayStartingSides))) return;

        if (IsPlayerValid(captain))
        {
            if (captain!.PlayerPawn?.TeamNum == 3)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.ct", captain.Controller.PlayerName]);
                mixScrimsService.RaiseStartingSideChosen(Team.CT);
            }
            else if (captain.PlayerPawn?.TeamNum == 2)
            {
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.t", captain.Controller.PlayerName]);
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
            else
            {
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
            else
            {
                player.ChangeTeamAsync(Team.T);
            }
        }

        isMovingPlayersToTeams = false;
    }
}
