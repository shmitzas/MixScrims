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
    internal Team sideVoteWinnerTeam { get; set; } = Team.None;

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

        if (captainCt != null && IsPlayerValid(captainCt))
        {
            if (!playingCtPlayers.Any(p => p.PlayerID == captainCt.PlayerID))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Adding manually-set CT Captain {PlayerName} to playingCtPlayers.", captainCt.Controller.PlayerName);
                playingCtPlayers.Add(captainCt);
            }
        }

        if (captainT != null && IsPlayerValid(captainT))
        {
            if (!playingTPlayers.Any(p => p.PlayerID == captainT.PlayerID))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Adding manually-set T Captain {PlayerName} to playingTPlayers.", captainT.Controller.PlayerName);
                playingTPlayers.Add(captainT);
            }
        }

        if (captainCt == null && playingCtPlayers.Count > 0)
        {
            captainCt = playingCtPlayers[0];
            if (cfg.DetailedLogging)
                logger.LogInformation("StartKnifeRound: CT Captain not set, assigning {PlayerName} as CT Captain.", captainCt.Controller.PlayerName);
        }

        if (captainT == null && playingTPlayers.Count > 0)
        {
            captainT = playingTPlayers[0];
            if (cfg.DetailedLogging)
                logger.LogInformation("StartKnifeRound: T Captain not set, assigning {PlayerName} as T Captain.", captainT.Controller.PlayerName);
        }

        readyPlayers.Clear();

        StopPreMatchAnnouncementTimers();

        if (cfg.ShowReadyStatusInScoreboard)
            RemoveReadyClanTagsFromAllPlayers();

        // Close any open team picking menus for captains
        if (captainCt != null && IsPlayerValid(captainCt))
        {
            var ctMenu = Core.MenusAPI.GetCurrentMenu(captainCt);
            if (ctMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(captainCt, ctMenu);
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Closed open menu for CT captain {PlayerName}", captainCt.Controller.PlayerName);
            }
        }

        if (captainT != null && IsPlayerValid(captainT))
        {
            var tMenu = Core.MenusAPI.GetCurrentMenu(captainT);
            if (tMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(captainT, tMenu);
                if (cfg.DetailedLogging)
                    logger.LogInformation("StartKnifeRound: Closed open menu for T captain {PlayerName}", captainT.Controller.PlayerName);
            }
        }

        UnpauseMatch();

        Core.Engine.ExecuteCommand("exec mixscrims/knife_round.cfg");
        Core.Scheduler.NextTick(() => RelaxEngineTeamLimits("StartKnifeRound"));

        if (cfg.KickPlayersNotInMatch)
        {
            mixScrimsService.KickNotPlayingPlayers(Core.Localizer["info.kick_reason.not_picked"]);
        }
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
            sideVoteWinnerTeam = winnerTeam;
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

            var sideVoteToken = Core.Scheduler.DelayBySeconds(30, () =>
            {
                if (mixScrimsService.GetCurrentMatchState() == MatchState.PickingStartingSide)
                {
                    ProcessTeamSideVotes();
                }
            });
            Core.Scheduler.StopOnMapChange(sideVoteToken);
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
            var playerTeam = (captain.PlayerPawn?.TeamNum == 3) ? Team.CT : Team.T;
            if (sideVoteWinnerTeam != Team.None && playerTeam != sideVoteWinnerTeam)
            {
                PrintMessageToPlayer(captain, Core.Localizer["error.not_winner_team"]);
                return;
            }

            sideVotes[captain.PlayerID] = choice;
            PrintMessageToPlayer(captain, Core.Localizer["command.side_vote.recorded", choice]);

            var winningTeamPlayers = sideVoteWinnerTeam != Team.None
                ? (sideVoteWinnerTeam == Team.CT ? playingCtPlayers : playingTPlayers)
                : (playerTeam == Team.CT ? playingCtPlayers : playingTPlayers);
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
        var switchVotes = sideVotes.Values.Count(v => string.Equals(v, "Switch", StringComparison.OrdinalIgnoreCase));
        var stayVotes = sideVotes.Values.Count(v => string.Equals(v, "Stay", StringComparison.OrdinalIgnoreCase));

        if (cfg.DetailedLogging)
            logger.LogInformation("ProcessTeamSideVotes: Switch={SwitchVotes}, Stay={StayVotes}", switchVotes, stayVotes);

        PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.vote_results", switchVotes, stayVotes]);

        var firstVoter = GetPlayers().FirstOrDefault(p => sideVotes.ContainsKey(p.PlayerID));

        if (switchVotes > stayVotes)
        {
            SwitchStartingSides(firstVoter);
        }
        else
        {
            StayStartingSides(firstVoter);
        }

        sideVotes.Clear();
    }

    /// <summary>
    /// Switches the starting sides of the Counter-Terrorist and Terrorist teams, including their players and captains.
    /// </summary>
    internal void SwitchStartingSides(IPlayer? captain)
    {
        if (captain != null && captain.PlayerPawn == null)
        {
            logger.LogError("SwitchStartingSides: Captain PlayerPawn is null.");
            return;
        }

        if (captain?.PlayerPawn?.TeamNum == 3)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_switch.ct", captain.Controller.PlayerName]);
        }

        if (captain?.PlayerPawn?.TeamNum == 2)
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
                SetTeamName(Team.CT, IsPlayerValid(captainCt) ? captainCt!.Controller.PlayerName : null);
                SetTeamName(Team.T, IsPlayerValid(captainT) ? captainT!.Controller.PlayerName : null);

                isMovingPlayersToTeams = true;

                foreach (var player in playingTPlayers)
                {
                    if (player != null && player.IsValid)
                    {
                        if (cfg.DetailedLogging)
                                logger.LogInformation("SwitchStartingSides: Moving {PlayerName} to T", player.Controller.PlayerName);
                        if (IsBot(player))
                            player.SwitchTeamAsync(Team.T);
                        else
                        {
                            try { player.ChangeTeamAsync(Team.T); }
                            catch (Exception ex)
                            {
                                if (cfg.DetailedLogging)
                                    logger.LogWarning(ex, "SwitchStartingSides: Error changing T player team.");
                            }
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
                                logger.LogInformation("SwitchStartingSides: Moving {PlayerName} to CT", player.Controller.PlayerName);
                        if (IsBot(player))
                            player.SwitchTeamAsync(Team.CT);
                        else
                        {
                            try { player.ChangeTeamAsync(Team.CT); }
                            catch (Exception ex)
                            {
                                if (cfg.DetailedLogging)
                                    logger.LogWarning(ex, "SwitchStartingSides: Error changing CT player team.");
                            }
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
        if (captain?.PlayerPawn?.TeamNum == 3)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.knife_round.captain.chose_stay.ct", captain.Controller.PlayerName]);
        }
        else if (captain?.PlayerPawn?.TeamNum == 2)
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
        var playingPlayerIds = new HashSet<int>(playingCtPlayers.Select(p => p.PlayerID).Concat(playingTPlayers.Select(p => p.PlayerID)));
        players.RemoveAll(p => playingPlayerIds.Contains(p.PlayerID));

        foreach (var player in players)
        {
            if (IsBot(player))
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("Player is a bot, skipping move to SPEC");
                continue;
            }

            // ChangeTeam kills the player and recreates the pawn entity, which is expensive.
            // Skip the call if the player is already on Spectator to avoid unnecessary entity churn.
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.Spectator)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on SPEC, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to SPEC", player.Controller!.PlayerName);
            player.ChangeTeamAsync(Team.Spectator);
        }

        var playingCtPlayerIds = new HashSet<int>(playingCtPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!playingCtPlayerIds.Contains(player.PlayerID))
                continue;

            // Skip if the player is already on CT — ChangeTeam kills/respawns the pawn,
            // and doing this unnecessarily for every player on phase transitions causes
            // ~1s server-frame stalls (combined with mp_restartgame in match_start.cfg).
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.CT)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on CT, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to CT", player.Controller!.PlayerName);
            if (IsBot(player))
            {
                player.SwitchTeamAsync(Team.CT);
            }
            else
            {
                player.ChangeTeamAsync(Team.CT);
            }
        }
        

        var playingTPlayerIds = new HashSet<int>(playingTPlayers.Select(p => p.PlayerID));
        foreach (var player in GetPlayingPlayers())
        {
            if (!playingTPlayerIds.Contains(player.PlayerID))
                continue;

            // Same reasoning as the CT loop above — avoid redundant pawn destroy/create.
            int currentTeam = player.Controller?.TeamNum ?? -1;
            if (currentTeam == (int)Team.T)
            {
                if (cfg.DetailedLogging)
                    logger.LogInformation("MovePlayersToDesignatedTeamsPreMatch: {PlayerName} already on T, skipping.", player.Controller?.PlayerName ?? "<unknown>");
                continue;
            }

            if (cfg.DetailedLogging)
                logger.LogInformation("Moving {PlayerName} to T", player.Controller!.PlayerName);
            if (IsBot(player))
            {
                player.SwitchTeamAsync(Team.T);
            }
            else
            {
                player.ChangeTeamAsync(Team.T);
            }
        }

        isMovingPlayersToTeams = false;
    }
}
