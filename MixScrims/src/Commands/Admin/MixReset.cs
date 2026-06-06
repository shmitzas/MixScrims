using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully resets mix state to the warmup state
    ///</summary>
    [Command("mix_reset", false, "managemix", HelpText = "Forcefully resets the plugin to the warmup state. Usage: !mix_reset")]
    public void OnResetPlugin(ICommandContext context)
    {
        var admin = context.Sender;
        if (admin == null)
        {
            logger.LogInformation("Mix state has been reset by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.mix_reset", "Console"]);
        }
        else
        {
            logger.LogInformation("Mix state has been reset by {AdminName}", admin.Controller.PlayerName);
            PrintMessageToAllPlayers(Core.Localizer["command.mix_reset", admin.Controller.PlayerName]);
        }

        ResetPluginState();
    }
}
