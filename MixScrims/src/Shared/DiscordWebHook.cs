using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MixScrims;

partial class MixScrims
{
    /// <summary>
    /// Sends a message to all configured Discord webhooks.
    /// </summary>
    public async Task SendToDiscord(string message, string webhook)
    {
        try
        {
            StringContent content = FormatPayload(message);
            if (content is null)
            {
                logger.LogError($"Error in MixScrims sending request to Discord. Message \"{message}\" was not converted to \"StringContent\"");
                return;
            }
            using var httpClient = new HttpClient();
            var response = await httpClient.PostAsync(webhook, content);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError($"Error in MixScrims sending request to Discord. Status Code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                logger.LogError($"Message: {message}");
                logger.LogError($"Webhook URL: {webhook}");
            }
            else
            {
                if (cfg.DetailedLogging)
                {
                    logger.LogInformation("Successfully sent message to Discord webhook.");
                    logger.LogInformation($"Message: {message}");
                    logger.LogInformation($"Webhook URL: {webhook}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Error in MixScrims sending request to Discord: {ex}");
        }
    }

    /// <summary>
    /// Formats payload to be sent to Discord webhook.
    /// </summary>
    /// <returns>Formatted payload as StringContent</returns>
    internal static StringContent FormatPayload(string message)
    {
        var payload = new
        {
            content = message
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        return new StringContent(jsonPayload, Encoding.UTF8, "application/json");
    }
}
