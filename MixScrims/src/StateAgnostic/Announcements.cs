using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    List<string> usedReminders = [];

    /// <summary>
    /// Prints ready and not ready players to in-game chat.
    /// </summary>
    internal void PrintReadyAndNotReadyPlayers()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("PrintReadyAndNotReadyPlayers");

        if (cfg.ShowReadyStatusInChat)
        {
            ShowReadyAndNotReadyPlayersInChat();
        }
       
        if (cfg.ShowReadyStatusInScoreboard)
        {
            ShowReadyAndNotReadyPlayersInScoreboard();
        }
    }

    internal void ShowReadyAndNotReadyPlayersInChat()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("ShowReadyAndNotReadyPlayersInChat");

        var notReadyPlayers = GetNotReadyPlayers();
        if (cfg.DetailedLogging)
            logger.LogInformation("Not ready players count: {Count}", notReadyPlayers.Count);

        if (notReadyPlayers.Count > 0)
        {
            // Filter out invalid players before accessing Controller
            var validNotReadyPlayers = notReadyPlayers.Where(p => IsPlayerValid(p)).ToList();
            if (validNotReadyPlayers.Count > 0)
            {
                string notReadyPlayersNames = string.Join(", ", validNotReadyPlayers.Select(p => p.Controller.PlayerName));
                if (cfg.DetailedLogging)
                    logger.LogInformation("Not ready players: {Names}", notReadyPlayersNames);
                PrintMessageToAllPlayers(Core.Localizer["announcement.ready_status", GetEffectiveReadyCount(), GetNumberOfPlayersRequiredToStart()]);
                PrintMessageToAllPlayers(Core.Localizer["announcement.not_ready_players", notReadyPlayersNames]);
            }
        }
    }

    /// <summary>
    /// Displays a prefix for each ready and not ready players in the scoreboard.
    /// </summary>
    internal void ShowReadyAndNotReadyPlayersInScoreboard()
    {
        var notReadyPlayers = GetNotReadyPlayers();

        foreach(var player in readyPlayers)
        {
            if (IsPlayerValid(player))
            {
                SetPlayerReadyStatusInScoreboard(player, true);
            }
        }

        foreach(var player in notReadyPlayers)
        {
            if (IsPlayerValid(player))
            {
                SetPlayerReadyStatusInScoreboard(player, false);
            }
        }
    }

    /// <summary>
    /// Updates the player's clan tag in the scoreboard to reflect their ready or not ready status.
    /// </summary>
    internal void SetPlayerReadyStatusInScoreboard(IPlayer player, bool isReady)
    {
        try
        {
            if (player.IsFakeClient)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("SetPlayerReadyStatusInScoreboard: Skipping bot.");
                return;
            }

            var playerClanTag = player.Controller.Clan;
            if (isReady)
            {
                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.ready"]))
                {
                    return;
                }
                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.not_ready"]))
                {
                    playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.not_ready"], "").Trim();
                }
                playerClanTag = $"{Core.Localizer["info.clan_tag.ready"]} {playerClanTag}";
            }
            else
            {
                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.not_ready"]))
                {
                    return;
                }
                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.ready"]))
                {
                    playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.ready"], "").Trim();
                }
                playerClanTag = $"{Core.Localizer["info.clan_tag.not_ready"]} {playerClanTag}";
            }

            player.Controller.Clan = playerClanTag;
            player.Controller.ClanUpdated();
            if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
                Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SetPlayerReadyStatusInScoreboard: Failed to update clan tag for player {PlayerName}. Most likely player left the server while setting clan tag.", player?.Name ?? $"Slot: {player?.Slot}");
        }
    }

    /// <summary>
    /// Removes ready and not ready clan tags from all players.
    /// </summary>
    internal void RemoveReadyClanTagsFromAllPlayers()
    {
        var allPlayers = Core.PlayerManager.GetAllValidPlayers();
        
        foreach (var player in allPlayers)
        {
            if (!IsPlayerValid(player) || player.IsFakeClient)
                continue;

            try
            {
                var playerClanTag = player.Controller.Clan;
                var modified = false;

                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.ready"]))
                {
                    playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.ready"], "").Trim();
                    modified = true;
                }

                if (playerClanTag.Contains(Core.Localizer["info.clan_tag.not_ready"]))
                {
                    playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.not_ready"], "").Trim();
                    modified = true;
                }

                if (modified)
                {
                    player.Controller.Clan = playerClanTag;
                    player.Controller.ClanUpdated();
                    if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
                        Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RemoveReadyClanTagsFromAllPlayers: Failed to remove clan tag for player {PlayerName}.", player?.Name ?? $"Slot: {player?.Slot}");
            }
        }
    }

    // Captain clan tags — pulled from the translations file (info.clan_tag.captain_ct /
    // info.clan_tag.captain_t) so each language can localize the label. Accessed as
    // properties (not const) because the localizer needs the plugin instance.
    internal string CaptainCtClanTag => Core.Localizer["info.clan_tag.captain_ct"];
    internal string CaptainTClanTag => Core.Localizer["info.clan_tag.captain_t"];

    /// <summary>
    /// Prefixes the given player's clan tag with the captain tag for their team. Preserves
    /// whatever clan tag the player already has, so <see cref="RemoveCaptainClanTagFromPlayer"/>
    /// (or the sweep on match start / reset) restores the original tag on removal. Idempotent.
    /// </summary>
    internal void SetCaptainClanTag(IPlayer? player, Team team)
    {
        if (player == null || !IsPlayerValid(player) || player.IsFakeClient)
            return;

        var tag = team == Team.CT ? CaptainCtClanTag : CaptainTClanTag;
        var otherTag = team == Team.CT ? CaptainTClanTag : CaptainCtClanTag;

        try
        {
            var playerClanTag = player.Controller.Clan ?? string.Empty;
            var original = playerClanTag;

            // Strip the opposite captain tag if the player somehow had it (defensive - covers
            // an admin re-picking the same player for the other team).
            if (playerClanTag.Contains(otherTag))
                playerClanTag = playerClanTag.Replace(otherTag, "").Trim();
            // Strip ready/not-ready markers so the captain prefix ends up first.
            if (playerClanTag.Contains(Core.Localizer["info.clan_tag.ready"]))
                playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.ready"], "").Trim();
            if (playerClanTag.Contains(Core.Localizer["info.clan_tag.not_ready"]))
                playerClanTag = playerClanTag.Replace(Core.Localizer["info.clan_tag.not_ready"], "").Trim();

            if (!playerClanTag.Contains(tag))
                playerClanTag = string.IsNullOrEmpty(playerClanTag) ? tag : $"{tag} {playerClanTag}";

            if (playerClanTag == original)
                return;

            player.Controller.Clan = playerClanTag;
            player.Controller.ClanUpdated();
            if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
                Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SetCaptainClanTag: Failed to set captain clan tag for player {PlayerName}.", player?.Name ?? $"Slot: {player?.Slot}");
        }
    }

    /// <summary>
    /// Strips both captain clan tags from a single player. Used when a captain is being replaced
    /// mid-flow (admin re-pick, volunteer takeover) so the outgoing captain doesn't keep the tag.
    /// </summary>
    internal void RemoveCaptainClanTagFromPlayer(IPlayer? player)
    {
        if (player == null || !IsPlayerValid(player) || player.IsFakeClient)
            return;

        try
        {
            var playerClanTag = player.Controller.Clan ?? string.Empty;
            var modified = false;

            if (playerClanTag.Contains(CaptainCtClanTag))
            {
                playerClanTag = playerClanTag.Replace(CaptainCtClanTag, "").Trim();
                modified = true;
            }
            if (playerClanTag.Contains(CaptainTClanTag))
            {
                playerClanTag = playerClanTag.Replace(CaptainTClanTag, "").Trim();
                modified = true;
            }

            if (!modified)
                return;

            player.Controller.Clan = playerClanTag;
            player.Controller.ClanUpdated();
            if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
                Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RemoveCaptainClanTagFromPlayer: Failed to remove captain clan tag for player {PlayerName}.", player?.Name ?? $"Slot: {player?.Slot}");
        }
    }

    /// <summary>
    /// Sweeps captain clan tags off every player. Called on transition into Match and on any
    /// reset/abort path so stale tags never survive across matches.
    /// </summary>
    internal void RemoveCaptainClanTagsFromAllPlayers()
    {
        var allPlayers = Core.PlayerManager.GetAllValidPlayers();

        foreach (var player in allPlayers)
        {
            if (!IsPlayerValid(player) || player.IsFakeClient)
                continue;

            RemoveCaptainClanTagFromPlayer(player);
        }
    }

    /// <summary>
    /// Prints command reminders to all players, cycling through all available reminders.
    /// Reminders listed in <see cref="MainConfig.HideCommandRemindersDuringPhases"/> for the
    /// current match state are filtered out before cycling.
    /// </summary>
    internal void PrintCommandReminders()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("PrintCommandReminders");

        var currentStateName = mixScrimsService.GetCurrentMatchState().ToString();
        var reminders = cfg.CommandRemindersLocalization
            .Where(r => !IsReminderSuppressedInPhase(r, currentStateName))
            .ToList();

        if (reminders.Count == 0)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PrintCommandReminders: all reminders suppressed in phase {Phase}", currentStateName);
            return;
        }

        string? reminderToUse = reminders.FirstOrDefault(r => !usedReminders.Contains(r));

        if (reminderToUse == null)
        {
            usedReminders.Clear();
            reminderToUse = reminders.FirstOrDefault();
        }

        if (reminderToUse != null)
        {
            PrintMessageToAllPlayers(Core.Localizer[$"command_reminders.{reminderToUse}"]);
            usedReminders.Add(reminderToUse);
        }
    }

    private bool IsReminderSuppressedInPhase(string reminder, string phaseName)
    {
        if (!cfg.HideCommandRemindersDuringPhases.TryGetValue(reminder, out var phases) || phases.Count == 0)
            return false;
        return phases.Contains(phaseName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Announces the chosen captains for both teams to all players, if applicable.
    /// </summary>
    internal void PrintChosenCaptains()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("PrintChosenCaptains");

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PrintChosenCaptains: Captains are disabled in configuration.");
            return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.MapChosen)
        {
            return;
        }

        // Captains may have been set from references that became disposed during the map change;
        // drop those before touching .Controller below.
        EnsureCaptainsAlive();

        if (captainCt != null && IsPlayerValid(captainCt))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Captain CT: {PlayerName}", captainCt.Controller.PlayerName);
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.chosen.ct", captainCt.Controller.PlayerName]);
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Captain CT: Not chosen");
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.not_chosen.ct"]);
        }

        if (captainT != null && IsPlayerValid(captainT))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Captain T: {PlayerName}", captainT.Controller.PlayerName);
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.chosen.t", captainT.Controller.PlayerName]);
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Captain T: Not chosen");
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.not_chosen.t"]);
        }
    }

    internal void DisplayReadyAndNotReadyPlayersInCenterHtml(int displayLenght)
    {
        if (suppressBuiltInCenterHtml) return;

        var playersToStart = GetNumberOfPlayersRequiredToStart();
        var readyMessage = Core.Localizer["info.center.ready_players_counter", GetEffectiveReadyCount(), playersToStart];
        var matchStartRequirements = Core.Localizer["info.center.match_start_requirements"];
        var message = $"{readyMessage}<br>{matchStartRequirements}";

        if (cfg.HideReadyStatusInCenterWhenReady)
        {
            var playersToShow = GetNotReadyPlayers();
            if (playersToShow.Count > 0)
            {
                foreach (var player in playersToShow)
                {
                    if (IsPlayerValid(player))
                    {
                        player.SendCenterHTML(message, displayLenght);
                    }
                }
            }
            return;
        }

        Core.PlayerManager.SendCenterHTML(message, displayLenght);
    }
}
