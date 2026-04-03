using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Sends an invite message to the discord webhook
    /// </summary>
    [Command("invite")]
    public void OnInvite(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
        {
            logger.LogError("OnInvite: command can only be used by players");
            return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
        {
            logger.LogError("OnInvite: player is invalid");
            return;
        }

        if (!discordConfig.EnableDiscordInvites)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invite.disabled"]);
            return;
        }

        if (discordConfig.Invites.Count == 0)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invite.no_webhooks"]);
            return;
        }

        var timeSinceLastInvite = DateTime.Now - lastDiscordInviteSentAt;
        var timeRemaining = TimeSpan.FromMinutes(discordConfig.DiscordInviteDelayMinutes) - timeSinceLastInvite;

        if (timeRemaining > TimeSpan.Zero)
        {
            int minutes = (int)timeRemaining.TotalMinutes;
            int seconds = timeRemaining.Seconds;
            string formattedTime = $"{minutes}min {seconds}s";

            PrintMessageToPlayer(player, Core.Localizer["command.invite.to_early", formattedTime]);
            return;
        }

        int playingPlayers = GetPlayers().Count;
        int remainingPlayers = cfg.MinimumReadyPlayers - playingPlayers;

        if (remainingPlayers < 1)
        {
            PrintMessageToPlayer(player, Core.Localizer["command.invite.no_need", playingPlayers, cfg.MinimumReadyPlayers]);
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var invite in discordConfig.Invites)
            {
                var inviteWithReplacements = ReplaceInvitePlaceholders(invite, remainingPlayers);
                await SendToDiscord(inviteWithReplacements);
            }
        });

        lastDiscordInviteSentAt = DateTime.Now;
        PrintMessageToAllPlayers(Core.Localizer["command.invite", player.Controller.PlayerName]);
    }
}
