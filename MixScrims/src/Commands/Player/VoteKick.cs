using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Initiates a vote to kick a teammate from the match. All other teammates must vote YES for the kick to pass.
    /// </summary>
    [Command("votekick", true, "", HelpText = "Starts a unanimous vote to kick a teammate from the match. Usage: !votekick")]
    public void OnVoteKick(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnVoteKick: command can only be used by players");
            return;
        }

        var caller = context.Sender;
        if (caller == null || !IsPlayerValid(caller))
        {
            logger.LogError("OnVoteKick: caller is invalid");
            return;
        }

        if (!cfg.VoteKick.Enabled)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.disabled"]);
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Match)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.invalid_state", "votekick"]);
            return;
        }

        if (caller.PlayerPawn == null)
        {
            logger.LogError("OnVoteKick: caller PlayerPawn is null");
            return;
        }

        var callerTeam = (Team)caller.PlayerPawn.TeamNum;
        if (callerTeam != Team.CT && callerTeam != Team.T)
        {
            PrintMessageToPlayer(caller, Core.Localizer["error.not_in_team"]);
            return;
        }

        if (context.Args.Length < 1)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.missing_args"]);
            return;
        }

        var targetName = string.Join(" ", context.Args);
        var target = GetPlayerByName(targetName);
        if (target == null || !IsPlayerValid(target))
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.invalid_target", targetName]);
            return;
        }

        if (target.PlayerID == caller.PlayerID)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.self"]);
            return;
        }

        if (target.PlayerPawn == null)
        {
            logger.LogError("OnVoteKick: target PlayerPawn is null");
            return;
        }

        var targetTeam = (Team)target.PlayerPawn.TeamNum;
        if (targetTeam != callerTeam)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.not_teammate"]);
            return;
        }

        bool voteInProgress = callerTeam == Team.CT ? isVoteKickInProgressCt : isVoteKickInProgressT;
        if (voteInProgress)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.vote_in_progress"]);
            return;
        }

        StartVoteKick(caller, target, callerTeam);
    }
}
