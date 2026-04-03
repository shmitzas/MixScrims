using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully starts the match regardless of how many players are ready
    ///</summary>
    public void OnForceMatchStart(ICommandContext context)
    {
        var admin = context.Sender;
        var connectedPlayers = GetPlayers().Count;

        if (connectedPlayers < cfg.MinimumReadyPlayers)
        {
            logger.LogWarning($"OnForceMatchStart: Not enough players connected ({connectedPlayers}/{cfg.MinimumReadyPlayers})");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
            }
            else
            {
                logger.LogWarning("Console: Not enough players to force start match");
            }
            return;
        }

        if (context.IsSentByPlayer)
        {
            if (admin == null)
            {
                logger.LogInformation("Match started by force by Admin (null)");
                PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Admin"]);
            }
            else
            {
                logger.LogInformation($"Match started by force by {admin.Controller.PlayerName}");
                PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", admin.Controller.PlayerName]);
            }
        }
        else
        {
            logger.LogInformation("Match started by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Console"]);
        }

        StartKnifeRound();
    }
}
