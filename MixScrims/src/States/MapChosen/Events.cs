using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using MixScrims.Contract;
using SwiftlyS2.Shared.Events;

namespace MixScrims;

public partial class MixScrims
{
    internal List<MapDetails> playedMaps { get; set; } = [];

    /// <summary>
    /// Registers listeners for events during the MapChosen state.
    /// </summary>
    internal void RegisterMapChosenListeners()
    {
        Core.Event.OnMapLoad += AddPickedMapToPlayedMaps;
    }

    /// <summary>
    /// Adds the specified map to the list of played maps if the match state allows it.
    /// </summary>
    public void AddPickedMapToPlayedMaps(IOnMapLoadEvent mapName)
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("AddPickedMapToPlayedMaps: OnMapLoad event fired for map {MapName}", mapName.MapName);
        HandleMapChosenNewMapLoad();
    }

    /// <summary>
    /// Handles the logic required when a new map is loaded after a map has been chosen in the match flow.
    /// </summary>
    internal void HandleMapChosenNewMapLoad()
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (cfg.DetailedLogging)
            logger.LogInformation("HandleMapChosenNewMapLoad: Current match state is {MatchState}", matchState);

        if (matchState != MatchState.MapLoading)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMapChosenNewMapLoad: Ignored map start event because match state is {MatchState}", matchState);
            return;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleMapChosenNewMapLoad: Clearing ready players and executing warmup config");

        readyPlayers.Clear();

        // Restore the state captured before the MapLoading transition. If a manual map change
        // was issued during Warmup, we stay in Warmup. If the map vote flow set MapChosen prior
        // to LoadSelectedMap, we go back to MapChosen. Default to MapChosen for safety.
        var targetState = stateBeforeMapLoading ?? MatchState.MapChosen;
        stateBeforeMapLoading = null;

        // MapLoading is a transient internal state - never restore it.
        if (targetState == MatchState.MapLoading)
            targetState = MatchState.MapChosen;

        // Ended is a dead end - RTV and !ready are both Warmup-gated - and a consumer reaches
        // here by calling ChangeMap after a match, since the post-match auto-reset is skipped
        // while phase progression is held.
        if (targetState == MatchState.Ended)
            targetState = MatchState.Warmup;

        // If the map load was driven by the match flow (vote -> MapChosen -> LoadSelectedMap),
        // force MapChosen regardless of what was captured. This guarantees the next ready burst
        // advances to team picking instead of looping back into map voting.
        if (mapLoadedFromMatchFlow)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMapChosenNewMapLoad: mapLoadedFromMatchFlow set, forcing target state to MapChosen (was {State}).", targetState);
            targetState = MatchState.MapChosen;
            mapLoadedFromMatchFlow = false;
        }

        mixScrimsService.SetMatchState(targetState);
        if (cfg.DetailedLogging)
            logger.LogInformation("HandleMapChosenNewMapLoad: Match state changed to {State}", targetState);

        var warmupToken = Core.Scheduler.DelayBySeconds(5, LoadWarmupConfig);
        Core.Scheduler.StopOnMapChange(warmupToken);

        // Captain selection only makes sense once the map vote has produced a chosen map.
        // Skip it for any other restored state (e.g. Warmup after a manual !map).
        if (targetState != MatchState.MapChosen)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMapChosenNewMapLoad: Skipping captain selection because restored state is {State}", targetState);
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMapChosenNewMapLoad: Captains are disabled in configuration.");
            return;
        }

        var captainAnnouncementToken = Core.Scheduler.DelayBySeconds(30, () =>
        {
            PickCaptains();
            captainsAnnouncementsTimer?.Cancel();
            // DelayAndRepeatBySeconds (not RepeatBySeconds): SwiftlyS2 fires the first tick of a
            // RepeatBySeconds timer synchronously at schedule time, which would double-print the
            // captain announcement right on top of the pick-time announcement PickCaptains just
            // emitted. Delay the first reminder by one full interval instead.
            captainsAnnouncementsTimer = Core.Scheduler.DelayAndRepeatBySeconds(cfg.ChatAnnouncementTimers.CaptainsAnnouncements, cfg.ChatAnnouncementTimers.CaptainsAnnouncements, PrintChosenCaptains);
            Core.Scheduler.StopOnMapChange(captainsAnnouncementsTimer);
        });
        Core.Scheduler.StopOnMapChange(captainAnnouncementToken);

        return;
    }
}
