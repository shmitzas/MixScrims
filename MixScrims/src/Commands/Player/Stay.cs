using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Additional way of choosing whether to stay or switch teams after knife round
    /// </summary>
    [Command("stay", false, "", HelpText = "Vote to stay on your current side after winning the knife round. Usage: !stay")]
    public void OnStay(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnStay: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnStay: player is invalid");
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.PickingStartingSide)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "sidePick"]);
            return;
        }

        if (cfg.DisableCaptains)
        {
            HandleCaptainSideChoice(player, "Stay");
            return;
        }

        if (player.PlayerID != winnerCaptain?.PlayerID)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.not_captain"]);
            return;
        }

        if (!IsBot(player) && IsPlayerValid(player))
        {
            var menu = Core.MenusAPI.GetCurrentMenu(player);
            if (menu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(player, menu);
            }
        }

        var token = Core.Scheduler.DelayBySeconds(1, () => StayStartingSides(player));
        Core.Scheduler.StopOnMapChange(token);
        logger.LogInformation("OnStay: Captain {PlayerName} chose to !stay", player.Controller.PlayerName);
    }
}
