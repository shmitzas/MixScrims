using Microsoft.Extensions.Logging;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    List<string> usedReminders = [];

    /// <summary>
    /// Prints ready and not ready players to in-game chat.
    /// </summary>
    private void PrintReadyAndNotReadyPlayers()
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
    }

    /// <summary>
    /// Prints command reminders to all players, cycling through all available reminders.
    /// </summary>
    private void PrintCommandReminders()
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
    private void PrintChosenCaptains()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("PrintChosenCaptains");

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
}
