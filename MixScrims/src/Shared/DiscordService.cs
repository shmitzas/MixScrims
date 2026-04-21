using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MixScrims;

partial class MixScrims
{
    // Shared HttpClient instance - reusing avoids socket exhaustion from per-request creation
    private static readonly HttpClient _discordHttpClient = new();

    /// <summary>
    /// Sends a Discord invite with full embed support to the configured webhook.
    /// </summary>
    public async Task SendToDiscord(DiscordInvite invite)
    {
        try
        {
            StringContent content = FormatPayload(invite);
            if (content is null)
            {
                logger.LogError("Error in MixScrims sending request to Discord. Invite was not converted to StringContent");
                return;
            }
            var response = await _discordHttpClient.PostAsync(invite.WebhookUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Discord webhook request failed. Status: {StatusCode}, Reason: {Reason}", response.StatusCode, response.ReasonPhrase);
            }
            else if (cfg.DetailedLogging)
            {
                logger.LogInformation("Successfully sent message to Discord webhook");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending request to Discord webhook");
        }
    }

    /// <summary>
    /// Formats payload to be sent to Discord webhook with embed support.
    /// </summary>
    /// <returns>Formatted payload as StringContent</returns>
    internal static StringContent FormatPayload(DiscordInvite invite)
    {
        var payload = new Dictionary<string, object>();

        // Add username and avatar if provided
        if (!string.IsNullOrEmpty(invite.Username))
            payload["username"] = invite.Username;

        if (!string.IsNullOrEmpty(invite.AvatarUrl))
            payload["avatar_url"] = invite.AvatarUrl;

        // Add content if provided
        if (!string.IsNullOrEmpty(invite.Content))
            payload["content"] = invite.Content;

        // Add embed if it has content
        if (invite.Embed != null && HasEmbedContent(invite.Embed))
        {
            var embed = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(invite.Embed.Title))
                embed["title"] = invite.Embed.Title;

            if (!string.IsNullOrEmpty(invite.Embed.Description))
                embed["description"] = invite.Embed.Description;

            if (!string.IsNullOrEmpty(invite.Embed.Color) &&
                int.TryParse(invite.Embed.Color, System.Globalization.NumberStyles.HexNumber, null, out var colorInt))
                embed["color"] = colorInt;

            if (!string.IsNullOrEmpty(invite.Embed.Footer))
            {
                embed["footer"] = new Dictionary<string, string>
                {
                    ["text"] = invite.Embed.Footer
                };
            }

            if (invite.Embed.Fields != null && invite.Embed.Fields.Count > 0)
            {
                embed["fields"] = invite.Embed.Fields
                    .Where(f => !string.IsNullOrEmpty(f.Name) || !string.IsNullOrEmpty(f.Value))
                    .Select(f => new Dictionary<string, object>
                    {
                        ["name"] = f.Name,
                        ["value"] = f.Value,
                        ["inline"] = f.Inline
                    })
                    .ToList();
            }

            payload["embeds"] = new[] { embed };
        }

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var jsonPayload = JsonSerializer.Serialize(payload, options);
        return new StringContent(jsonPayload, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Checks if an embed has any content worth sending
    /// </summary>
    private static bool HasEmbedContent(Embed embed)
    {
        return !string.IsNullOrEmpty(embed.Title) ||
               !string.IsNullOrEmpty(embed.Description) ||
               !string.IsNullOrEmpty(embed.Footer) ||
               (embed.Fields != null && embed.Fields.Count > 0);
    }

    /// <summary>
	/// Replaces placeholders in Discord invite with actual values
	/// </summary>
	private DiscordInvite ReplaceInvitePlaceholders(DiscordInvite invite, int remainingPlayers)
    {
        var replacedInvite = new DiscordInvite
        {
            WebhookUrl = invite.WebhookUrl,
            Username = invite.Username,
            AvatarUrl = invite.AvatarUrl,
            Content = invite.Content.Replace("{0}", remainingPlayers.ToString()),
            Embed = new Embed
            {
                Title = invite.Embed.Title.Replace("{0}", remainingPlayers.ToString()),
                Description = invite.Embed.Description.Replace("{0}", remainingPlayers.ToString()),
                Footer = invite.Embed.Footer.Replace("{0}", remainingPlayers.ToString()),
                Color = invite.Embed.Color,
                Fields = invite.Embed.Fields.Select(f => new EmbedField
                {
                    Name = f.Name.Replace("{0}", remainingPlayers.ToString()),
                    Value = f.Value.Replace("{0}", remainingPlayers.ToString()),
                    Inline = f.Inline
                }).ToList()
            }
        };

        return replacedInvite;
    }
}
