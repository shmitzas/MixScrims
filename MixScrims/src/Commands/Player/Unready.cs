using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Marks player as unready if they were ready (for example if a player disconnects while being ready)
    /// </summary>
    [Command("unready")]
    public void OnUnReady(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnUnReady: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnUnReady: player is invalid");
            return;
        }

        RemovePlayerFromReadyList(player, true);
    }
}
