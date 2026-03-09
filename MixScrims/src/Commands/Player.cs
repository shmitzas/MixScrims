using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using MixScrims.Contract;

namespace MixScrims;

partial class MixScrims
{
	[Command("ready")]
	/// <summary>
	/// Marks player as ready if they are not already ready. If they are ready, they get informed that they are already ready
	/// </summary>
	public void OnReady(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnReady: command can only be used by players");
			return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnReady: player is invalid");
			return;
		}
		AddPlayerToReadyList(player, true);
	}

	/// <summary>
	/// Marks player as unready if they were ready (for example if a player disconnects while being ready)
	/// </summary>
	[Command("unready")]
	public void OnUnReady(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnUnReady: command can only be used by players");
			return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnUnReady: player is invalid");
			return;
		}
		RemovePlayerFromReadyList(player, true);
	}

	/// <summary>
	/// Players can revote map pick if the map picking is not over yet
	/// </summary>
	[Command("revote")]
	public void OnRevote(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnRevote: command can only be used by players");
			return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnRevote: player is invalid");
			return;
		}

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.MapVoting)
		{
			if (mapVotingMenu == null)
			{
				logger.LogError("OnRevote: mapVotingMenu is null");
				PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "revote"]);
				return;
			}
			DisplayMapVotingMenu(player);
			return;
		}
		PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "revote"]);
	}

	/// <summary>
	/// Players can call timeout if they have timeouts left and the match is in progress
	/// </summary>
	[Command("timeout")]
	public void OnTimeout(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnTimeout: command can only be used by players");
			return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnTimeout: player is invalid");

			return;
		}

		if (player.PlayerPawn == null)
		{
			logger.LogError("OnTimeout: PlayerPawn is null for player {PlayerName}", player.Controller?.PlayerName);
			return;
        }

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.Match)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "timeout"]);
			return;
        }

		if (timeoutPending != TimeoutPending.None)
		{
			PrintMessageToPlayer(player, Core.Localizer["error.timeout_pending"]);
			return;
        }

        var team = (Team)player.PlayerPawn.TeamNum;
		
		if (team == Team.CT)
		{
			if (timeoutCountCt < 1)
			{
                PrintMessageToPlayer(player, Core.Localizer["error.no_timeouts_left", 0, cfg.Timeouts]);
                return;
            }
			StartTimeoutVote(player, Team.CT);
        }

        if (team == Team.T)
        {
            if (timeoutCountT < 1)
            {
                PrintMessageToPlayer(player, Core.Localizer["error.no_timeouts_left", 0, cfg.Timeouts]);
                return;
            }
            StartTimeoutVote(player, Team.T);
        }
    }

	/// <summary>
	/// Sends an invite message to the discord webhook
	/// </summary>
	[Command("invite")]
	public void OnInvite(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnInvite: command can only be used by players");
			return;
		}

		var player = context.Sender;
		if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnInvite: player is invalid");
			return;
		}

		if (!discordConfig.EnableDiscordInvites)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invite.disabled"]);
			return;
		}

		if (discordConfig.Invites.Count == 0)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invite.no_webhooks"]);
			return;
		}

		var timeSinceLastInvite = DateTime.Now - lastDiscordInviteSentAt;
		var timeRemaining = TimeSpan.FromMinutes(discordConfig.DiscordInviteDelayMinutes) - timeSinceLastInvite;

		if (timeRemaining > TimeSpan.Zero)
		{
			int minutes = (int)timeRemaining.TotalMinutes;
			int seconds = timeRemaining.Seconds;
			string formattedTime = $"{minutes}min {seconds}s";

			PrintMessageToPlayer(player, Core.Localizer["command.invite.to_early", formattedTime]);
			return;
		}

		int playingPlayers = GetPlayers().Count;

		int remainingPlayers = cfg.MinimumReadyPlayers - playingPlayers;

		if (remainingPlayers < 1)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invite.no_need", playingPlayers, cfg.MinimumReadyPlayers]);
			return;
		}

		_ = Task.Run(async () =>
		{
			foreach (var invite in discordConfig.Invites)
			{
				var inviteWithReplacements = ReplaceInvitePlaceholders(invite, remainingPlayers);
				await SendToDiscord(inviteWithReplacements);
			}
		});

		lastDiscordInviteSentAt = DateTime.Now;
		PrintMessageToAllPlayers(Core.Localizer["command.invite", player.Controller.PlayerName]);
	}

	/// <summary>
	/// Additional way of chosing wheter to stay or switch teams after knife round
	/// </summary>
	[Command("stay")]
	public void OnStay(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnStay: command can only be used by players");
			return;
        }

        var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnStay: player is invalid");
			return;
		}

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.PickingStartingSide)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "sidePick"]);
			return;
		}

		if (cfg.DisableCaptains)
		{
			HandleCaptainSideChoice(player, "Stay");
			return;
		}

		if (player != winnerCaptain)
		{
			PrintMessageToPlayer(player, Core.Localizer["error.not_captain"]);
			return;
		}

        if (!IsBot(player) && IsPlayerValid(player))
        {
            var menu = Core.MenusAPI.GetCurrentMenu(player);
			if (menu != null)
			{
                Core.MenusAPI.CloseMenuForPlayer(player, menu);
            }
        }

        var token = Core.Scheduler.DelayBySeconds(1, () => StayStartingSides(player));
        Core.Scheduler.StopOnMapChange(token);
        logger.LogInformation($"OnStay: Captain {player.Controller.PlayerName} chose to !stay");
	}

	/// <summary>
	/// Additional way of chosing wheter to stay or switch teams after knife round
	/// </summary>
	[Command("switch")]
	public void OnSwitch(ICommandContext context)
	{
		var player = context.Sender;
        if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnStay: player is invalid");
			return;
		}

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState != MatchState.PickingStartingSide)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "sidePick"]);
			return;
		}

		if (cfg.DisableCaptains)
		{
			HandleCaptainSideChoice(player, "Switch");
			return;
		}

		if (player != winnerCaptain)
		{
			PrintMessageToPlayer(player, Core.Localizer["error.not_captain"]);
			return;
		}

        if (!IsBot(player) && IsPlayerValid(player))
        {
            var menu = Core.MenusAPI.GetCurrentMenu(player);
            if (menu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(player, menu);
            }
        }

        var token = Core.Scheduler.DelayBySeconds(1, () => SwitchStartingSides(player));
		Core.Scheduler.StopOnMapChange(token);
        logger.LogInformation($"OnStay: Captain {player.Controller.PlayerName} chose to !switch");
    }

	/// <summary>
	/// Handles a player's request to volunteer as a team captain for the current match.
	/// </summary>
	public void OnCaptainVolunteer(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnCaptainVolunteer: command can only be used by players");
			return;
		}
		var player = context.Sender;
		if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnCaptainVolunteer: player is invalid");
			return;
		}
		
        var admin = context.Sender;
        if (admin == null || !context.IsSentByPlayer)
        {
            logger.LogError("Console cannot set captain, only a live player can");
            return;
        }

		if (cfg.DisableCaptains)
		{
			if (cfg.DetailedLogging)
				logger.LogInformation("OnCaptainVolunteer: Captains are disabled in configuration.");
			PrintMessageToPlayer(player, Core.Localizer["error.captain.disabled"]);
			return;
		}

        var matchState = mixScrimsService.GetCurrentMatchState();

        if (matchState == MatchState.Warmup
			|| matchState == MatchState.MapLoading
			|| matchState == MatchState.MapChosen)
		{
			if (context.Args.Length < 1)
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
				return;
			}

			var team = context.Args[0].ToLower();
			if (team != "t" && team != "ct")
			{
				PrintMessageToPlayer(admin, Core.Localizer["error.invalid_args", "!vol_cap <t/ct>"]);
				return;
			}

			if (cfg.AllowVolunteerCaptains == false)
			{
				PrintMessageToPlayer(player, Core.Localizer["error.captain.volunteering_disabled"]);
				return;
			}
			if (captainCt != null && captainT != null)
			{
				PrintMessageToPlayer(player, Core.Localizer["error.captains_already_chosen"]);
				return;
			}
			if (captainCt != null && captainCt.PlayerID == player.PlayerID)
			{
				PrintMessageToPlayer(player, Core.Localizer["error.already_captain.ct"]);
				return;
			}
			if (captainT != null && captainT.PlayerID == player.PlayerID)
			{
				PrintMessageToPlayer(player, Core.Localizer["error.already_captain.t"]);
				return;
			}

			if (team == "ct" && captainCt == null)
				PickCtCaptain(player);

			if (team == "t" && captainT == null)
				PickTCaptain(player);
		}
		else
		{
            logger.LogError("OnCaptain: Invalid match state \"{matchState}\", must be MatchState.Warmup/MapChosen/MapLoading", matchState);
            PrintMessageToPlayer(admin, Core.Localizer["command.invalid_state", "captain"]);
        }
    }

	/// <summary>
	/// Players can surrender the match if their team agrees
	/// </summary>
	public void OnSurrender(ICommandContext context)
	{
		if (!context.IsSentByPlayer)
		{
			logger.LogError("OnSurrender: command can only be used by players");
			return;
		}

		var player = context.Sender;
		if (player == null || !IsPlayerValid(player))
		{
			logger.LogError("OnSurrender: player is invalid");
			return;
		}

		if (player.PlayerPawn == null)
		{
			logger.LogError("OnSurrender: PlayerPawn is null for player {PlayerName}", player.Controller?.PlayerName);
			return;
		}

		var matchState = mixScrimsService.GetCurrentMatchState();

		if (matchState != MatchState.Match && matchState != MatchState.KnifeRound)
		{
			PrintMessageToPlayer(player, Core.Localizer["command.invalid_state", "surrender"]);
			return;
		}

		var team = (Team)player.PlayerPawn.TeamNum;

		if (team == Team.CT)
		{
			StartSurrenderVote(player, Team.CT);
		}
		else if (team == Team.T)
		{
			StartSurrenderVote(player, Team.T);
		}
		else
		{
			PrintMessageToPlayer(player, Core.Localizer["error.not_in_team"]);
		}
	}

}

