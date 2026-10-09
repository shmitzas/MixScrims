using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully starts the match regardless of how many players are ready
    ///</summary>
    [Command("mix_start", false, "managemix", HelpText = "Forcefully starts the match regardless of how many players are ready. Usage: !mix_start")]
    public void OnForceMatchStart(ICommandContext context)
    {
        var admin = context.Sender;
        var connectedPlayers = GetPlayers().Count;

        if (!cfg.AdminCommandsBypassPlayerLimit && connectedPlayers < cfg.MinimumReadyPlayers)
        {
            logger.LogWarning("OnForceMatchStart: Not enough players connected ({Connected}/{Minimum})", connectedPlayers, cfg.MinimumReadyPlayers);
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
                if (cfg.DetailedLogging)
                    logger.LogInformation("Match started by force by Admin (null)");
                PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Admin"]);
            }
            else
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Match started by force by {AdminName}", admin.Name);
                PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", admin.Name]);
            }
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Match started by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Console"]);
        }

        // Past picking the rosters are sealed, so re-entering would restart a live match.
        var currentState = mixScrimsService.GetCurrentMatchState();
        if (currentState != MatchState.Warmup
            && currentState != MatchState.MapChosen
            && currentState != MatchState.MapLoading)
        {
            logger.LogWarning("OnForceMatchStart: ignored, current state is {State} (only allowed from Warmup/MapChosen).", currentState);
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "warmup"]);
            }
            return;
        }

        StartMatchWithoutKnifeRound();
    }
}
