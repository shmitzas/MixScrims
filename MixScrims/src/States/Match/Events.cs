using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Handles the end of a match and transitions the system to a fresh match state.
    /// </summary>
    [GameEventHandler (HookMode.Pre)]
    public HookResult HandleMatchEnd(EventCsWinPanelMatch @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Match)
            return HookResult.Continue;

        // Set before MatchEnded is raised so a subscriber re-reading the state sees Ended. Ended
        // must stay out of the map-change bail list below - it is the state this callback runs in.
        mixScrimsService.SetMatchState(MatchState.Ended);

        // Fire MatchEnded synchronously here rather than inside the 10s delayed callback:
        // scores are still readable, and IMixScrims consumers get the transition signal
        // before the plugin starts tearing state down.
        try
        {
            var md = Core.Game.MatchData;
            int ctScore = md.CTScoreTotal;
            int tScore = md.TerroristScoreTotal;
            Team winner;
            if (ctScore > tScore) winner = Team.CT;
            else if (tScore > ctScore) winner = Team.T;
            else winner = Team.None;
            mixScrimsService.RaiseMatchEnded(winner, ctScore, tScore);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HandleMatchEnd: failed to read MatchData for MatchEnded event; firing with zero scores.");
            mixScrimsService.RaiseMatchEnded(Team.None, 0, 0);
        }

        var token = Core.Scheduler.DelayBySeconds(10, () =>
        {
            try
            {
                // Read at fire time, not schedule time, so a hold taken during the window counts.
                if (mixScrimsService.IsPhaseProgressionHeld())
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleMatchEnd: phase progression held, skipping post-match reset.");
                    return;
                }

                // Bail if another component has already initiated a map change in the
                // meantime - stacking host_workshop_map / map commands across plugins is
                // the classic CS2 map-transition crash window.
                var stateNow = mixScrimsService.GetCurrentMatchState();
                if (stateNow == MatchState.MapLoading || stateNow == MatchState.MapChosen)
                {
                    if (cfg.DetailedLogging)
                        logger.LogInformation("HandleMatchEnd: map change already in progress ({State}), skipping post-match LoadMap.", stateNow);
                    return;
                }

                // Engine null-guard: a concurrent transition (MapChooser, end-of-map cycle)
                // may have begun tearing the world down within the 10s window. GlobalVars is
                // a value type so only the engine reference is null-checked here.
                if (Core.Engine is not { } engine)
                {
                    logger.LogWarning("HandleMatchEnd: Core.Engine unavailable, skipping post-match LoadMap.");
                    return;
                }

                if (cfg.DetailedLogging)
                    logger.LogInformation("Match ended, transitioning to Fresh match state.");
                ResetPluginState();
                var mapNameStr = engine.GlobalVars.MapName.ToString() ?? string.Empty;
                var map = new MapDetails
                {
                    MapName = mapNameStr,
                    DisplayName = mapNameStr,
                };
                LoadMap(map);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HandleMatchEnd: delayed post-match callback failed.");
            }
        });
        Core.Scheduler.StopOnMapChange(token);
        return HookResult.Continue;
    }

    /// <summary>
    /// Pre-round hook that disables join validation while the engine performs any potential
    /// side switch (regular halftime, OT halftime, or OT-period boundary). The bypass is
    /// scoped to tracked players only (see HandlePlayerChangeTeam) so untracked joiners
    /// remain validated.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleMatchRoundPrestart(EventRoundPrestart @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.Match || matchState == MatchState.KnifeRound)
        {
            // Engine-side defense: CS2's CCSGameRules uses NumSpawnable{T,CT} / MaxNum{T,CTs}
            // together with mp_limitteams to kick "excess" players to spectator at side
            // switches (regular halftime and EVERY overtime period boundary). Without this,
            // when sides swap the engine briefly sees both teams piled on one side and
            // dumps half the roster to spec - the plugin's isMovingPlayersToTeams bypass
            // only prevents plugin-side rejections, not engine-side kicks. Re-applying
            // every round-prestart guarantees the override survives any cvar reset.
            RelaxEngineTeamLimits("RoundPrestart");

            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMatchRoundPrestart: state={State}, disabling team validation for potential side switch (CT list:{Ct} T list:{T})",
                    matchState, playingCtPlayers.Count, playingTPlayers.Count);

            isMovingPlayersToTeams = true;
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Post-round hook that, after the engine has finished assigning sides for the new
    /// round, resyncs the plugin's CT/T playing lists from the engine's actual team
    /// assignments and re-enables join validation. This is the source of truth for
    /// every side-switch path (halftime, OT halftime, OT-period transitions, etc.)
    /// and replaces the previous toggle-based halftime swap, which missed OT period
    /// boundaries because <c>round_announce_last_round_half</c> does not fire there.
    /// </summary>
    [GameEventHandler(HookMode.Post)]
    public HookResult HandleRoundStart(EventRoundStart @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Match && matchState != MatchState.KnifeRound)
            return HookResult.Continue;

        // Re-apply the engine team limit override after round start too, in case the engine
        // reset the values during its own SwitchTeamsAtRoundReset() pass.
        RelaxEngineTeamLimits("RoundStart");

        if (matchState != MatchState.Match)
        {
            // knife_round.cfg pins mp_maxmoney 0 but no longer runs mp_restartgame, so
            // nothing zeroes the accounts players carried out of the pick phase.
            if (matchState == MatchState.KnifeRound)
                SetMoneyForPlayers(playingCtPlayers.Concat(playingTPlayers), 0);
            return HookResult.Continue;
        }

        if (pendingMatchStartReset)
        {
            pendingMatchStartReset = false;
            ResetMatchStartState();
        }

        var resyncToken = Core.Scheduler.DelayBySeconds(1f, () =>
        {
            try
            {
                ResyncPlayingListsFromEngine();
                isMovingPlayersToTeams = false;
                if (cfg.DetailedLogging)
                    logger.LogInformation("Round start resync complete - team validation re-enabled (CT:{CT} T:{T}, actual CT:{ActualCt} T:{ActualT})",
                        playingCtPlayers.Count, playingTPlayers.Count,
                        GetPlayersInTeam(Team.CT).Count, GetPlayersInTeam(Team.T).Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HandleRoundStart: deferred resync failed.");
            }
        });
        Core.Scheduler.StopOnMapChange(resyncToken);

        return HookResult.Continue;
    }

    /// <summary>
    /// Raises the engine's per-team spawn and max-player counts to MaxClients so CS2's
    /// <c>mp_limitteams</c> auto-balance cannot force players to Spectator at a side switch.
    /// </summary>
    /// <remarks>
    /// <c>CCSGameRules</c> is a native schema entity - a null, invalid or freed pointer segfaults
    /// the CS2 server process instead of throwing, so this is main-game-thread only.
    /// </remarks>
    internal void RelaxEngineTeamLimits(string callSite)
    {
        try
        {
            CCSGameRules? gameRules = Core.EntitySystem.GetGameRules();
            if (gameRules == null)
            {
                logger.LogWarning("RelaxEngineTeamLimits[{Site}]: GetGameRules() returned null - skipping override", callSite);
                return;
            }

            if (!gameRules.IsValid)
            {
                logger.LogWarning("RelaxEngineTeamLimits[{Site}]: game rules entity is not valid - skipping override", callSite);
                return;
            }

            int maxPlayers = Core.Engine.GlobalVars.MaxClients;
            if (maxPlayers <= 0)
            {
                logger.LogWarning("RelaxEngineTeamLimits[{Site}]: MaxClients={Max} is not positive - skipping override", callSite, maxPlayers);
                return;
            }

            gameRules.NumSpawnableTerrorist = maxPlayers;
            gameRules.MaxNumTerrorists = maxPlayers;
            gameRules.NumSpawnableCT = maxPlayers;
            gameRules.MaxNumCTs = maxPlayers;

            if (cfg.DetailedLogging)
                logger.LogInformation("RelaxEngineTeamLimits[{Site}]: NumSpawnable/MaxNum (T,CT) set to {Max}", callSite, maxPlayers);
        }
        catch (Exception ex)
        {
            // GetGameRules throws when the entity system is not yet initialized, and a stale
            // schema pointer can surface as a managed exception; never let this helper break the
            // event flow it defends.
            logger.LogError(ex, "RelaxEngineTeamLimits[{Site}]: failed to override engine team limits (exception swallowed)", callSite);
        }
    }

    /// <summary>
    /// Drives a round transition through <c>CCSGameRules::TerminateRound</c> instead of
    /// <c>mp_restartgame</c>, whose complete-reset branch is the confirmed segfault site.
    /// </summary>
    /// <remarks>
    /// Does not zero scores, the round counter or player money, so match-start callers must pair
    /// this with <see cref="ResetMatchStartState"/>; the knife round must arm
    /// <see cref="pendingKnifeRoundStart"/> first or the <c>round_end</c> this fires reads as the
    /// knife round having been won; and it is a no-op while <c>WarmupPeriod</c> is set, so warmup
    /// resets through <c>mp_warmup_start</c> + <see cref="ResetWarmupState"/> instead. A caller
    /// that lifted a pause must re-apply it when this returns false - no restart means no
    /// <c>round_prestart</c>, which is what makes a phase pause stick.
    /// </remarks>
    /// <returns><c>true</c> when <c>TerminateRound</c> was dispatched.</returns>
    internal bool RestartRoundManually(string callSite, RoundEndReason reason, float delay)
    {
        try
        {
            CCSGameRules? gameRules = Core.EntitySystem.GetGameRules();
            if (gameRules is null || !gameRules.IsValid)
            {
                logger.LogWarning("RestartRoundManually[{Site}]: game rules invalid - skipping restart.", callSite);
                return false;
            }

            // TerminateRound is a no-op during warmup, so a stuck warmup would otherwise swallow
            // the match start silently.
            if (gameRules.WarmupPeriod)
            {
                logger.LogWarning("RestartRoundManually[{Site}]: still in warmup - skipping restart.", callSite);
                return false;
            }

            gameRules.TerminateRound(reason, delay);
            if (cfg.DetailedLogging)
                logger.LogInformation("RestartRoundManually[{Site}]: TerminateRound({Reason}, delay={Delay:F2}s) queued.", callSite, reason, delay);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RestartRoundManually[{Site}]: exception during TerminateRound dispatch", callSite);
            return false;
        }
    }

    // Far enough out that CCSGameRules::Think can never reach it during a pick phase.
    private const float RoundRestartHoldSeconds = 3600f;

    /// <summary>
    /// Parks CS2's pending round restart so <c>CCSGameRules::Think</c> cannot fire it while a pick
    /// phase is open - <c>TerminateRound</c> arms <c>m_flRestartRoundTime</c> at
    /// <c>curtime + mp_round_restart_delay</c>, which would otherwise land mid-pick.
    /// </summary>
    internal bool HoldPendingRoundRestart(string callSite)
        => SetPendingRoundRestart(callSite, RoundRestartHoldSeconds);

    /// <summary>
    /// Hands the parked restart back to the engine so it lands <paramref name="delay"/> seconds
    /// from now, reusing the one the knife round's <c>round_end</c> already armed - unlike
    /// <see cref="RestartRoundManually"/> this emits no synthetic <c>GameCommencing</c>
    /// <c>round_end</c> for K4-LevelRanks / MapChooser / stats trackers to misread.
    /// </summary>
    /// <returns>
    /// <c>false</c> when nothing was armed to release; the caller must then create the transition
    /// itself via <see cref="RestartRoundManually"/>.
    /// </returns>
    internal bool ReleasePendingRoundRestart(string callSite, float delay)
        => SetPendingRoundRestart(callSite, delay);

    /// <remarks>
    /// Main game thread only, same native-safety contract as <see cref="RelaxEngineTeamLimits"/>.
    /// A zero or already-elapsed timer means nothing is armed, which both directions report as
    /// false rather than fabricating a restart that was never scheduled.
    /// </remarks>
    private bool SetPendingRoundRestart(string callSite, float seconds)
    {
        try
        {
            CCSGameRules? gameRules = Core.EntitySystem.GetGameRules();
            if (gameRules is null || !gameRules.IsValid)
            {
                logger.LogWarning("SetPendingRoundRestart[{Site}]: game rules invalid - skipping.", callSite);
                return false;
            }

            float now = Core.Engine.GlobalVars.CurrentTime;
            float armed = gameRules.RestartRoundTime.Value;
            if (armed <= 0f || armed <= now)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("SetPendingRoundRestart[{Site}]: no restart armed (value={Armed:F2}, now={Now:F2}).", callSite, armed, now);
                return false;
            }

            gameRules.RestartRoundTime.Value = now + seconds;
            gameRules.RestartRoundTimeUpdated();

            if (cfg.DetailedLogging)
                logger.LogInformation("SetPendingRoundRestart[{Site}]: m_flRestartRoundTime {Armed:F2} -> {New:F2} (now={Now:F2}).",
                    callSite, armed, now + seconds, now);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SetPendingRoundRestart[{Site}]: failed to write m_flRestartRoundTime", callSite);
            return false;
        }
    }

    /// <summary>
    /// Restores the state that <c>mp_restartgame</c>'s complete-reset branch used to hand us:
    /// 0-0 scores, round counter at zero, and every playing player back to
    /// <c>mp_startmoney</c>. Run once from <see cref="HandleRoundStart"/> after the match-start
    /// transition lands, so it overwrites anything the round transition itself tallied.
    /// </summary>
    internal void ResetMatchStartState()
        => ResetScoreboardAndMoney("ResetMatchStartState",
                                   playingCtPlayers.Concat(playingTPlayers),
                                   Core.ConVar.Find<int>("mp_startmoney")?.Value ?? 800);

    /// <summary>
    /// Warmup twin of <see cref="ResetMatchStartState"/>. warmup.cfg no longer ends with
    /// <c>mp_restartgame 1</c>, and <c>TerminateRound</c> is a no-op during warmup, so the
    /// reset is done directly. Covers every connected player because warmup has no roster.
    /// Matters most on the surrender path, which reaches warmup without a changelevel.
    /// </summary>
    internal void ResetWarmupState()
        => ResetScoreboardAndMoney("ResetWarmupState",
                                   GetPlayers(),
                                   Core.ConVar.Find<int>("mp_startmoney")?.Value ?? 0);

    private void ResetScoreboardAndMoney(string callSite, IEnumerable<IPlayer> players, int money)
    {
        try
        {
            // AddCTScore / AddTerroristScore are the only APIs that reach the native CCSMatch
            // buffer; bare CCSTeam writes update the scoreboard but leave CCSMatch stale, which
            // is what every other plugin (MapChooser, ServerReporter) actually reads. There is
            // no SetScore, hence the negative delta. AddCTWins / AddTerroristWins are
            // deliberately avoided - they also bump ActualRoundsPlayed.
            var md = Core.Game.MatchData;
            int ctScore = md.CTScoreTotal;
            int tScore = md.TerroristScoreTotal;
            if (ctScore != 0) Core.Game.AddCTScore(-ctScore);
            if (tScore != 0) Core.Game.AddTerroristScore(-tScore);

            // Separate counter from the CCSMatch buckets above; this is the one the engine
            // consults for the mp_maxrounds match-end check.
            CCSGameRules? gameRules = Core.EntitySystem.GetGameRules();
            if (gameRules is not null && gameRules.IsValid)
            {
                gameRules.TotalRoundsPlayed = 0;
                gameRules.TotalRoundsPlayedUpdated();

                // Halftime and the OT phase boundaries count off THIS field, not
                // TotalRoundsPlayed. Leaving the knife round in it made halftime land one
                // round early.
                gameRules.RoundsPlayedThisPhase = 0;
                gameRules.RoundsPlayedThisPhaseUpdated();

                // CCSGameRules carries TWO total-round counters: the networked
                // m_totalRoundsPlayed above (HUD/display) and the server-side
                // m_iTotalRoundsPlayed, which SwiftlyS2 generates as TotalRoundsPlayed1
                // because the names collide. The halftime check reads the server-side one,
                // so missing it kept halftime a round early even with the other two zeroed.
                // Server-only, hence no Updated() twin.
                gameRules.TotalRoundsPlayed1 = 0;
            }

            int statsCleared = ResetPlayerMatchStats(players);
            int reset = SetMoneyForPlayers(players, money);

            if (cfg.DetailedLogging)
            {
                logger.LogInformation("{Site}: scores {Ct}:{T} -> 0:0, rounds -> 0, stats cleared for {Stats}, {Count} players set to ${Money}.",
                    callSite, ctScore, tScore, statsCleared, reset, money);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Site}: failed to reset match state", callSite);
        }
    }

    /// <summary>Returns the number of players whose account was written.</summary>
    internal int SetMoneyForPlayers(IEnumerable<IPlayer> players, int amount)
    {
        int reset = 0;
        foreach (var player in players)
        {
            if (!IsPlayerValid(player)) continue;
            var money = player.Controller?.InGameMoneyServices;
            if (money is null) continue;
            money.Account = amount;
            money.AccountUpdated();
            reset++;
        }
        return reset;
    }

    /// <summary>
    /// Clears the scoreboard stats <c>mp_restartgame</c>'s complete-reset branch used to wipe.
    /// Without this the knife round's kills / deaths / assists / damage carry into the match.
    /// <c>MatchStats</c> inherits the per-round stat fields, so they are read straight off it.
    /// </summary>
    /// <returns>The number of players whose stats were written.</returns>
    internal int ResetPlayerMatchStats(IEnumerable<IPlayer> players)
    {
        int cleared = 0;
        foreach (var player in players)
        {
            if (!IsPlayerValid(player)) continue;

            try
            {
                var controller = player.Controller;
                if (controller is null) continue;

                controller.Score = 0;
                controller.ScoreUpdated();
                controller.MVPs = 0;
                controller.MVPsUpdated();

                var tracking = controller.ActionTrackingServices;
                if (tracking is null) continue;

                var stats = tracking.MatchStats;
                stats.Kills = 0; stats.KillsUpdated();
                stats.Deaths = 0; stats.DeathsUpdated();
                stats.Assists = 0; stats.AssistsUpdated();
                stats.Damage = 0; stats.DamageUpdated();
                stats.HeadShotKills = 0; stats.HeadShotKillsUpdated();
                tracking.MatchStatsUpdated();

                cleared++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ResetPlayerMatchStats: failed to clear stats for a player; continuing.");
            }
        }
        return cleared;
    }
}
