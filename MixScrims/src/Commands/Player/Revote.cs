using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Players can revote map pick if the map picking is not over yet
    /// </summary>
    [Command("revote", false, "", HelpText = "Reopens the map vote menu while map voting is active. Usage: !revote")]
    public void OnRevote(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnRevote: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnRevote: player is invalid");
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.MapVoting)
        {
            if (mapVotingMenu == null)
            {
                logger.LogError("OnRevote: mapVotingMenu is null");
                PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "revote"]);
                return;
            }
            DisplayMapVotingMenu(player);
            return;
        }

        PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "revote"]);
    }
}
