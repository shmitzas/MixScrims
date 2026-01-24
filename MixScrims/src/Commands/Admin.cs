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
			PrintMessageToAllPlayers(Core.Localizer["command.mixReset", "Console"]);
		}
		else
		{
			logger.LogInformation($"Mix state has been reset by {admin.Controller.PlayerName}");
			PrintMessageToAllPlayers(Core.Localizer["command.mixReset", admin.Controller.PlayerName]);
		}

		ResetPluginState();
	}

	///<summary>
	///Forcefully starts the match regardless of how many players are ready
	///</summary>
	public void OnForceMatchStart(ICommandContext context)
	{
		var admin = context.Sender;
		if (context.IsSentByPlayer)
		{
			if (admin == null)
			{
				logger.LogInformation("Match started by force by Admin (null)");
				PrintMessageToAllPlayers(Core.Localizer["command.forceMatchStart", "Admin"]);
			}
			else
			{
				logger.LogInformation($"Match started by force by {admin.Controller.PlayerName}");
				PrintMessageToAllPlayers(Core.Localizer["command.forceMatchStart", admin.Controller.PlayerName]);
			}
		}
		else
		{
			logger.LogInformation("Match started by force by Console");
			PrintMessageToAllPlayers(Core.Localizer["command.forceMatchStart", "Console"]);
		}

		StartKnifeRound();
	}

	///<summary>
	///Forcefully marks all players as ready and starts the next mix state
	///</summary>
	public void OnForceReady(ICommandContext context)
	{
		var admin = context.Sender;
		if (admin == null)
		{
			logger.LogInformation("Players were forced into ready state by force by Console");
			PrintMessageToAllPlayers(Core.Localizer["command.forceReady", "Console"]);
		}
		else
		{
			logger.LogInformation($"Players were forced into ready state by force by {admin.Controller.PlayerName}");
			PrintMessageToAllPlayers(Core.Localizer["command.forceReady", admin.Controller.PlayerName]);
		}

		var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Warmup && matchState != MatchState.MapChosen)
		{
			logger.LogWarning("OnForceReady: Invalid match state, must be MatchState.Warmup or MatchState.MapChosen");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["command.invalidState", "forceready"]);
			}
			return;
		}

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

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup
			|| matchState == MatchState.MapLoading
			|| matchState == MatchState.MapChosen)
		{

			if (context.Args.Length < 1)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.InvalidArgs", "!captain <t/ct>"]);
				return;
			}

			var team = context.Args[0].ToLower();
			if (team != "t" && team != "ct")
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.InvalidArgs", "!captain <t/ct>"]);
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
				.Design.SetMenuTitle(Core.Localizer["menu.captainPickTitle", team.ToUpper()])
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
			PrintMessageToPlayer(admin, Core.Localizer["command.invalidState", "captain"]);
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
				PrintMessageToPlayer(admin, Core.Localizer["error.InvalidArgs", "!map <map_name>, eg Mirage or de_mirage"]);
			}
			return;
		}

		var mapName = context.Args[0];
		if (string.IsNullOrEmpty(mapName))
		{
			logger.LogError("OnGoToMap: No map name provided");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.InvalidArgs", "!map <map_name>, eg Mirage or de_mirage"]);
			}
			return;
		}

		var map = GetMapByName(mapName);
		if (map == null)
		{
			logger.LogError($"OnGoToMap: Map not found in configuration: {mapName}");
			if (admin != null)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.mapNotFound", mapName]);
			}
			return;
		}

		if (admin == null)
		{
			logger.LogInformation("Map changed by Console");
			PrintMessageToAllPlayers(Core.Localizer["command.goToMap", "Console", map.DisplayName]);
		}
		else
		{
			logger.LogInformation($"Map changed by {admin.Controller.PlayerName}");
			PrintMessageToAllPlayers(Core.Localizer["command.goToMap", admin.Controller.PlayerName, map.DisplayName]);
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
