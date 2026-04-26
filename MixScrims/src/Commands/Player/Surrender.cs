using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Players can surrender the match if their team agrees
    /// </summary>
    [Command("surrender", true, "", HelpText = "Starts a vote for your team to surrender the match. Usage: !surrender")]
    public void OnSurrender(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnSurrender: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnSurrender: player is invalid");
            return;
        }

        if (player.PlayerPawn == null)
        {
            logger.LogError("OnSurrender: PlayerPawn is null for player {PlayerName}", player.Name);
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Match && matchState != MatchState.KnifeRound)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "surrender"]);
            return;
        }

        var team = (Team)player.PlayerPawn.TeamNum;

        if (team == Team.CT)
        {
            StartSurrenderVote(player, Team.CT);
        }
        else if (team == Team.T)
        {
            StartSurrenderVote(player, Team.T);
        }
        else
        {
            PrintMessageToPlayer(player, Core.Localizer["error.not_in_team"]);
        }
    }
}
