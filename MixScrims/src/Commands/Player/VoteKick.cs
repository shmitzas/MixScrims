using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Initiates a vote to kick a player. By default the vote is put to the caller's team during
    /// a match and needs every other teammate to vote YES; a consumer can switch it server-wide,
    /// where it runs in any state and passes on a ratio of the players present.
    /// </summary>
    [Command("votekick", false, "", HelpText = "Starts a vote to kick a player. Usage: !votekick <player name>")]
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

        var serverWide = serverWideVoteKick;

        var matchState = mixScrimsService.GetCurrentMatchState();
        var stateAllows = serverWide ? IsVoteKickAllowedInState(matchState) : matchState == MatchState.Match;
        if (!stateAllows)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.invalid_state", "votekick"]);
            return;
        }

        var callerTeam = Team.None;
        if (!serverWide)
        {
            if (caller.PlayerPawn == null)
            {
                logger.LogError("OnVoteKick: caller PlayerPawn is null");
                return;
            }

            callerTeam = (Team)caller.PlayerPawn.TeamNum;
            if (callerTeam != Team.CT && callerTeam != Team.T)
            {
                PrintMessageToPlayer(caller, Core.Localizer["error.not_in_team"]);
                return;
            }
        }

        if (context.Args.Length < 1)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.missing_args"]);
            return;
        }

        var targetName = string.Join(" ", context.Args);
        var target = FindTargetByQuery(caller, targetName, out var ambiguousTarget);
        if (ambiguousTarget)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.ambiguous_target", targetName]);
            return;
        }

        if (target == null || !IsPlayerValid(target))
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.invalid_target", targetName]);
            return;
        }

        if (target.SteamID == caller.SteamID)
        {
            PrintMessageToPlayer(caller, Core.Localizer["command.votekick.self"]);
            return;
        }

        if (!serverWide)
        {
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
        }

        if (GetVoteKickSlot(callerTeam).InProgress)
        {
            PrintMessageToPlayer(caller, Core.Localizer[
                serverWide ? "command.votekick.vote_in_progress.server" : "command.votekick.vote_in_progress"]);
            return;
        }

        StartVoteKick(caller, target, callerTeam);
    }
}
