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
    [Command("volunteer_captain", false, "", HelpText = "Volunteer to be a captain for the chosen team. Usage: !volunteer_captain <t/ct>")]
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
            // The bare (arg-less) form is only legal while built-in menus are suppressed:
            // there the consumer plugin renders the side picker, so the team is chosen in
            // its UI rather than on the command line.
            Team? side = null;
            if (context.Args.Length >= 1)
            {
                var teamArg = context.Args[0].ToLower();
                if (teamArg != "t" && teamArg != "ct")
                {
                    PrintMessageToPlayer(player, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
                    return;
                }
                side = teamArg == "ct" ? Team.CT : Team.T;
            }
            else if (!suppressBuiltInMenus)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
                return;
            }

            if (suppressBuiltInMenus)
            {
                var requesterSid = SafeSteamId(player);
                if (requesterSid != 0)
                    mixScrimsService.RaiseVolunteerCaptainMenuRequested(requesterSid, side);
                return;
            }

            TryVolunteerCaptain(player, side!.Value);
        }
        else
        {
            logger.LogError("OnCaptainVolunteer: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "captain"]);
        }
    }

    /// <summary>
    /// Runs the volunteer-captain eligibility chain and assigns the slot on success.
    /// Shared by the <c>!volunteer_captain</c> command and <c>IMixScrims.VolunteerAsCaptain</c>
    /// so both paths validate identically. Returns false (and messages the player) on rejection.
    /// </summary>
    internal bool TryVolunteerCaptain(IPlayer player, Team team)
    {
        if (!IsPlayerValid(player))
        {
            logger.LogWarning("TryVolunteerCaptain: player is invalid.");
            return false;
        }

        if (team != Team.CT && team != Team.T)
        {
            logger.LogWarning("TryVolunteerCaptain: unsupported team {Team}.", team);
            return false;
        }

        if (cfg.DisableCaptains)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.captain.disabled"]);
            return false;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (matchState != MatchState.Warmup
            && matchState != MatchState.MapLoading
            && matchState != MatchState.MapChosen)
        {
            logger.LogWarning("TryVolunteerCaptain: invalid match state {MatchState}.", matchState);
            PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "captain"]);
            return false;
        }

        if (!cfg.AllowVolunteerCaptains)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.captain.volunteering_disabled"]);
            return false;
        }
        if (captainCt != null && captainT != null)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.captains_already_chosen"]);
            return false;
        }

        var playerSteamId = SafeSteamId(player);
        if (captainCt != null && SafeSteamId(captainCt) == playerSteamId)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.already_captain.ct"]);
            return false;
        }
        if (captainT != null && SafeSteamId(captainT) == playerSteamId)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.already_captain.t"]);
            return false;
        }

        if (team == Team.CT)
        {
            if (captainCt != null)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.captains_already_chosen"]);
                return false;
            }
            PickCtCaptain(player);
            return true;
        }

        if (captainT != null)
        {
            PrintMessageToPlayer(player, Core.Localizer["error.captains_already_chosen"]);
            return false;
        }
        PickTCaptain(player);
        return true;
    }
}
