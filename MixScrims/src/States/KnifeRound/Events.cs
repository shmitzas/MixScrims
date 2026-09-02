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
    /// any pause issued alongside it - <c>PickingStartingSide</c> from knife_round.cfg's
    /// <c>mp_restartgame 1</c>, <c>PickingTeam</c> from the plugin-driven TerminateRound in
    /// StartTeamPickingPhase - so the pause only sticks when re-applied on the round it
    /// produces.
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
