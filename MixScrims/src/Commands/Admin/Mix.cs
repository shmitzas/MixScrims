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
    ///Opens the match control panel
    ///</summary>
    [Command("mix", false, "managemix", HelpText = "Opens the match control panel. Usage: !mix")]
    public void OnMixControl(ICommandContext context)
    {
        var admin = context.Sender;
        if (admin == null || !context.IsSentByPlayer)
        {
            logger.LogWarning("OnMixControl: the control panel is a menu, so only a player can open it.");
            return;
        }

        // Opening replaces the admin's open menu, and the pick and side menus deliberately have no close.
        if (suppressBuiltInMenus
            || (Core.Menu.GetSession(admin)?.Menu.Id is "mixscrims.team-pick" or "mixscrims.knife-side-pick"))
        {
            PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "mix"]);
            return;
        }

        if (IsPlayerValid(admin))
        {
            BuildControlMenu().Open(admin);
        }
    }

    /// <summary>The control panel. A renderer keys on its component ids, which makes them a contract.</summary>
    internal IMenu BuildControlMenu() => Core.Menu
        .CreateMenu("mixscrims.control")
        .AddHeader(new TitleComponent(Core.Localizer["menu.control"]) { Overflow = MenuTextOverflow.None })
        // The header is never scanned for focus, which is what keeps this line unselectable.
        .AddHeader(new ControlStatusLine(ControlStatusText) { Overflow = MenuTextOverflow.None })
        .Add(ControlButton("start", "menu.control.start", admin => ForceMatchStartByAdmin(admin, sentByPlayer: true, deferred: true), () => IsForceStartState(MatchState)))
        .Add(ControlToggle("hold", "menu.control.hold", () => phaseProgressionHeld, SetHoldFromPanel, CanToggleHold))
        .Add(ControlButton("reset", "menu.control.reset", ResetMixByAdmin))
        .Add(ControlButton("force-ready", "menu.control.force_ready", admin => ForceReadyByAdmin(admin, deferred: true), () => IsForceReadyState(MatchState)))
        .Add(ControlButton("force-unready", "menu.control.force_unready", ForceUnreadyByAdmin, () => IsForceReadyState(MatchState)))
        .Add(new SubmenuComponent(Core.Localizer["menu.control.captains"], BuildCaptainsFromControl, "captains")
        {
            EnabledWhen = _ => !cfg.DisableCaptains && IsCaptainPickerState(MatchState),
        })
        .Add(ControlToggle("lock-joining", "menu.control.lock_joining", () => preventNotPickedPlayersFromJoiningOngoingMatch, mixScrimsService.PreventNewPlayersJoining))
        .Add(ControlButton("kick-spectators", "menu.control.kick_spectators", KickSpectatorsByAdmin, CanKickSpectators))
        .Add(new SubmenuComponent(Core.Localizer["menu.control.change_map"], _ => suppressBuiltInMenus ? null : BuildChangeMapMenu(), "change-map"))
        .Add(new SubmenuComponent(Core.Localizer["menu.control.map_pool"], _ => suppressBuiltInMenus ? null : BuildMapPoolMenu(), "map-pool"))
        .Build();

    private ButtonComponent ControlButton(string id, string labelKey, Action<IPlayer> action, Func<bool>? enabledWhen = null) =>
        new(Core.Localizer[labelKey], ctx =>
        {
            action(ctx.Player);
            // Most actions land a tick later; redraw then so the status line catches up.
            Core.Scheduler.NextTick(ctx.Session.Invalidate);
            return ValueTask.CompletedTask;
        }, id)
        {
            EnabledWhen = enabledWhen == null ? null : _ => enabledWhen(),
        };

    private LiveToggle ControlToggle(string id, string labelKey, Func<bool> current, Action<bool> apply, Func<bool>? enabledWhen = null) =>
        new(Core.Localizer[labelKey], current, apply, id)
        {
            EnabledWhen = enabledWhen == null ? null : _ => enabledWhen(),
        };

    // A plugin's hold is that plugin's to release. The panel's own is releasable anywhere, but switched on only where
    // releasing lets the match carry on: one that reaches the end of team picking or of the match parks it until a reset.
    private bool CanToggleHold() => phaseProgressionHeld
        ? phaseProgressionHeldByPanel
        : MatchState is MatchState.Warmup or MatchState.MapChosen or MatchState.KnifeRound;

    // Guarded again: a click flips the session's value, which can trail a plugin's release by a tick. No plugin follows
    // up a panel hold's release, so the ready check re-runs, a tick late: the click sound reads IsEnabled afterwards.
    private void SetHoldFromPanel(bool held)
    {
        if (!held && !phaseProgressionHeldByPanel)
            return;

        mixScrimsService.SetPhaseProgressionHeld(held);
        phaseProgressionHeldByPanel = held;
        if (!held && MatchState is MatchState.Warmup or MatchState.MapChosen)
            Core.Scheduler.NextTick(CheckReadyPlayersToStart);
    }

    // DefaultValueProvider seeds a session once, so an open panel would miss a change made elsewhere.
    // Synced in NeedsRedraw, not Render: a component renderer registered for ToggleComponent replaces Render.
    private sealed class LiveToggle : ToggleComponent
    {
        private sealed class Shown
        {
            public bool Enabled;
        }

        private readonly Func<bool> current;

        public LiveToggle(string text, Func<bool> current, Action<bool> apply, string id)
            : base(text, onChanged: c => apply(c.NewValue), id: id)
        {
            this.current = current;
            DefaultValueProvider = _ => current();
        }

        public override bool NeedsRedraw(IMenuSession session, DateTime now)
        {
            var live = current();
            // Enabled too: a plugin taking over the panel's hold greys the row without changing its value.
            var enabled = IsEnabled(session);
            var shown = session.GetState<Shown>(this);
            if (GetValue(session) == live && shown.Enabled == enabled)
                return base.NeedsRedraw(session, now);

            // Into the state directly: SetValue would fire OnChanged and re-apply the value.
            session.GetState<MenuValueState<bool>>(this).Value = live;
            shown.Enabled = enabled;
            return true;
        }
    }

    // TextComponent runs TextProvider every tick yet, with Overflow.None, never asks for a redraw.
    // ponytail: re-checks once a second; invalidating open panels on MatchStateChanged / PlayerReadyChanged would be exact.
    private sealed class ControlStatusLine : TextComponent
    {
        private sealed class Shown
        {
            public string? Text;
            public DateTime NextCheck;
        }

        private readonly Func<string> build;

        public ControlStatusLine(Func<string> build) : base(id: "status")
        {
            this.build = build;
            TextProvider = session => session.GetState<Shown>(this).Text = build();
        }

        public override bool NeedsRedraw(IMenuSession session, DateTime now)
        {
            var shown = session.GetState<Shown>(this);
            if (now < shown.NextCheck) return false;
            shown.NextCheck = now.AddSeconds(1);
            return build() != shown.Text;
        }
    }

    private string ControlStatusText()
    {
        var state = MatchState;
        var phase = Core.Localizer[$"menu.control.phase.{state.ToString().ToLowerInvariant()}"];
        // A ready count means something only while players are readying up.
        return state is MatchState.Warmup or MatchState.MapChosen
            ? Core.Localizer["menu.control.status", phase, GetEffectiveReadyCount(), GetNumberOfPlayersRequiredToStart()]
            : phase;
    }

    private IMenu? BuildCaptainsFromControl(IMenuSession session)
    {
        var admin = session.Player;
        if (suppressBuiltInMenus || !CanOpenCaptainPicker(admin) || !HasCaptainCandidates(admin))
            return null;

        var team = captainCt != null && captainT == null ? Team.T : Team.CT;
        return BuildCaptainPickMenu(team, session.Menu);
    }

    // Sealed with a human on them: KickNotPlayingPlayers keeps roster SteamIDs, and every bot reads 0.
    private bool CanKickSpectators() =>
        teamPickingFinalized && mixScrimsService.GetPlayingCtPlayers().Count + mixScrimsService.GetPlayingTPlayers().Count > 0;

    private void KickSpectatorsByAdmin(IPlayer admin)
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("Control panel: {Admin} kicked every other player outside the playing rosters.", IsPlayerValid(admin) ? SafePlayerName(admin) : "<gone>");
        mixScrimsService.KickNotPlayingPlayers(Core.Localizer["info.kick_reason.not_picked"], SafeSteamId(admin));
    }

    /// <summary>Every configured map; a row tagged with its <see cref="MapPoolEntry"/> loads it down the <c>!map</c> path.</summary>
    internal IMenu BuildChangeMapMenu()
    {
        var builder = Core.Menu
            .CreateMenu("mixscrims.change-map")
            .AddHeader(new TitleComponent(Core.Localizer["menu.control.change_map"]) { Overflow = MenuTextOverflow.None });

        foreach (var entry in mixScrimsService.GetConfiguredMapPool())
        {
            builder.Add(new ButtonComponent(entry.Name, ctx =>
            {
                // Resolved on click, as `!map` resolves its argument: maps.jsonc may have reloaded since.
                // By CanonicalId alone, since GetMapByName would also match another entry's Name.
                var map = mapsConfig.Maps.FirstOrDefault(m =>
                    m != null && string.Equals(m.CanonicalId, entry.Id, StringComparison.OrdinalIgnoreCase));
                if (map == null)
                {
                    logger.LogWarning("Control panel: {Map} left maps.jsonc before it could be loaded.", entry.Id);
                    PrintMessageToPlayer(ctx.Player, Core.Localizer["error.map_not_found", entry.Name]);
                    return ValueTask.CompletedTask;
                }

                ChangeMapByAdmin(ctx.Player, map);
                return ValueTask.CompletedTask;
            }) { Tag = entry, CloseOnSelect = true });
        }

        return builder.Build();
    }

    /// <summary>Every configured map, read-only, each row tagged with its <see cref="MapPoolEntry"/>.</summary>
    internal IMenu BuildMapPoolMenu()
    {
        var builder = Core.Menu
            .CreateMenu("mixscrims.map-pool")
            .AddHeader(new TitleComponent(Core.Localizer["menu.control.map_pool"]) { Overflow = MenuTextOverflow.None });

        foreach (var entry in mixScrimsService.GetConfiguredMapPool())
            builder.Add(new TextComponent(entry.Name == entry.Id ? entry.Name : $"{entry.Name} ({entry.Id})") { Tag = entry });

        return builder.Build();
    }
}
