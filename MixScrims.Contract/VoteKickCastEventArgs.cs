using SwiftlyS2.Shared.Players;

namespace MixScrims.Contract;

/// <summary>
/// Payload for the <see cref="IMixScrims.VoteKickCast"/> event. Uses a dedicated record because
/// the payload carries six fields (over the plain-Action threshold of three).
/// </summary>
/// <param name="Team">Team whose vote kick is in progress (CT or T).</param>
/// <param name="VoterSteamId">SteamID64 of the player whose vote was just cast.</param>
/// <param name="VoteYes">True for a YES vote, false for a NO vote.</param>
/// <param name="CurrentYesCount">Running YES-vote tally after this cast (includes caller auto-yes and any bot auto-yes).</param>
/// <param name="CurrentTotalCast">Running total of votes cast after this cast (yes + no).</param>
/// <param name="EligibleVotes">Total eligible voters for this vote kick (team size minus target).</param>
public sealed record VoteKickCastEventArgs(
    Team Team,
    ulong VoterSteamId,
    bool VoteYes,
    int CurrentYesCount,
    int CurrentTotalCast,
    int EligibleVotes);
