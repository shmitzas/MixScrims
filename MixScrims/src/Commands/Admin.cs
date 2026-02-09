using Microsoft.Extensions.Logging;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Commands;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
	///<summary>
	///Forcefully resets mix state to the warmup state
	///</summary>
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
			logger.LogInformation($"Mix state has been reset by {admin.Controller.PlayerName}");
			PrintMessageToAllPlayers(Core.Localizer["command.mix_reset", admin.Controller.PlayerName]);
		}

		ResetPluginState();
	}

	///<summary>
	///Forcefully starts the match regardless of how many players are ready
	///</summary>
	public void OnForceMatchStart(ICommandContext context)
	{
		var admin = context.Sender;
		var connectedPlayers = GetPlayers().Count;
		
		if (connectedPlayers < cfg.MinimumReadyPlayers)
		{
			logger.LogWarning($"OnForceMatchStart: Not enough players connected ({connectedPlayers}/{cfg.MinimumReadyPlayers})");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
			}
			else
			{
				logger.LogWarning("Console: Not enough players to force start match");
			}
			return;
		}
		
		if (context.IsSentByPlayer)
		{
			if (admin == null)
			{
				logger.LogInformation("Match started by force by Admin (null)");
				PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Admin"]);
			}
			else
			{
				logger.LogInformation($"Match started by force by {admin.Controller.PlayerName}");
				PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", admin.Controller.PlayerName]);
			}
		}
		else
		{
			logger.LogInformation("Match started by force by Console");
			PrintMessageToAllPlayers(Core.Localizer["command.force.match_start", "Console"]);
		}

		StartKnifeRound();
	}

	///<summary>
	///Forcefully marks all players as ready and starts the next mix state
	///</summary>
	public void OnForceReady(ICommandContext context)
	{
		var admin = context.Sender;
		var connectedPlayers = GetPlayers().Count;
		
		if (connectedPlayers < cfg.MinimumReadyPlayers)
		{
			logger.LogWarning($"OnForceReady: Not enough players connected ({connectedPlayers}/{cfg.MinimumReadyPlayers})");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
			}
			else
			{
				logger.LogWarning("Console: Not enough players to force ready");
			}
			return;
		}
		
		if (admin == null)
		{
			logger.LogInformation("Players were forced into ready state by force by Console");
			PrintMessageToAllPlayers(Core.Localizer["command.force.ready", "Console"]);
		}
		else
		{
			logger.LogInformation($"Players were forced into ready state by force by {admin.Controller.PlayerName}");
			PrintMessageToAllPlayers(Core.Localizer["command.force.ready", admin.Controller.PlayerName]);
		}

		var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Warmup && matchState != MatchState.MapChosen)
		{
			logger.LogWarning("OnForceReady: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "forceready"]);
			}
			return;
		}

		ForceReadyAllPlayers();
	}

	internal void ForceReadyAllPlayers()
	{
        var players = GetPlayers();
        foreach (var player in players)
        {
            if (!readyPlayers.Any(rp => rp.PlayerID == player.PlayerID))
            {
                logger.LogInformation("OnForceReady: Adding players to ready list");
                AddPlayerToReadyList(player, false);
            }
        }
    }

    public void OnForceUnready(ICommandContext context)
    {
        var admin = context.Sender;
        var connectedPlayers = GetPlayers().Count;
        
        if (connectedPlayers < cfg.MinimumReadyPlayers)
        {
            logger.LogWarning($"OnForceUnready: Not enough players connected ({connectedPlayers}/{cfg.MinimumReadyPlayers})");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["error.not_enough_players", connectedPlayers, cfg.MinimumReadyPlayers]);
            }
            else
            {
                logger.LogWarning("Console: Not enough players to force unready");
            }
            return;
        }
        
        if (admin == null)
        {
            logger.LogInformation("Players were forced into unready state by force by Console");
            PrintMessageToAllPlayers(Core.Localizer["command.force.unready", "Console"]);
        }
        else
        {
            logger.LogInformation($"Players were forced into unready state by force by {admin.Controller.PlayerName}");
            PrintMessageToAllPlayers(Core.Localizer["command.force.unready", admin.Controller.PlayerName]);
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Warmup && matchState != MatchState.MapChosen)
        {
            logger.LogWarning("OnForceUnready: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
            if (admin != null)
            {
                PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "forceunready"]);
            }
            return;
        }

		ForceUnreadyAllPlayers();
    }

	internal void ForceUnreadyAllPlayers()
	{
        var players = GetPlayers();
        foreach (var player in players)
        {
            if (readyPlayers.Any(rp => rp.PlayerID == player.PlayerID))
            {
                logger.LogInformation("ForceUnreadyAllPlayers: Removing players from ready list");
                RemovePlayerFromReadyList(player, false);
            }
        }
    }

    ///<summary>
    ///Prompts a list of players to choose a captain for chosen team
    ///</summary>
    public void OnCaptain(ICommandContext context)
	{
		var admin = context.Sender;
		if (admin == null || !context.IsSentByPlayer)
		{
			logger.LogError("Console cannot set captain, only a live player can");
			return;
		}

		if (cfg.DisableCaptains)
		{
			if (cfg.DetailedLogging)
				logger.LogInformation("OnCaptain: Captains are disabled in configuration.");
			PrintMessageToPlayer(admin, Core.Localizer["error.captain.disabled"]);
			return;
		}

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup
			|| matchState == MatchState.MapLoading
			|| matchState == MatchState.MapChosen)
		{

			if (context.Args.Length < 1)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
				return;
			}

			var team = context.Args[0].ToLower();
			if (team != "t" && team != "ct")
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!captain <t/ct>"]);
				return;
			}

			var players = GetPlayingPlayers();
			players.RemoveAll(p => captainCt?.PlayerID == p.PlayerID || captainT?.PlayerID == p.PlayerID);

			if (players.Count == 0)
			{
				logger.LogWarning("OnCaptain: No eligible players to pick as captain");
				PrintMessageToPlayer(admin, "No eligible players available.");
				return;
			}

			var builder = Core.MenusAPI
				.CreateBuilder()
				.Design.SetMenuTitle(Core.Localizer["menu.captain_pick", team.ToUpper()])
				.Design.SetMenuTitleVisible(true)
				.Design.SetMenuFooterVisible(true)
				.EnableSound()
				.SetPlayerFrozen(false)
				.SetAutoCloseDelay(0);

			foreach (var player in players)
			{
				var displayName = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
				var button = new ButtonMenuOption(displayName);

				if (team == "t")
				{
					button.Click += async (sender, args) =>
					{
						SetTCaptain(admin, displayName);
						await ValueTask.CompletedTask;
					};
				}
				if (team == "ct")
				{
					button.Click += async (sender, args) =>
					{
						SetCtCaptain(admin, displayName);
						await ValueTask.CompletedTask;
					};
				}

				builder.AddOption(button);
			}

			var menu = builder.Build();
			if (IsPlayerValid(admin))
			{
				Core.MenusAPI.OpenMenuForPlayer(admin, menu);
			}
		}
        else
        {
			logger.LogError("OnCaptain: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
			PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "captain"]);
        }
    }

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

	///<summary>
	///Lists all the maps that are available for voting
	///</summary>
	public void OnListVoteableMaps(ICommandContext context)
	{
		var admin = context.Sender;
		var maps = GetMapsToVote();
		if (admin == null)
		{
			logger.LogInformation("Voteable maps list:");
			foreach (var map in maps)
			{
				logger.LogInformation($"Map: {map.DisplayName} ({map.MapName})");
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

	///<summary>
	///Lists all the maps that are available for voting
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
