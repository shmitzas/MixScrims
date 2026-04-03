using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Players can call timeout if they have timeouts left and the match is in progress
    /// </summary>
    [Command("timeout")]
    public void OnTimeout(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnTimeout: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnTimeout: player is invalid");
            return;
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("OnTimeout: PlayerPawn is null for player {PlayerName}", player.Name);
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Match)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "timeout"]);
            return;
        }

        if (timeoutPending != TimeoutPending.None)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.timeout_pending"]);
            return;
        }

        var team = (Team)player.PlayerPawn.TeamNum;

        if (team == Team.CT)
        {
            if (timeoutCountCt < 1)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.no_timeouts_left", 0, cfg.Timeouts]);
                return;
            }
            StartTimeoutVote(player, Team.CT);
        }

        if (team == Team.T)
        {
            if (timeoutCountT < 1)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.no_timeouts_left", 0, cfg.Timeouts]);
                return;
            }
            StartTimeoutVote(player, Team.T);
        }
    }
}
