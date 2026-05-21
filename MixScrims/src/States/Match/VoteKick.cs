using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    // CT team vote kick state
    internal IPlayer? voteKickTargetCt = null;
    internal bool isVoteKickInProgressCt = false;
    internal int voteKickYesCountCt = 0;
    internal int voteKickEligibleVotesCt = 0;
    internal int voteKickTotalVotesCastCt = 0;
    internal CancellationTokenSource? voteKickTimerCt = null;
    internal HashSet<ulong> voteKickVotersCt = [];

    // T team vote kick state
    internal IPlayer? voteKickTargetT = null;
    internal bool isVoteKickInProgressT = false;
    internal int voteKickYesCountT = 0;
    internal int voteKickEligibleVotesT = 0;
    internal int voteKickTotalVotesCastT = 0;
    internal CancellationTokenSource? voteKickTimerT = null;
    internal HashSet<ulong> voteKickVotersT = [];

    /// <summary>
    /// Initiates a vote to kick the specified player from their team. All eligible teammates must vote YES
    /// for the kick to pass. Any NO vote or timer expiry fails the vote.
    /// </summary>
    internal void StartVoteKick(IPlayer caller, IPlayer target, Team team)
    {
        var callerName = caller.Name ?? $"#{caller.PlayerID}";
        var targetName = target.Name ?? $"#{target.PlayerID}";

        if (cfg.DetailedLogging)
            logger.LogInformation("StartVoteKick: {Caller} called vote to kick {Target} on team {Team}", callerName, targetName, team);

        // Initialize per-team state
        if (team == Team.CT)
        {
            isVoteKickInProgressCt = true;
            voteKickTargetCt = target;
            voteKickYesCountCt = 1; // caller auto-votes yes
            voteKickTotalVotesCastCt = 1;
            voteKickVotersCt = [caller.SteamID];
            voteKickTimerCt?.Cancel();
            voteKickTimerCt = null;
        }
        else
        {
            isVoteKickInProgressT = true;
            voteKickTargetT = target;
            voteKickYesCountT = 1; // caller auto-votes yes
            voteKickTotalVotesCastT = 1;
            voteKickVotersT = [caller.SteamID];
            voteKickTimerT?.Cancel();
            voteKickTimerT = null;
        }

        // Eligible voters = all team members except the target
        var teamPlayers = GetPlayersInTeam(team);
        var eligible = teamPlayers.Where(p => p.PlayerID != target.PlayerID).ToList();
        int eligibleCount = eligible.Count;

        if (team == Team.CT)
            voteKickEligibleVotesCt = eligibleCount;
        else
            voteKickEligibleVotesT = eligibleCount;

        // Players that need to see the menu (eligible minus caller)
        var menuPlayers = eligible.Where(p => p.PlayerID != caller.PlayerID).ToList();

        // If the caller is the only eligible voter, auto-pass immediately
        if (menuPlayers.Count == 0)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartVoteKick: No other eligible voters, auto-passing vote");
            VoteKickResult(team, true);
            return;
        }

        // Count bot votes inline (bots auto-vote yes)
        int botYesVotes = 0;
        foreach (var p in menuPlayers.Where(p => IsBot(p)))
        {
            var voterSet = team == Team.CT ? voteKickVotersCt : voteKickVotersT;
            voterSet.Add(p.SteamID);
            botYesVotes++;
        }

        if (team == Team.CT)
        {
            voteKickYesCountCt += botYesVotes;
            voteKickTotalVotesCastCt += botYesVotes;
        }
        else
        {
            voteKickYesCountT += botYesVotes;
            voteKickTotalVotesCastT += botYesVotes;
        }

        // Check if all eligible players already voted yes after bots
        int currentYes = team == Team.CT ? voteKickYesCountCt : voteKickYesCountT;
        if (currentYes >= eligibleCount)
        {
            VoteKickResult(team, true);
            return;
        }

        // Announce to team
        PrintMessageToTeam(team, Core.Localizer["command.votekick.started", callerName, targetName]);

        // Build vote menu
        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.votekick", targetName])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var yesBtn = new ButtonMenuOption("Yes");
        yesBtn.Click += async (sender, args) =>
        {
            HandleVoteKickVote(args.Player, team, true);
            await ValueTask.CompletedTask;
        };
        builder.AddOption(yesBtn);

        var noBtn = new ButtonMenuOption("No");
        noBtn.Click += async (sender, args) =>
        {
            HandleVoteKickVote(args.Player, team, false);
            await ValueTask.CompletedTask;
        };
        builder.AddOption(noBtn);

        var menu = builder.Build();

        // Open menu for human eligible voters (excluding caller and bots)
        foreach (var player in menuPlayers.Where(p => !IsBot(p)))
        {
            if (IsPlayerValid(player))
                Core.MenusAPI.OpenMenuForPlayer(player, menu);
        }

        // Show initial vote progress (caller has already voted)
        SendVoteKickProgressCenterHtml(team);

        // Start vote expiry timer
        var timer = Core.Scheduler.DelayBySeconds(cfg.VoteKick.VoteKickTime, () => VoteKickResult(team, false));
        Core.Scheduler.StopOnMapChange(timer);

        if (team == Team.CT)
            voteKickTimerCt = timer;
        else
            voteKickTimerT = timer;
    }

    /// <summary>
    /// Handles a single player's vote in an active vote kick for the specified team.
    /// A NO vote immediately fails the vote; a YES vote checks whether all eligible players have voted.
    /// </summary>
    internal void HandleVoteKickVote(IPlayer voter, Team team, bool voteYes)
    {
        if (!IsPlayerValid(voter))
        {
            logger.LogWarning("HandleVoteKickVote: ignoring vote from invalid/disconnected player {Slot}.", voter?.Slot);
            return;
        }

        bool inProgress = team == Team.CT ? isVoteKickInProgressCt : isVoteKickInProgressT;
        if (!inProgress)
            return;

        var voters = team == Team.CT ? voteKickVotersCt : voteKickVotersT;
        if (!voters.Add(voter.SteamID))
            return; // already voted

        CloseMenuForPlayer(voter);

        if (team == Team.CT)
        {
            voteKickTotalVotesCastCt++;
            if (voteYes) voteKickYesCountCt++;
        }
        else
        {
            voteKickTotalVotesCastT++;
            if (voteYes) voteKickYesCountT++;
        }

        SendVoteKickProgressCenterHtml(team);

        // Any NO vote fails the kick immediately
        if (!voteYes)
        {
            var target = team == Team.CT ? voteKickTargetCt : voteKickTargetT;
            var targetName = target?.Name ?? "?";
            PrintMessageToTeam(team, Core.Localizer["command.votekick.failed", targetName]);
            CloseVoteKickMenusForTeam(team);
            ResetVoteKickState(team);
            return;
        }

        // Check if all eligible players voted yes
        int yesCount = team == Team.CT ? voteKickYesCountCt : voteKickYesCountT;
        int eligibleCount = team == Team.CT ? voteKickEligibleVotesCt : voteKickEligibleVotesT;
        if (yesCount >= eligibleCount)
        {
            (team == Team.CT ? voteKickTimerCt : voteKickTimerT)?.Cancel();
            VoteKickResult(team, true);
        }
    }

    /// <summary>
    /// Processes the final result of a vote kick. Kicks the target if the vote passed, otherwise announces failure.
    /// </summary>
    internal void VoteKickResult(Team team, bool passed)
    {
        bool inProgress = team == Team.CT ? isVoteKickInProgressCt : isVoteKickInProgressT;
        if (!inProgress)
            return;

        var target = team == Team.CT ? voteKickTargetCt : voteKickTargetT;
        var targetName = target?.Name ?? "?";

        if (cfg.DetailedLogging)
            logger.LogInformation("VoteKickResult: team {Team}, passed {Passed}, target {Target}", team, passed, targetName);

        CloseVoteKickMenusForTeam(team);

        if (passed && target != null)
        {
            var locKey = team == Team.CT ? "command.votekick.passed.ct" : "command.votekick.passed.t";
            PrintMessageToTeam(team, Core.Localizer[locKey, targetName]);
            KickPlayer(target.SteamID, Core.Localizer["info.kick_reason.votekicked"]);
        }
        else
        {
            PrintMessageToTeam(team, Core.Localizer["command.votekick.failed", targetName]);
        }

        ResetVoteKickState(team);
    }

    /// <summary>
    /// Sends a CenterHTML vote progress display ("Waiting for votes / [X/Y] players voted")
    /// to all players in the specified team.
    /// </summary>
    internal void SendVoteKickProgressCenterHtml(Team team)
    {
        int totalVotesCast = team == Team.CT ? voteKickTotalVotesCastCt : voteKickTotalVotesCastT;
        int eligibleCount = team == Team.CT ? voteKickEligibleVotesCt : voteKickEligibleVotesT;
        var message = Core.Localizer["info.center.votekick.progress", totalVotesCast, eligibleCount];

        var teamPlayers = GetPlayersInTeam(team);
        foreach (var player in teamPlayers)
        {
            if (IsPlayerValid(player) && !IsBot(player))
                player.SendCenterHTML(message, 5000);
        }
    }

    /// <summary>
    /// Closes the vote kick menu for all human players in the specified team.
    /// </summary>
    internal void CloseVoteKickMenusForTeam(Team team)
    {
        var teamPlayers = GetPlayersInTeam(team);
        foreach (var player in teamPlayers)
        {
            if (IsPlayerValid(player) && !IsBot(player))
                CloseMenuForPlayer(player);
        }
    }

    /// <summary>
    /// Resets all vote kick state variables for the specified team.
    /// </summary>
    internal void ResetVoteKickState(Team team)
    {
        if (team == Team.CT)
        {
            voteKickTargetCt = null;
            isVoteKickInProgressCt = false;
            voteKickYesCountCt = 0;
            voteKickEligibleVotesCt = 0;
            voteKickTotalVotesCastCt = 0;
            voteKickTimerCt?.Cancel();
            voteKickTimerCt = null;
            voteKickVotersCt.Clear();
        }
        else if (team == Team.T)
        {
            voteKickTargetT = null;
            isVoteKickInProgressT = false;
            voteKickYesCountT = 0;
            voteKickEligibleVotesT = 0;
            voteKickTotalVotesCastT = 0;
            voteKickTimerT?.Cancel();
            voteKickTimerT = null;
            voteKickVotersT.Clear();
        }
    }
}
