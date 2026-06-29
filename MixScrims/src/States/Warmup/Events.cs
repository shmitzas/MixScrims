using SwiftlyS2.Shared.Events;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Registers listeners for events during the Warmup state.
    /// </summary>
    internal void RegisterWarmupListeners()
    {
        Core.Event.OnMapLoad += WarmupHandleOnMapStart;
    }

    /// <summary>
    /// Handles map start events during Warmup state by clearing player lists.
    /// </summary>
    internal void WarmupHandleOnMapStart(IOnMapLoadEvent @event)
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Warmup)
            return;
        ResetRtvState();
        LoadWarmupConfig();
    }
}
