using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    [Command("ready", false, "", HelpText = "Marks you as ready for the match to start. Usage: !ready")]
    /// <summary>
    /// Marks player as ready if they are not already ready. If they are ready, they get informed that they are already ready
    /// </summary>
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
