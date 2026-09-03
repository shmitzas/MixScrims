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
    /// Overrides the engine's per-team spawn/max-player counts to MaxClients to neutralize
    /// CS2's built-in <c>mp_limitteams</c> / auto-balance behavior, which otherwise force-
    /// moves players to Spectator at side switches (halftime + every OT halftime). Based
    /// on the well-known TeamLimitFix pattern (OniquirAK/Fixes/TeamLimitFix.cs).
    /// </summary>
    /// <summary>
    /// Overrides the engine's per-team spawn/max-player counts to MaxClients to neutralize
    /// CS2's built-in <c>mp_limitteams</c> / auto-balance behavior, which otherwise force-
    /// moves players to Spectator at side switches (halftime + every OT halftime). Based
    /// on the well-known TeamLimitFix pattern (OniquirAK/Fixes/TeamLimitFix.cs).
    /// </summary>
    /// <remarks>
    /// SAFETY: <c>CCSGameRules</c> is a native schema entity. Dereferencing a null or
    /// invalid pointer, or writing to schema fields after the entity has been freed, will
    /// segfault the CS2 server process (not a managed exception - the whole server dies).
    /// Every access is therefore guarded by:
    ///   1. A try/catch around the entire body (covers <c>InvalidOperationException</c>
    ///      thrown by <c>Core.EntitySystem</c> when called too early, plus any native
    ///      access violations the runtime can surface).
    ///   2. An explicit null check on the returned reference.
    ///   3. An <c>IsValid</c> check (point-in-time validity of the underlying entity).
    ///   4. A sanity check that <c>MaxClients</c> is a positive value before writing.
    /// Never call this method outside the main game thread (round/match event handlers
    /// and <c>NextTick</c> callbacks are safe; arbitrary scheduler delays are too).
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
                // Defensive: MaxClients should always be positive on a running server, but
                // if it ever isn't, writing a zero/negative cap would make CS2 worse, not
                // better. Bail rather than corrupt the schema.
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
            // Catch-all on purpose: GetGameRules can throw InvalidOperationException when
            // the entity system is not yet initialized, and a stale/freed schema pointer
            // could theoretically surface as a managed exception. We must never let an
            // exception from this defense-in-depth helper crash the plugin's event flow.
            logger.LogError(ex, "RelaxEngineTeamLimits[{Site}]: failed to override engine team limits (exception swallowed)", callSite);
        }
    }

    /// <summary>
    /// Drives a round transition through <c>CCSGameRules::TerminateRound</c> instead of
    /// <c>mp_restartgame</c>. Both reach <c>RestartRound()</c>, but only <c>mp_restartgame</c>
    /// takes its complete-reset branch, which is the confirmed segfault site on the 3rd match
    /// of a server process. Match-start callers must pair this with
    /// <see cref="ResetMatchStartState"/> — TerminateRound does NOT zero scores, the round
    /// counter, or player money the way <c>mp_restartgame</c> did.
    /// </summary>
    /// <remarks>
    /// Safe from <c>Match</c>, <c>PickingTeam</c> and <c>KnifeRound</c>. The knife round has to
    /// arm <see cref="pendingKnifeRoundStart"/> first — the <c>round_end</c> this fires would
    /// otherwise land in <c>HandleRoundEndOnKnifeRound</c> and be read as the knife round
    /// having been won. Cannot be used from <c>Warmup</c>: <c>TerminateRound</c> is a no-op
    /// while <c>WarmupPeriod</c> is set, so warmup resets through
    /// <c>mp_warmup_start</c> + <see cref="ResetWarmupState"/> instead.
    /// </remarks>
    /// <returns>
    /// <c>true</c> when <c>TerminateRound</c> was dispatched. Callers that lifted a pause in
    /// anticipation of the restart must re-apply it on <c>false</c> — no restart means no
    /// <c>round_prestart</c>, and the prestart hook is what makes a phase pause stick.
    /// </returns>
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

            // TerminateRound is a no-op during warmup, so a stuck warmup would silently
            // swallow the match start rather than surfacing here.
            if (gameRules.WarmupPeriod)
            {
                logger.LogWarning("RestartRoundManually[{Site}]: still in warmup - skipping restart.", callSite);
                return false;
            }

            gameRules.TerminateRound(reason, delay);
            logger.LogInformation("RestartRoundManually[{Site}]: TerminateRound({Reason}, delay={Delay:F2}s) queued.", callSite, reason, delay);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RestartRoundManually[{Site}]: exception during TerminateRound dispatch", callSite);
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
            }

            int reset = SetMoneyForPlayers(players, money);

            logger.LogInformation("{Site}: scores {Ct}:{T} -> 0:0, rounds -> 0, {Count} players set to ${Money}.",
                callSite, ctScore, tScore, reset, money);
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
}
