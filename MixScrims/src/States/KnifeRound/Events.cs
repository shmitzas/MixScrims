using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Handles the end of a knife round and initiates the process for the winning team's captain to choose the starting
    /// side. A knife round ended by the clock (or drawn) gets a random winner so the match never stalls.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleRoundEndOnKnifeRound(EventRoundEnd @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.KnifeRound)
        {
            // Our own start transition, not a knife-round result. Winner is normally
            // CSTeam_None for GameCommencing, but the flag makes it explicit rather than
            // depending on what the engine puts in the event.
            if (pendingKnifeRoundStart)
            {
                pendingKnifeRoundStart = false;
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleRoundEndOnKnifeRound: ignoring the round_end from the knife-round start restart.");
                return HookResult.Continue;
            }

            var reason = (RoundEndReason)@event.Reason;
            var eventWinner = (Team)@event.Winner;
            // No C4 here: these mean the clock ran out, or (RoundDraw) both teams died at once.
            var clockOrDraw = reason is RoundEndReason.RoundDraw or RoundEndReason.TargetSaved
                or RoundEndReason.HostagesNotRescued or RoundEndReason.TerroristsNotEscaped;

            Team winner;
            if (!clockOrDraw && eventWinner is Team.T or Team.CT)
            {
                winner = eventWinner;
            }
            else if (reason == RoundEndReason.GameCommencing)
            {
                // Not our start restart (consumed above): leave the engine's restart armed so the knife round replays.
                logger.LogWarning("HandleRoundEndOnKnifeRound: GameCommencing round_end with no winner (winner={Winner}); not holding the restart, the knife round replays.", @event.Winner);
                return HookResult.Continue;
            }
            else
            {
                winner = Random.Shared.Next(2) == 0 ? Team.CT : Team.T;
                if (cfg.DetailedLogging)
                    logger.LogInformation("HandleRoundEndOnKnifeRound: no decisive knife-round result (winner={Winner}, reason={Reason}:{ReasonName}); picked {Team} at random.",
                        @event.Winner, @event.Reason, reason, winner);
                PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.time_expired"]);
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("HandleRoundEndOnKnifeRound: Knife round ended, transitioning to PickingStartingSide state.");

            // Earliest possible grab of the restart this very round_end arms. The engine may
            // write m_flRestartRoundTime after this Pre hook returns, so the phase's own 0.5s
            // ticker (BeginStartingSideRestartHold) is what actually guarantees the hold.
            // Only held once a winner exists: a parked restart with no phase to release it is a permanent stall.
            HoldPendingRoundRestart("KnifeRoundEnd");
            PromptWinnerTCaptainoChoseStartingSide(winner);
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Re-applies the pause on round prestart for phases that must stay frozen while
    /// players interact with menus. <c>PickingTeam</c> enters through StartTeamPickingPhase's
    /// restart, which clears any pause issued alongside it, so the pause only sticks when
    /// re-applied on the round it produces. <c>PickingStartingSide</c> normally sees no restart
    /// at all now (the phase parks the engine's timer — see BeginStartingSideRestartHold); it
    /// stays listed here as a safety net for the case where the hold could not be applied.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleRoundPrestartPreKnifeRound(EventRoundPrestart @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState == MatchState.PickingStartingSide || matchState == MatchState.PickingTeam)
        {
            PauseMatch();
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Suppresses the round MVP for the knife round. CS2 still awards one and plays the winner's
    /// music kit, which now bleeds into the match start because the pick phase holds the round
    /// open. <see cref="ResetMatchStartState"/> zeroes the MVP counter afterwards either way;
    /// this kills the announcement and the music at the source.
    /// </summary>
    /// <remarks>
    /// Covers <c>PickingStartingSide</c> too: <c>round_mvp</c> and <c>round_end</c> fire in the
    /// same win sequence, so the state may already have moved on by the time this runs.
    /// </remarks>
    [GameEventHandler(HookMode.Pre)]
    public HookResult HandleRoundMvpOnKnifeRound(EventRoundMvp @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.KnifeRound && matchState != MatchState.PickingStartingSide)
            return HookResult.Continue;

        // Belt and braces: Stop blocks the announcement, NoMusic covers the music kit in case
        // the client still receives it.
        @event.NoMusic = 1;
        return HookResult.Stop;
    }
}
