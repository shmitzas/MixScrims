using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully marks all players as ready and starts the next mix state
    ///</summary>
    [Command("forceready", false, "managemix", HelpText = "Forces all connected players into the ready state. Usage: !forceready")]
    public void OnForceReady(ICommandContext context)
    {
        var admin = context.Sender;
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

        if (matchState != MatchState.Warmup && matchState != MatchState.MapChosen)
        {
            logger.LogWarning("OnForceReady: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "forceready"]);
            }
            return;
        }

        ForceReadyAllPlayers();

        // Success announcement runs after ForceReadyAllPlayers so an aborted run
        // (invalid state, exception) never fires a misleading "players were forced" message.
        if (admin == null)
        {
            logger.LogInformation("Players were forced into ready state by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.ready", "Console"]);
        }
        else
        {
            logger.LogInformation("Players were forced into ready state by {AdminName}", admin.Controller.PlayerName);
            PrintMessageToAllPlayers(Core.Localizer["command.force.ready", admin.Controller.PlayerName]);
        }
    }

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
            // Cache the live loop-var SteamID and use SafeSteamId on roster entries: readyPlayers
            // can carry disposed IPlayer references and reading .SteamID on those throws
            // ObjectDisposedException, which is the crash the log at ForceReady.cs:69 showed.
            var playerId = player.SteamID;
            if (!readyPlayers.Any(rp => SafeSteamId(rp) == playerId))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("OnForceReady: Adding {PlayerName} (SteamID={SteamID}) to ready list", SafePlayerName(player), playerId);
                AddPlayerToReadyList(player, false);
            }
        }
    }
}
