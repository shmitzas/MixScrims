namespace MixScrims;

public class DiscordConfig
{
    public bool EnableDiscordInvites { get; set; } = true;
    public int DiscordInviteDelayMinutes { get; set; } = 5;
    public List<DiscordInvite> Invites { get; set; } = new()
    {
        new DiscordInvite
        {
            WebhookUrl = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN",
            Username = "MixScrims Bot",
            AvatarUrl = "https://i.imgur.com/SXwCE1e.png",
            Content = "@here **We need more players!**",
            Embed = new Embed
            {
                Title = "Players Needed for Mix",
                Description = "We're looking for **{0} more player(s)** to start the match!\n\nJoin now and get ready to play!",
                Color = "29F24B",
                Footer = "React quickly - match starts when we have enough players!",
                Fields = new List<EmbedField>
                {
                    new EmbedField
                    {
                        Name = ":bar_chart: Status",
                        Value = "**{0}** player(s) needed",
                        Inline = true
                    },
                    new EmbedField
                    {
                        Name = ":satellite: Connect",
                        Value = "[Click here to connect](steam://run/730//+connect ip:port)",
                        Inline = true
                    }
                }
            }
        }
    };
}

public class DiscordInvite
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Embed Embed { get; set; } = new();

}

public class Embed
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Footer { get; set; } = string.Empty;
    public string Color { get; set; } = "000000";
    public List<EmbedField> Fields { get; set; } = new();
}

public class EmbedField
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Inline { get; set; } = true;

}