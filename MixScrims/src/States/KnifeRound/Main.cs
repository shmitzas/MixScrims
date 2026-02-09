using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal List<IPlayer> playingCtPlayers { get; set; } = [];
    internal List<IPlayer> playingTPlayers { get; set; } = [];
    internal IPlayer? winnerCaptain { get; set; } = null;
    internal Dictionary<int, string> sideVotes { get; set; } = new();

    /// <summary>
    /// Initiates the knife round phase of the match.
    /// </summary>
    internal void StartKnifeRound()
    {
        mixScrimsService.SetMatchState(MatchState.KnifeRound);
        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.knife_round"]);

        if (pickedCtPlayers.Count == 0)
        {
            logger.LogWarning("StartKnifeRound: No players picked for CT team. Setting current CT players as playingCtPlayers");
            var currentCtPlayers = GetPlayersInTeam(Team.CT);
            playingCtPlayers = currentCtPlayers.ToList();
        }
        else
        {
            playingCtPlayers = pickedCtPlayers.ToList();
            pickedCtPlayers.Clear();
        }

        if (pickedTPlayers.Count == 0)
        {
            logger.LogWarning("StartKnifeRound: No players picked for T team. Setting current T players as playingTPlayers");
            var currentTPlayers = GetPlayersInTeam(Team.T);
            playingTPlayers = currentTPlayers.ToList();
        }
        else
        {
            playingTPlayers = pickedTPlayers.ToList();
            pickedTPlayers.Clear();
        }

        if (captainCt == null && playingCtPlayers.Count > 0)
        {
            captainCt = playingCtPlayers[0];
            if (cfg.DetailedLogging)
                logger.LogInformation($"StartKnifeRound: CT Captain not set, assigning {captainCt.Controller.PlayerName} as CT Captain.");
        }

        if (captainT == null && playingTPlayers.Count > 0)
        {
            captainT = playingTPlayers[0];
            if (cfg.DetailedLogging)
                logger.LogInformation($"StartKnifeRound: T Captain not set, assigning {captainT.Controller.PlayerName} as T Captain.");
        }

        readyPlayers.Clear();

        StopPreMatchAnnouncementTimers();

        UnpauseMatch();
        //MovePlayersToDesignatedTeamsPreMatch();
        Core.Engine.ExecuteCommand("exec mixscrims/knife_round.cfg");
        mixScrimsService.KickNotPickedPlayers(Core.Localizer["info.kick_reason.not_picked"]);
    }

    /// <summary>
    /// Prompts the winning team's captain to choose the starting side for the match.
    /// </summary>
    internal void PromptWinnerTCaptainoChoseStartingSide(Team winnerTeam)
    {
        mixScrimsService.SetMatchState(MatchState.PickingStartingSide);

        if (cfg.DisableCaptains)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("PromptWinnerTCaptainoChoseStartingSide: Captains disabled, initiating team vote.");
            
            sideVotes.Clear();
            var winningTeamPlayers = winnerTeam == Team.CT ? playingCtPlayers : playingTPlayers;
            var teamName = winnerTeam == Team.CT ? "CT" : "T";
            
            PrintMessageToAllPlayers(Core.Localizer[$"announcement.knife_round.winner.{teamName.ToLower()}"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.team_vote_started"]);
            
            foreach (var player in winningTeamPlayers)
            {
                if (player != null && IsPlayerValid(player) && !IsBot(player))
                {
                    var menu = BuildSidePickingMenu();
                    Core.MenusAPI.OpenMenuForPlayer(player, menu);
                }
            }
            
            Core.Scheduler.DelayBySeconds(30, () =>
            {
                if (mixScrimsService.GetCurrentMatchState() == MatchState.PickingStartingSide)
                {
                    ProcessTeamSideVotes();
                }
            });
            return;
        }

        if (winnerTeam == Team.CT)
        {
            if (captainCt == null)
            {
                logger.LogError("PromptWinnerTCaptainoChoseStartingSide: CT Captain is null.");
                return;
            }

            winnerCaptain = captainCt;

            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.ct"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.ct", captainCt.Controller.PlayerName]);

            // Bot captain: auto "Switch"
            if (IsBot(captainCt))
            {
                HandleCaptainSideChoice(captainCt, "Switch");
                return;
            }

            var menu = BuildSidePickingMenu();
            if (IsPlayerValid(captainCt))
            {
                Core.MenusAPI.OpenMenuForPlayer(captainCt, menu);
            }
        }

        if (winnerTeam == Team.T)
        {
            if (captainT == null)
            {
                logger.LogError("PromptWinnerTCaptainoChoseStartingSide: T Captain is null.");
                return;
            }

            winnerCaptain = captainT;

            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.winner.t"]);
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.waiting_for_side_pick.t", captainT.Controller.PlayerName]);

            // Bot captain: auto "Switch"
            if (IsBot(captainT))
            {
                HandleCaptainSideChoice(captainT, "Switch");
                return;
            }

            var menu = BuildSidePickingMenu();
            if (IsPlayerValid(captainT))
            {
                Core.MenusAPI.OpenMenuForPlayer(captainT, menu);
            }
        }
    }

    /// <summary>
    /// Builds and returns a menu that allows the user to choose between switching or staying on their current side.
    /// </summary>
    internal IMenuAPI BuildSidePickingMenu()
    {
        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.side_picking"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .DisableExit()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var switchBtn = new ButtonMenuOption("Switch");
        switchBtn.Click += async (sender, args) =>
        {
            HandleCaptainSideChoice(args.Player, "Switch");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(switchBtn);

        var stayBtn = new ButtonMenuOption("Stay");
        stayBtn.Click += async (sender, args) =>
        {
            HandleCaptainSideChoice(args.Player, "Stay");
            await ValueTask.CompletedTask;
        };
        builder.AddOption(stayBtn);

        return builder.Build();
    }

    /// <summary>
    /// Handles the captain's choice regarding starting sides in the game.
    /// </summary>
    internal void HandleCaptainSideChoice(IPlayer captain, string choice)
    {
        if (captain == null)
        {
            logger.LogError("HandleCaptainSideChoice: Captain is null.");
            return;
        }

        CloseMenuForPlayer(captain);

        if (cfg.DisableCaptains)
        {
            sideVotes[captain.PlayerID] = choice;
            PrintMessageToPlayer(captain, Core.Localizer["command.side_vote.recorded", choice]);
            
            var winningTeam = (captain.PlayerPawn?.TeamNum == 3) ? Team.CT : Team.T;
            var winningTeamPlayers = winningTeam == Team.CT ? playingCtPlayers : playingTPlayers;
            var validPlayers = winningTeamPlayers.Count(p => p != null && IsPlayerValid(p) && !IsBot(p));
            
            if (sideVotes.Count >= validPlayers)
            {
                ProcessTeamSideVotes();
            }
            return;
        }

        if (string.Equals(choice, "Switch", StringComparison.OrdinalIgnoreCase))
        {
            SwitchStartingSides(captain);
            return;
        }
        if (string.Equals(choice, "Stay", StringComparison.OrdinalIgnoreCase))
        {
            StayStartingSides(captain);
            return;
        }

        logger.LogError("HandleCaptainSideChoice: Invalid choice made by captain.");
    }

    /// <summary>
    /// Processes team votes for side selection when captains are disabled.
    /// </summary>
    internal void ProcessTeamSideVotes()
    {
        if (sideVotes.Count == 0)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("ProcessTeamSideVotes: No votes received, staying on current sides.");
            StayStartingSides(null);
            return;
        }

        var switchVotes = sideVotes.Values.Count(v => string.Equals(v, "Switch", StringComparison.OrdinalIgnoreCase));
        var stayVotes = sideVotes.Values.Count(v => string.Equals(v, "Stay", StringComparison.OrdinalIgnoreCase));

        if (cfg.DetailedLogging)
            logger.LogInformation($"ProcessTeamSideVotes: Switch={switchVotes}, Stay={stayVotes}");

        PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.vote_results", switchVotes, stayVotes]);

        if (switchVotes > stayVotes)
        {
            var firstVoter = GetPlayers().FirstOrDefault(p => sideVotes.ContainsKey(p.PlayerID));
            if (firstVoter != null)
            {
                SwitchStartingSides(firstVoter);
            }
        }
        else
        {
            var firstVoter = GetPlayers().FirstOrDefault(p => sideVotes.ContainsKey(p.PlayerID));
            StayStartingSides(firstVoter);
        }

        sideVotes.Clear();
    }

    /// <summary>
    /// Switches the starting sides of the Counter-Terrorist and Terrorist teams, including their players and captains.
    /// </summary>
    internal void SwitchStartingSides(IPlayer captain)
    {
        if (captain == null)
        {
            logger.LogError("SwitchStartingSides: Captain is null.");
            return;
        }

        if (captain.PlayerPawn == null)
        {
            logger.LogError("SwitchStartingSides: Captain PlayerPawn is null.");
            return;
        }

        if (captain.PlayerPawn.TeamNum == 3)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.ct", captain.Controller.PlayerName]);
        }

        if (captain.PlayerPawn.TeamNum == 2)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.t", captain.Controller.PlayerName]);
        }

        if (cfg.DetailedLogging)
            logger.LogInformation("SwitchStartingSides: Switching sides...");

        var oldCtCaptain = captainCt;
        var oldTCaptain = captainT;
        var oldPlayingCtPlayers = playingCtPlayers.ToList();
        var oldPlayingTPlayers = playingTPlayers.ToList();

        playingCtPlayers = oldPlayingTPlayers;
        playingTPlayers = oldPlayingCtPlayers;
        captainCt = oldTCaptain;
        captainT = oldCtCaptain;

        Core.Scheduler.DelayBySeconds(0.2f, () =>
        {
            Core.Scheduler.NextWorldUpdate(() => 
            {
                SetTeamName(Team.CT, captainCt?.Controller.PlayerName);
                SetTeamName(Team.T, captainT?.Controller.PlayerName);

                isMovingPlayersToTeams = true;

                foreach (var player in playingTPlayers)
                {
                    if (player != null && player.IsValid)
                    {
                        if (cfg.DetailedLogging)
                            logger.LogInformation($"SwitchStartingSides: Moving {player.Controller.PlayerName} to T");
                        if (IsBot(player))
                        {
                            Core.Scheduler.NextTick(() => player.SwitchTeam(Team.T));
                        }

                        try
                        {
                            player.ChangeTeam(Team.T);
                        }
                        catch (Exception ex)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogWarning(ex, $"SwitchStartingSides: Error changing team for player.");
                        }
                    }
                    else
                    {
                        if (cfg.DetailedLogging)
                            logger.LogWarning("SwitchStartingSides: Encountered invalid player in playingTPlayers list.");
                    }
                }

                foreach (var player in playingCtPlayers)
                {
                    if (player != null && player.IsValid)
                    {
                        if (cfg.DetailedLogging)
                            logger.LogInformation($"SwitchStartingSides: Moving {player.Controller.PlayerName} to CT");
                        if (IsBot(player))
                        {
                            Core.Scheduler.NextTick(() => player.SwitchTeam(Team.CT));
                        }

                        try
                        {
                            player.ChangeTeam(Team.T);
                        }
                        catch (Exception ex)
                        {
                            if (cfg.DetailedLogging)
                                logger.LogWarning(ex, $"SwitchStartingSides: Error changing team for player.");
                        }
                    }
                    else
                    {
                        if (cfg.DetailedLogging)
                            logger.LogWarning("SwitchStartingSides: Encountered invalid player in playingCtPlayers list.");
                    }
                }
            });

            Core.Scheduler.NextTick(() => isMovingPlayersToTeams = false);

            StartMatch();
        });
    }

    /// <summary>
    /// Keeps the teams on their starting sides based on the captain's current team.
    /// </summary>
    internal void StayStartingSides(IPlayer? captain)
    {
        if (captain == null)
        {
            logger.LogError("StayStartingSides: Captain is null.");
            return;
        }

        if (captain.PlayerPawn == null)
        {
            logger.LogError("StayStartingSides: Captain PlayerPawn is null.");
            return;
        }

        if (captain.PlayerPawn.TeamNum == 3)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.ct", captain.Controller.PlayerName]);
        }

        if (captain.PlayerPawn.TeamNum == 2)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.t", captain.Controller.PlayerName]);
        }

        StartMatch();
    }

    /// <summary>
    /// Assigns players to their designated teams before the match begins.
    /// </summary>
    internal void MovePlayersToDesignatedTeamsPreMatch()
    {
        if (cfg.DetailedLogging)
            logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch");
        
        isMovingPlayersToTeams = true;
        
        var players = GetPlayingPlayers();
        players.RemoveAll(p => playingCtPlayers.Contains(p) || playingTPlayers.Contains(p));

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation($"Player is a bot, skipping move to SPEC" );
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller.PlayerName} to SPEC");
            player.ChangeTeam(Team.Spectator);
        }

        foreach (var player in playingCtPlayers)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller.PlayerName} to CT");
            if (IsBot(player))
            {
                Core.Scheduler.NextTick(() => player.SwitchTeam(Team.CT));
            }
            else
            {
                player.ChangeTeam(Team.CT);
            }
        }
        

        foreach (var player in playingTPlayers)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation($"Moving {player.Controller.PlayerName} to T");
            if (IsBot(player))
            {
                Core.Scheduler.NextTick(() => player.SwitchTeam(Team.T));
            }
            else
            {
                player.ChangeTeam(Team.T);
            }
        }

        isMovingPlayersToTeams = false;
    }
}
