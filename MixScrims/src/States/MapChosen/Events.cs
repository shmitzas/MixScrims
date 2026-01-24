using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    private List<MapDetails> playedMaps { get; set; } = [];

    /// <summary>
    /// Adds the specified map to the list of played maps if the match state allows it.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult AddPickedMapToPlayedMaps(EventRoundPrestart @event)
    {
        HandleMapChosenNewMapLoad();
        return HookResult.Continue;
    }

    private void HandleMapChosenNewMapLoad()
    {
        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.MapLoading)
        {
            logger.LogWarning($"HandleMapChosenNewMapLoad: Ignored map start event because match state is {matchState}");
            return;
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("Clearing ready players and executing warmup config");

        readyPlayers.Clear();

        mixScrimsService.SetMatchState(MatchState.MapChosen);

        var warmupToken = Core.Scheduler.DelayBySeconds(5, LoadWarmupConfig);
        Core.Scheduler.StopOnMapChange(warmupToken);

        var captainAnnouncementToken = Core.Scheduler.DelayBySeconds(30, () =>
        {
            PickCaptains();
            captainsAnnouncementsTimer = Core.Scheduler.DelayBySeconds(cfg.ChatAnnouncementTimers.CaptainsAnnouncements, PrintChosenCaptains);
        });
        Core.Scheduler.StopOnMapChange(captainAnnouncementToken);

        return;
    }
}
