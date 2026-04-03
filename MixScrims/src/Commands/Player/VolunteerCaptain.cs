using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Handles a player's request to volunteer as a team captain for the current match.
    /// </summary>
    public void OnCaptainVolunteer(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnCaptainVolunteer: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnCaptainVolunteer: player is invalid");
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("OnCaptainVolunteer: Captains are disabled in configuration.");
            PrintMessageToPlayer(player, Core.Localizer["error.captain.disabled"]);
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup
            || matchState == MatchState.MapLoading
            || matchState == MatchState.MapChosen)
        {
            if (context.Args.Length < 1)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
                return;
            }

            var team = context.Args[0].ToLower();
            if (team != "t" && team != "ct")
            {
                PrintMessageToPlayer(player, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
                return;
            }

            if (!cfg.AllowVolunteerCaptains)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.captain.volunteering_disabled"]);
                return;
            }
            if (captainCt != null && captainT != null)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.captains_already_chosen"]);
                return;
            }
            if (captainCt != null && captainCt.PlayerID == player.PlayerID)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.already_captain.ct"]);
                return;
            }
            if (captainT != null && captainT.PlayerID == player.PlayerID)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.already_captain.t"]);
                return;
            }

            if (team == "ct" && captainCt == null)
                PickCtCaptain(player);

            if (team == "t" && captainT == null)
                PickTCaptain(player);
        }
        else
        {
            logger.LogError("OnCaptainVolunteer: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "captain"]);
        }
    }
}
