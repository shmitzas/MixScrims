using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

public partial class MixScrims
{
    internal HashSet<ulong> rtvVoters = [];

    /// <summary>
    /// Clears all RTV voters. Called on plugin reset, on Warmup map load, and after a successful RTV.
    /// </summary>
    internal void ResetRtvState()
    {
        if (rtvVoters.Count > 0 && cfg.DetailedLogging)
            logger.LogInformation("ResetRtvState: clearing {Count} RTV voter(s).", rtvVoters.Count);
        rtvVoters.Clear();
    }

    /// <summary>
    /// Counts non-bot valid players currently connected. Both the voter set and the threshold
    /// base exclude bots.
    /// </summary>
    internal int ComputeRtvHumanCount()
    {
        return GetPlayers().Count(p => !IsBot(p));
    }

    /// <summary>
    /// Computes the YES-vote threshold required to pass an RTV given the current human count.
    /// Uses ceil(humans * VoteThresholdRatio), floored by MinimumVotesRequired.
    /// </summary>
    internal int ComputeRtvThreshold(int humanCount)
    {
        if (humanCount <= 0)
            return Math.Max(1, cfg.Rtv.MinimumVotesRequired);
        int byRatio = (int)Math.Ceiling(humanCount * cfg.Rtv.VoteThresholdRatio);
        return Math.Max(cfg.Rtv.MinimumVotesRequired, byRatio);
    }

    /// <summary>
    /// Drops a voter when they disconnect and re-checks the threshold against the new human count.
    /// If the remaining votes still meet the (lower) threshold the RTV fires now.
    /// Warmup-only; no-op otherwise.
    /// </summary>
    internal void HandlePlayerDisconnectRtv(ulong steamId)
    {
        if (rtvVoters.Count == 0)
            return;
        if (!rtvVoters.Remove(steamId))
            return;

        if (!cfg.Rtv.Enabled)
            return;
        if (mixScrimsService.GetCurrentMatchState() != MatchState.Warmup)
            return;

        int humanCount = ComputeRtvHumanCount();
        if (humanCount < cfg.Rtv.MinimumPlayersRequired)
            return;

        int threshold = ComputeRtvThreshold(humanCount);
        if (rtvVoters.Count < threshold)
            return;

        if (cfg.DetailedLogging)
            logger.LogInformation("HandlePlayerDisconnectRtv: threshold reached after disconnect ({Voters}/{Threshold}).", rtvVoters.Count, threshold);

        PrintMessageToAllPlayers(Core.Localizer["announcement.rtv.passed"]);
        ResetRtvState();
        StartMapVotingPhase();
    }

    /// <summary>
    /// Registers a player's RTV vote during Warmup. Re-running the command yields an "already voted"
    /// message and changes no state. Fires the map vote when the threshold is reached and clears
    /// the voter set so the threshold cannot re-fire on subsequent joins.
    /// </summary>
    internal void TryRegisterRtvVote(IPlayer voter)
    {
        int humanCount = ComputeRtvHumanCount();
        if (humanCount < cfg.Rtv.MinimumPlayersRequired)
        {
            PrintMessageToPlayer(voter, Core.Localizer["command.rtv.not_enough_players",
                cfg.Rtv.MinimumPlayersRequired, humanCount]);
            return;
        }

        if (!rtvVoters.Add(voter.SteamID))
        {
            PrintMessageToPlayer(voter, Core.Localizer["command.rtv.already_voted"]);
            return;
        }

        int threshold = ComputeRtvThreshold(humanCount);
        var voterName = voter.Name ?? $"#{voter.PlayerID}";

        if (cfg.DetailedLogging)
            logger.LogInformation("TryRegisterRtvVote: {Player} voted ({Voters}/{Threshold}, humans={Humans}).",
                voterName, rtvVoters.Count, threshold, humanCount);

        PrintMessageToAllPlayers(Core.Localizer["announcement.rtv.voted",
            voterName, rtvVoters.Count, threshold]);

        if (rtvVoters.Count >= threshold)
        {
            PrintMessageToAllPlayers(Core.Localizer["announcement.rtv.passed"]);
            ResetRtvState();
            StartMapVotingPhase();
        }
    }
}
