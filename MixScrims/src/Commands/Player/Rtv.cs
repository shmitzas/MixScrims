using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Lets players vote during Warmup to restart the map vote (rock the vote).
    /// </summary>
    [Command("rtv", false, "", HelpText = "Vote to restart the map vote during warmup. Usage: !rtv")]
    public void OnRtv(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnRtv: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnRtv: player is invalid");
            return;
        }

        // Feature flag — silently treat as unknown when disabled.
        if (!cfg.Rtv.Enabled)
            return;

        if (IsBot(player))
            return;

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Warmup)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "rtv"]);
            return;
        }

        TryRegisterRtvVote(player);
    }
}
