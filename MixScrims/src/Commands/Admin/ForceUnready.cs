using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully marks all players as not ready
    ///</summary>
    [Command("forceunready", false, "managemix", HelpText = "Forces all connected players into the unready state. Usage: !forceunready")]
    public void OnForceUnready(ICommandContext context)
    {
        var admin = context.Sender;
        var connectedPlayers = GetPlayers().Count;

        if (!cfg.AdminCommandsBypassPlayerLimit && connectedPlayers < cfg.MinimumReadyPlayers)
        {
            logger.LogWarning("OnForceUnready: Not enough players connected ({Connected}/{Minimum})", connectedPlayers, cfg.MinimumReadyPlayers);
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
            }
            else
            {
                logger.LogWarning("Console: Not enough players to force unready");
            }
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Warmup && matchState != MatchState.MapChosen)
        {
            logger.LogWarning("OnForceUnready: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "forceunready"]);
            }
            return;
        }

        ForceUnreadyAllPlayers();

        // Success announcement runs after ForceUnreadyAllPlayers so an aborted run
        // (invalid state, exception) never fires a misleading "players were forced" message.
        if (admin == null)
        {
            logger.LogInformation("Players were forced into unready state by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.unready", "Console"]);
        }
        else
        {
            logger.LogInformation("Players were forced into unready state by {AdminName}", admin.Controller.PlayerName);
            PrintMessageToAllPlayers(Core.Localizer["command.force.unready", admin.Controller.PlayerName]);
        }
    }

    internal void ForceUnreadyAllPlayers()
    {
        // Symmetric with ForceReadyAllPlayers: in TestMode bots are never in readyPlayers, so
        // iterating over them here would be a no-op at best and would fall through the same
        // SteamID-0 collision at worst. Keep the pass humans-only when TestMode is on.
        var players = cfg.TestMode
            ? GetPlayers().Where(p => !IsBot(p)).ToList()
            : GetPlayers();

        foreach (var player in players)
        {
            // Cache live loop-var SteamID and use SafeSteamId on roster entries: readyPlayers
            // may hold disposed IPlayer refs (see ForceReady.cs:69 crash) so raw .SteamID throws.
            var playerId = player.SteamID;
            if (readyPlayers.Any(rp => SafeSteamId(rp) == playerId))
            {
                RemovePlayerFromReadyList(player, false);
            }
        }
    }
}
