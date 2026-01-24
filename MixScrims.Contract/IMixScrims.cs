namespace MixScrims.Contract;

public interface IMixScrims : IDisposable
{
    /// <summary>
    /// Retrieves the current state of the match.
    /// </summary>
    MatchState GetCurrentMatchState();

    /// <summary>
    /// Retrieves the current operational state of the plugin.
    /// </summary>
    PluginState GetCurrentPluginState();
}
