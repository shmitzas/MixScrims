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
    }

    internal void ForceReadyAllPlayers()
    {
        var players = GetPlayers();
        foreach (var player in players)
        {
            if (!readyPlayers.Any(rp => rp.PlayerID == player.PlayerID))
            {
                logger.LogInformation("OnForceReady: Adding players to ready list");
                AddPlayerToReadyList(player, false);
            }
        }
    }
}
