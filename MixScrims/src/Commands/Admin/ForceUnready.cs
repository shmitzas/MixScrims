using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully marks all players as not ready
    ///</summary>
    [Command("forceunready", true, "managemix", HelpText = "Forces all connected players into the unready state. Usage: !forceunready")]
    public void OnForceUnready(ICommandContext context)
    {
        var admin = context.Sender;
        var connectedPlayers = GetPlayers().Count;

        if (connectedPlayers < cfg.MinimumReadyPlayers)
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
    }

    internal void ForceUnreadyAllPlayers()
    {
        var players = GetPlayers();
        foreach (var player in players)
        {
            if (readyPlayers.Any(rp => rp.PlayerID == player.PlayerID))
            {
                logger.LogInformation("ForceUnreadyAllPlayers: Removing players from ready list");
                RemovePlayerFromReadyList(player, false);
            }
        }
    }
}
