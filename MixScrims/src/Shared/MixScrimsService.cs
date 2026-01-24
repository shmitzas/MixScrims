using MixScrims.Contract;

namespace MixScrims;

public class MixScrimsService : IMixScrims
{
    MatchState MatchState { get; set; } = MatchState.Warmup;
    PluginState PluginState { get; set; } = PluginState.Production;

    /// <summary>
    /// Gets the current state of the match.
    /// </summary>
    public MatchState GetCurrentMatchState()
    {
        return MatchState;
    }

    /// <summary>
    /// Gets the current state of the plugin.
    /// </summary>
    public PluginState GetCurrentPluginState()
    {
        return PluginState;
    }

    /// <summary>
    /// Sets the current match state.
    /// </summary>
    public void SetMatchState(MatchState state)
    {
        MatchState = state;
    }

    /// <summary>
    /// Sets the current state of the plugin.
    /// </summary>
    public void SetPluginState(PluginState state)
    {
        PluginState = state;
    }

    public void Dispose()
    {
        // Cleanup resources if needed
        // Currently no unmanaged resources to dispose
    }
}
