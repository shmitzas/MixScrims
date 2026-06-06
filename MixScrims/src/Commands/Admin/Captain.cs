using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Prompts a list of players to choose a captain for chosen team
    ///</summary>
    [Command("captain", false, "managemix", HelpText = "Manually selects a captain for the chosen team. Usage: !captain <t/ct>")]
    public void OnCaptain(ICommandContext context)
    {
        var admin = context.Sender;
        if (admin == null || !context.IsSentByPlayer)
        {
            logger.LogError("Console cannot set captain, only a live player can");
            return;
        }

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("OnCaptain: Captains are disabled in configuration.");
            PrintMessageToPlayer(admin, Core.Localizer["error.captain.disabled"]);
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup
            || matchState == MatchState.MapLoading
            || matchState == MatchState.MapChosen)
        {
            if (context.Args.Length < 1)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
                return;
            }

            var team = context.Args[0].ToLower();
            if (team != "t" && team != "ct")
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
                return;
            }

            var players = GetPlayingPlayers();
            players.RemoveAll(p => captainCt?.PlayerID == p.PlayerID || captainT?.PlayerID == p.PlayerID);

            if (players.Count == 0)
            {
                logger.LogWarning("OnCaptain: No eligible players to pick as captain");
                PrintMessageToPlayer(admin, "No eligible players available.");
                return;
            }

            var builder = Core.MenusAPI
                .CreateBuilder()
                .Design.SetMenuTitle(Core.Localizer["menu.captain_pick", team.ToUpper()])
                .Design.SetMenuTitleVisible(true)
                .Design.SetMenuFooterVisible(true)
                .EnableSound()
                .SetPlayerFrozen(false)
                .SetAutoCloseDelay(0);

            foreach (var player in players)
            {
                var displayName = player.Name ?? $"#{player.PlayerID}";
                var button = new ButtonMenuOption(displayName);

                if (team == "t")
                {
                    button.Click += async (sender, args) =>
                    {
                        SetTCaptain(admin, displayName);
                        await ValueTask.CompletedTask;
                    };
                }
                if (team == "ct")
                {
                    button.Click += async (sender, args) =>
                    {
                        SetCtCaptain(admin, displayName);
                        await ValueTask.CompletedTask;
                    };
                }

                builder.AddOption(button);
            }

            var menu = builder.Build();
            if (IsPlayerValid(admin))
            {
                Core.MenusAPI.OpenMenuForPlayer(admin, menu);
            }
        }
        else
        {
            logger.LogError("OnCaptain: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
            PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "captain"]);
        }
    }
}
