using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Marks the sending player as ready, or tells them they already are.
    /// </summary>
    [Command("ready", false, "", HelpText = "Marks you as ready for the match to start. Usage: !ready")]
    public void OnReady(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnReady: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnReady: player is invalid");
            return;
        }

        AddPlayerToReadyList(player, true);
    }
}
