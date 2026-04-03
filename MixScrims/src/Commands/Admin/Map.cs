using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Changes the map to the specified map (if the map exists in the configuration)
    ///</summary>
    public void OnGoToMap(ICommandContext context)
    {
        var admin = context.Sender;
        if (context.Args.Length < 1)
        {
            logger.LogError("OnGoToMap: No map name provided");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!map <map_name>, eg Mirage or de_mirage"]);
            }
            return;
        }

        var mapName = context.Args[0];
        if (string.IsNullOrEmpty(mapName))
        {
            logger.LogError("OnGoToMap: No map name provided");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!map <map_name>, eg Mirage or de_mirage"]);
            }
            return;
        }

        var map = GetMapByName(mapName);
        if (map == null)
        {
            logger.LogError($"OnGoToMap: Map not found in configuration: {mapName}");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.map_not_found", mapName]);
            }
            return;
        }

        if (admin == null)
        {
            logger.LogInformation("Map changed by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.go_to_map", "Console", map.DisplayName]);
        }
        else
        {
            logger.LogInformation($"Map changed by {admin.Controller.PlayerName}");
            PrintMessageToAllPlayers(Core.Localizer["command.go_to_map", admin.Controller.PlayerName, map.DisplayName]);
        }

        LoadSelectedMap(map);
    }
}
