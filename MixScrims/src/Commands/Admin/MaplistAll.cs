using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Lists all configured maps regardless of voteable status
    ///</summary>
    public void OnListAllMaps(ICommandContext context)
    {
        var admin = context.Sender;
        var maps = cfg.Maps.ToList();
        if (admin == null)
        {
            logger.LogInformation("All maps list:");
            foreach (var map in maps)
            {
                logger.LogInformation($"Map: {map.DisplayName} ({map.MapName})");
            }
        }
        else
        {
            PrintMessageToPlayer(admin, "All maps list:");
            foreach (var map in maps)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.maps", map.DisplayName, map.MapName]);
            }
        }
    }
}
