using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Handles the end of a knife round and initiates the process for the winning team's captain to choose the starting
    /// side.
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

            if (cfg.DetailedLogging)
                logger.LogInformation("HandleRoundEndOnKnifeRound: Knife round ended, transitioning to PickingStartingSide state.");
            if (@event.Winner == 2)
            {
                PromptWinnerTCaptainoChoseStartingSide(Team.T);
            }
            else if (@event.Winner == 3)
            {
                PromptWinnerTCaptainoChoseStartingSide(Team.CT);
            }
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Re-applies the pause on round prestart for phases that must stay frozen while
    /// players interact with menus. Both phases enter through a round restart that clears
    /// any pause issued alongside it - <c>PickingStartingSide</c> from the knife round's
    /// transition, <c>PickingTeam</c> from StartTeamPickingPhase - so the pause only sticks
    /// when re-applied on the round it produces.
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
}
