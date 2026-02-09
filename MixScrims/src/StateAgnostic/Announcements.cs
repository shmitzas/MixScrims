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

        var notReadyPlayers = GetNotReadyPlayers();
        if (cfg.DetailedLogging)
            logger.LogInformation($"Not ready players count: {notReadyPlayers.Count}");

        if (notReadyPlayers.Count > 0)
        {
            string notReadyPlayersNames = string.Join(", ", notReadyPlayers.Select(p => p.Controller.PlayerName));
            if (cfg.DetailedLogging)
                logger.LogInformation($"Not ready players: {notReadyPlayersNames}");
            PrintMessageToAllPlayers(Core.Localizer["announcement.ready_status", readyPlayers.Count, GetNumberOfPlayersRequiredToStart()]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.not_ready_players", notReadyPlayersNames]);
        }

        if (cfg.ShowReadyStatusInScoreboard)
        {
            ShowReadyAndNotReadyPlayersInScoreboard();
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
            SetPlayerReadyStatusInScoreboard(player, true);
        }

        foreach(var player in notReadyPlayers)
        {
            SetPlayerReadyStatusInScoreboard(player, false);
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
            Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
        }
        catch (Exception ex)
        {
            if (cfg.DetailedLogging)
                logger.LogError(ex, "SetPlayerReadyStatusInScoreboard: Failed to update clan tag for player {PlayerName}. Most likely player left the server while setting clan tag.", player?.Controller?.PlayerName ?? $"Slot: {player?.Slot}");
        }
    }

    /// <summary>
    /// Prints command reminders to all players, cycling through all available reminders.
    /// </summary>
    internal void PrintCommandReminders()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("PrintCommandReminders");
        var reminders = cfg.CommandRemindersLocalization;
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

        if (captainCt != null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"Captain CT: {captainCt.Controller.PlayerName}");
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.chosen.ct", captainCt.Controller.PlayerName]);
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Captain CT: Not chosen");
            PrintMessageToAllPlayers(Core.Localizer["announcement.captain.not_chosen.ct"]);
        }

        if (captainT != null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"Captain T: {captainT.Controller.PlayerName}");
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
        var playersToStart = GetNumberOfPlayersRequiredToStart();
        var readyMessage = Core.Localizer["info.center.ready_players_counter", readyPlayers.Count, playersToStart];
        var matchStartRequirements = Core.Localizer["info.center.match_start_requirements"];
        var message = $"{readyMessage}<br>{matchStartRequirements}";
        Core.PlayerManager.SendCenterHTML(message, displayLenght);
    }
}
