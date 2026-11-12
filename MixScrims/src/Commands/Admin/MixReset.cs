using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    ///<summary>
    ///Forcefully resets mix state to the warmup state
    ///</summary>
    [Command("mix_reset", false, "managemix", HelpText = "Forcefully resets the plugin to the warmup state. Usage: !mix_reset")]
    public void OnResetPlugin(ICommandContext context) => ResetMixByAdmin(context.Sender);

    /// <summary><c>!mix_reset</c>, shared with the control panel's reset row.</summary>
    internal void ResetMixByAdmin(IPlayer? admin)
    {
        if (admin == null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Mix state has been reset by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.mix_reset", "Console"]);
        }
        else
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("Mix state has been reset by {AdminName}", admin.Name);
            PrintMessageToAllPlayers(Core.Localizer["command.mix_reset", admin.Name]);
        }

        ResetPluginState();
    }
}
