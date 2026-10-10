using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    /// <summary>
    /// Re-points every open vote and roster-driven menu at the roster a disconnect leaves
    /// behind, and resolves whatever that roster already settles. Branches are isolated so one
    /// throwing surface cannot strand the rest.
    /// </summary>
    internal void ReconcileVotesAfterDisconnect(IPlayer leaver)
    {
        try { HandlePlayerDisconnectSurrenderVote(leaver); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: surrender vote reconcile failed."); }

        try { HandlePlayerDisconnectTimeoutVote(leaver); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: timeout vote reconcile failed."); }

        try { HandlePlayerDisconnectVoteKick(leaver, Team.CT); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: CT vote kick reconcile failed."); }

        try { HandlePlayerDisconnectVoteKick(leaver, Team.T); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: T vote kick reconcile failed."); }

        try { HandlePlayerDisconnectMapVote(leaver); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: map vote reconcile failed."); }

        try { HandlePlayerDisconnectSideVote(leaver); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: starting-side vote reconcile failed."); }

        try { HandlePlayerDisconnectTeamPickMenu(leaver); }
        catch (Exception ex) { logger.LogError(ex, "ReconcileVotesAfterDisconnect: team pick menu reconcile failed."); }
    }

    /// <summary>
    /// Team members who could still answer: present, human, not the leaver, not
    /// <paramref name="exclude"/> (the vote-kick target) and not already in
    /// <paramref name="voters"/>. Bots are skipped - Start*Vote already counted their yes.
    /// </summary>
    private int CountPendingVoters(Team team, HashSet<ulong> voters, IPlayer leaver, IPlayer? exclude = null)
    {
        var pending = 0;
        foreach (var player in GetPlayersInTeam(team))
        {
            if (IsBot(player)) continue;
            if (IsSamePlayer(player, leaver)) continue;
            if (exclude != null && IsSamePlayer(player, exclude)) continue;

            // A human reading 0 is a disposed reference, i.e. someone already gone.
            var steamId = SafeSteamId(player);
            if (steamId == 0 || voters.Contains(steamId)) continue;
            pending++;
        }
        return pending;
    }

    /// <summary>
    /// Drops the leaver from the surrender electorate and fires the outcome if the remaining
    /// roster already settles it - including a collapse down to the caller alone.
    /// </summary>
    /// <remarks>
    /// Recomputed as "votes cast + voters who can still answer", never decremented:
    /// <see cref="SafeSteamId"/> reads 0 off a disposed reference, so a decrement keyed on the
    /// leaver would no-op exactly when it is needed.
    /// </remarks>
    internal void HandlePlayerDisconnectSurrenderVote(IPlayer leaver)
    {
        if (!isSurrenderVoteInProgress) return;
        if (surrenderVoteTeam is not (Team.CT or Team.T)) return;

        var cast = surrenderVoteYesCount + surrenderVoteNoCount;
        var electorate = cast + CountPendingVoters(surrenderVoteTeam, surrenderVoters, leaver);

        // Surrender and timeout store the team minus the caller, whose yes is already in `cast`;
        // vote kick keeps its caller. Deliberately different - do not unify.
        var recomputed = Math.Max(0, electorate - 1);
        if (recomputed == surrenderTotalEligibleVotes) return;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectSurrenderVote: eligible {Old} -> {New} ({Yes} yes / {No} no).",
                surrenderTotalEligibleVotes, recomputed, surrenderVoteYesCount, surrenderVoteNoCount);

        surrenderTotalEligibleVotes = recomputed;
        PrintMessageToTeam(surrenderVoteTeam, Core.Localizer["announcement.surrender.vote.progress",
            surrenderVoteYesCount, surrenderVoteNoCount, SurrenderRequiredVotes()]);

        TryResolveSurrenderVoteEarly();
    }

    /// <summary>
    /// Timeout counterpart of <see cref="HandlePlayerDisconnectSurrenderVote"/>; same electorate
    /// convention, and <see cref="TimeoutRequiredVotes"/> derives its lower threshold from it.
    /// </summary>
    internal void HandlePlayerDisconnectTimeoutVote(IPlayer leaver)
    {
        if (!isTimeoutVoteInProgress) return;
        if (timeoutVoteTeam is not (Team.CT or Team.T)) return;

        var cast = timeoutVoteYesCount + timeoutVoteNoCount;
        var electorate = cast + CountPendingVoters(timeoutVoteTeam, timeoutVoters, leaver);
        var recomputed = Math.Max(0, electorate - 1);
        if (recomputed == timeoutTotalEligibleVotes) return;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectTimeoutVote: eligible {Old} -> {New} ({Yes} yes / {No} no).",
                timeoutTotalEligibleVotes, recomputed, timeoutVoteYesCount, timeoutVoteNoCount);

        timeoutTotalEligibleVotes = recomputed;
        PrintMessageToTeam(timeoutVoteTeam, Core.Localizer["announcement.timeout.vote.progress",
            timeoutVoteYesCount, timeoutVoteNoCount, TimeoutRequiredVotes()]);

        TryResolveTimeoutVoteEarly();
    }

    /// <summary>
    /// Ends the vote when the kick target leaves, otherwise re-sizes the electorate and passes
    /// the kick once the remaining eligible voters are unanimous.
    /// </summary>
    internal void HandlePlayerDisconnectVoteKick(IPlayer leaver, Team team)
    {
        var inProgress = team == Team.CT ? isVoteKickInProgressCt : isVoteKickInProgressT;
        if (!inProgress) return;

        var target = team == Team.CT ? voteKickTargetCt : voteKickTargetT;

        // Nothing left to kick. Through VoteKickResult, never inline: it is the only path that
        // raises VoteKickResult, which a consumer rendering its own UI has nothing else to go on.
        if (IsSamePlayer(target, leaver))
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("HandlePlayerDisconnectVoteKick: {Team} target disconnected, closing the vote.", team);
            VoteKickResult(team, false);
            return;
        }

        var voters = team == Team.CT ? voteKickVotersCt : voteKickVotersT;
        var cast = team == Team.CT ? voteKickTotalVotesCastCt : voteKickTotalVotesCastT;

        // No -1 here: StartVoteKick keeps the caller inside the eligible count (it excludes only
        // the target), and the pass check compares the yes count against that same number.
        var recomputed = cast + CountPendingVoters(team, voters, leaver, target);

        var current = team == Team.CT ? voteKickEligibleVotesCt : voteKickEligibleVotesT;
        if (recomputed == current) return;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectVoteKick: {Team} eligible {Old} -> {New} ({Cast} cast).",
                team, current, recomputed, cast);

        if (team == Team.CT)
            voteKickEligibleVotesCt = recomputed;
        else
            voteKickEligibleVotesT = recomputed;

        SendVoteKickProgressCenterHtml(team);

        var yesCount = team == Team.CT ? voteKickYesCountCt : voteKickYesCountT;
        if (yesCount >= recomputed)
        {
            (team == Team.CT ? voteKickTimerCt : voteKickTimerT)?.Cancel();
            VoteKickResult(team, true);
        }
    }

    /// <summary>
    /// Withdraws the leaver's map vote.
    /// </summary>
    /// <remarks>
    /// Keyed on slot: a vote left behind makes the next connection into that slot read as a revote
    /// in <see cref="RegisterMapVoteByName"/>, decrementing a map they never chose.
    /// </remarks>
    internal void HandlePlayerDisconnectMapVote(IPlayer leaver)
    {
        if (mixScrimsService.GetCurrentMatchState() != MatchState.MapVoting) return;
        if (votedMaps.Count == 0) return;

        var slot = SafePlayerId(leaver);
        if (slot < 0) return;

        var castVote = votedMaps.FirstOrDefault(m => m.VotedBy.Contains(slot));
        if (castVote == null) return;

        castVote.VotedBy.Remove(slot);
        castVote.Votes = Math.Max(0, castVote.Votes - 1);

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectMapVote: withdrew a vote for {Map} ({Votes} left).",
                castVote.Map?.DisplayName ?? "<unknown>", castVote.Votes);
    }

    /// <summary>
    /// Drops the leaver out of the <c>DisableCaptains</c> starting-side vote and closes it if the
    /// rest of the winning team has already answered, instead of idling to the 30s backstop.
    /// </summary>
    internal void HandlePlayerDisconnectSideVote(IPlayer leaver)
    {
        if (!cfg.DisableCaptains) return;
        if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingStartingSide) return;
        if (startingSideCommitted) return;

        TryCloseTeamSideVote(leaver: leaver);
    }

    /// <summary>
    /// Rebuilds the open team-pick menu when the leaver was one of its options, so the captain is
    /// never left holding a button for someone who is gone - and so a disconnect that empties the
    /// pool advances the phase instead of waiting on a click that has no working target left.
    /// </summary>
    internal void HandlePlayerDisconnectTeamPickMenu(IPlayer leaver)
    {
        if (mixScrimsService.GetCurrentMatchState() != MatchState.PickingTeam) return;
        if (activePickingTeam is not { } team) return;

        var slot = SafePlayerId(leaver);
        if (slot < 0 || !openPickMenuPoolSlots.Contains(slot)) return;

        var captain = team == Team.CT ? captainCt : captainT;
        if (captain == null) return;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectTeamPickMenu: rebuilding the {Team} pick menu without slot {Slot}.", team, slot);

        CloseMenuForPlayer(captain);
        PromptCaptainToPickPlayer(captain, team, slot);
    }
}
