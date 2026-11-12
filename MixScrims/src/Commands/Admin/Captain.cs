using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

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

        if (!CanOpenCaptainPicker(admin))
            return;

        // The bare (arg-less) form is only legal while built-in menus are suppressed:
        // there the consumer plugin renders both the side and the player picker.
        string? team = null;
        if (context.Args.Length >= 1)
        {
            team = context.Args[0].ToLower();
            if (team != "t" && team != "ct")
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
                return;
            }
        }
        else if (!suppressBuiltInMenus)
        {
            PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
            return;
        }

        if (!HasCaptainCandidates(admin))
            return;

        if (suppressBuiltInMenus)
        {
            var requesterSid = SafeSteamId(admin);
            if (requesterSid != 0)
                mixScrimsService.RaiseCaptainMenuRequested(requesterSid, team == null ? null : (team == "ct" ? Team.CT : Team.T));
            return;
        }

        if (IsPlayerValid(admin))
        {
            BuildCaptainPickMenu(team == "ct" ? Team.CT : Team.T, parent: null).Open(admin);
        }
    }

    /// <summary>The <c>!captain</c> checks that do not depend on its argument, replying to the admin on a refusal.</summary>
    internal bool CanOpenCaptainPicker(IPlayer admin)
    {
        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("OnCaptain: Captains are disabled in configuration.");
            PrintMessageToPlayer(admin, Core.Localizer["error.captain.disabled"]);
            return false;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();
        if (IsCaptainPickerState(matchState))
            return true;

        logger.LogError("OnCaptain: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
        PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "captain"]);
        return false;
    }

    internal static bool IsCaptainPickerState(MatchState state) =>
        state is MatchState.Warmup or MatchState.MapLoading or MatchState.MapChosen;

    private bool HasCaptainCandidates(IPlayer admin)
    {
        if (GetCaptainCandidates().Count > 0)
            return true;

        logger.LogWarning("OnCaptain: No eligible players to pick as captain");
        PrintMessageToPlayer(admin, "No eligible players available.");
        return false;
    }

    private List<IPlayer> GetCaptainCandidates()
    {
        // Every connected player is eligible; already holding a captain slot is the
        // only disqualifier. GetPlayingPlayers() was too narrow — it drops anyone in
        // Spectator/unassigned, who is a legal captain target (PickCtCaptain has no
        // team requirement, and PickRandomCaptain deliberately considers spectators).
        // It also gated the empty-list check, so a lobby that had not picked teams
        // yet aborted the command outright, including the consumer-menu path.
        var players = GetPlayers();
        // Compare by PlayerID (unique per slot), NOT SteamID — bots all share
        // SteamID = 0, so a SteamID compare would wipe every bot from the
        // eligible list the moment either captain is a bot.
        players.RemoveAll(p => captainCt?.PlayerID == p.PlayerID || captainT?.PlayerID == p.PlayerID);
        return players;
    }

    /// <summary>
    /// The captain page filling <paramref name="team"/> (its <c>IMenu.Tag</c>): two slot rows, tagged
    /// with their <see cref="Team"/>, reopen it for either side; player rows are tagged with their slot.
    /// </summary>
    internal IMenu BuildCaptainPickMenu(Team team, IMenu? parent)
    {
        var builder = Core.Menu
            .CreateMenu("mixscrims.captain-pick")
            .AddHeader(new TitleComponent(Core.Localizer["menu.captain_pick", team == Team.CT ? "CT" : "T"]) { Overflow = MenuTextOverflow.None })
            .Add(BuildCaptainSlotRow(Team.CT, parent))
            .Add(BuildCaptainSlotRow(Team.T, parent));
        if (parent != null)
            builder.WithParent(parent);

        foreach (var player in GetCaptainCandidates())
        {
            var displayName = player.Name ?? $"#{player.PlayerID}";
            var pickedSlot = player.PlayerID;
            var pickedSteamId = SafeSteamId(player);
            builder.Add(new ButtonComponent(displayName, ctx =>
            {
                if (team == Team.CT)
                    SetCtCaptain(ctx.Player, displayName, pickedSlot, pickedSteamId);
                else
                    SetTCaptain(ctx.Player, displayName, pickedSlot, pickedSteamId);
                return ValueTask.CompletedTask;
            }) { Tag = pickedSlot });
        }

        var menu = builder.Build();
        menu.Tag = team;
        return menu;
    }

    private ButtonComponent BuildCaptainSlotRow(Team slotTeam, IMenu? parent)
    {
        var holder = slotTeam == Team.CT ? captainCt : captainT;
        var holderName = IsPlayerValid(holder) ? SafePlayerName(holder) : Core.Localizer["menu.captain_slot.empty"];

        return new ButtonComponent(
            Core.Localizer[slotTeam == Team.CT ? "menu.captain_slot.ct" : "menu.captain_slot.t", holderName],
            ctx =>
            {
                // Reopening is opening the page again, so it passes the same checks `!captain` does.
                var admin = ctx.Player;
                if (!suppressBuiltInMenus && CanOpenCaptainPicker(admin) && HasCaptainCandidates(admin))
                    BuildCaptainPickMenu(slotTeam, parent).Open(admin);
                return ValueTask.CompletedTask;
            },
            slotTeam == Team.CT ? "slot-ct" : "slot-t") { Tag = slotTeam };
    }
}
