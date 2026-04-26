using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Lists all the maps that are available for voting
    ///</summary>
    [Command("maps", true, "managemix", HelpText = "Lists all maps available for voting. Usage: !maps")]
    public void OnListVoteableMaps(ICommandContext context)
    {
        var admin = context.Sender;
        var maps = GetMapsToVote();
        if (admin == null)
        {
            logger.LogInformation("Voteable maps list:");
            foreach (var map in maps)
            {
                logger.LogInformation("Map: {DisplayName} ({MapName})", map.DisplayName, map.MapName);
            }
        }
        else
        {
            PrintMessageToPlayer(admin, "Voteable maps list:");
            foreach (var map in maps)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.maps", map.DisplayName, map.MapName]);
            }
        }
    }
}
