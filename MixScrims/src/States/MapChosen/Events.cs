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
            logger.LogInformation($"AddPickedMapToPlayedMaps: OnMapLoad event fired for map {mapName.MapName}");
        HandleMapChosenNewMapLoad();
    }

    /// <summary>
    /// Handles the logic required when a new map is loaded after a map has been chosen in the match flow.
    /// </summary>
    internal void HandleMapChosenNewMapLoad()
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (cfg.DetailedLogging)
            logger.LogInformation($"HandleMapChosenNewMapLoad: Current match state is {matchState}");

        if (matchState != MatchState.MapLoading)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"HandleMapChosenNewMapLoad: Ignored map start event because match state is {matchState}");
            return;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("HandleMapChosenNewMapLoad: Clearing ready players and executing warmup config");

        readyPlayers.Clear();

        mixScrimsService.SetMatchState(MatchState.MapChosen);
        if (cfg.DetailedLogging)
            logger.LogInformation("HandleMapChosenNewMapLoad: Match state changed to MapChosen");

        var warmupToken = Core.Scheduler.DelayBySeconds(5, LoadWarmupConfig);
        Core.Scheduler.StopOnMapChange(warmupToken);

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandleMapChosenNewMapLoad: Captains are disabled in configuration.");
            return;
        }

        var captainAnnouncementToken = Core.Scheduler.DelayBySeconds(30, () =>
        {
            PickCaptains();
            captainsAnnouncementsTimer = Core.Scheduler.DelayBySeconds(cfg.ChatAnnouncementTimers.CaptainsAnnouncements, PrintChosenCaptains);
        });
        Core.Scheduler.StopOnMapChange(captainAnnouncementToken);

        return;
    }
}
