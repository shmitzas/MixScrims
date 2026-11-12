using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully marks all players as ready and starts the next mix state
    ///</summary>
    [Command("forceready", false, "managemix", HelpText = "Forces all connected players into the ready state. Usage: !forceready")]
    public void OnForceReady(ICommandContext context) => ForceReadyByAdmin(context.Sender);

    /// <summary><c>!forceready</c>, shared with the control panel's force-ready row, which passes <paramref name="deferred"/>.</summary>
    internal void ForceReadyByAdmin(IPlayer? admin, bool deferred = false)
    {
        var connectedPlayers = GetPlayers().Count;

        if (!cfg.AdminCommandsBypassPlayerLimit && connectedPlayers < cfg.MinimumReadyPlayers)
        {
            logger.LogWarning("OnForceReady: Not enough players connected ({Connected}/{Minimum})", connectedPlayers, cfg.MinimumReadyPlayers);
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
            }
            else
            {
                logger.LogWarning("Console: Not enough players to force ready");
            }
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (!IsForceReadyState(matchState))
        {
            logger.LogWarning("OnForceReady: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "forceready"]);
            }
            return;
        }

        if (deferred)
        {
            // A phase moving on inside a menu click makes its sound Fail, so it reruns whole next tick, checks included.
            Core.Scheduler.NextTick(() =>
            {
                if (IsPlayerConnected(admin))
                    ForceReadyByAdmin(admin);
            });
            return;
        }

        ForceReadyAllPlayers();

        // Success announcement runs after ForceReadyAllPlayers so an aborted run
        // (invalid state, exception) never fires a misleading "players were forced" message.
        if (admin == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Players were forced into ready state by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.ready", "Console"]);
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Players were forced into ready state by {AdminName}", admin.Name);
            PrintMessageToAllPlayers(Core.Localizer["command.force.ready", admin.Name]);
        }
    }

    /// <summary>The states <c>!forceready</c> and <c>!forceunready</c> accept.</summary>
    internal static bool IsForceReadyState(MatchState state) =>
        state is MatchState.Warmup or MatchState.MapChosen;

    internal void ForceReadyAllPlayers()
    {
        // In TestMode bots are implicitly ready via GetEffectiveReadyCount(); keep them out of
        // the readyPlayers list so their shared SteamID 0 doesn't collapse every bot onto a
        // single dedup slot (the bug that produced "2/10 ready" with 1 human + 9 bots).
        var players = cfg.TestMode
            ? GetPlayers().Where(p => !IsBot(p)).ToList()
            : GetPlayers();

        foreach (var player in players)
        {
            var playerId = player.SteamID;
            if (!readyPlayers.Any(e => e.SteamId == playerId))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("OnForceReady: Adding {PlayerName} (SteamID={SteamID}) to ready list", SafePlayerName(player), playerId);
                AddPlayerToReadyList(player, false);
            }
        }
    }
}
