using SwiftlyS2.Shared.Players;

namespace MixScrims.Contract;

/// <summary>
/// MixScrims' shared API surface; the project wiki is the full API documentation.
/// </summary>
public interface IMixScrims : IDisposable
{
    /// <summary>Fires when the match state transitions between two distinct values; no-op transitions do not fire.</summary>
    event Action<MatchState, MatchState>? MatchStateChanged;

    /// <summary>Fires when the plugin operational mode changes between Production and Staging.</summary>
    event Action<PluginState, PluginState>? PluginStateChanged;

    /// <summary>Fires when a player's ready status changes during Warmup or MapChosen (SteamID64, isReady).</summary>
    event Action<ulong, bool>? PlayerReadyChanged;

    /// <summary>Fires when a captain slot is filled (team, incoming captain SteamID64).</summary>
    event Action<Team, ulong>? CaptainAssigned;

    /// <summary>Fires when a captain slot is vacated (team, outgoing captain SteamID64), preceding <see cref="CaptainAssigned"/> on a replacement.</summary>
    event Action<Team, ulong>? CaptainRemoved;

    /// <summary>Fires once when the PickingTeam phase opens, carrying the team that picks first.</summary>
    event Action<Team>? TeamPickingStarted;

    /// <summary>Fires when a player joins a picked roster (team, picked SteamID64, 1-based pick index), where the SteamID is 0 for a bot.</summary>
    event Action<Team, ulong, int>? PlayerPickedForTeam;

    /// <summary>Fires once when map voting opens (voteable map display names in menu order, deadline in seconds).</summary>
    event Action<IReadOnlyList<string>, int>? MapVotingStarted;

    /// <summary>Fires when a player casts or changes a map vote (voter SteamID64, chosen map, previous map or null).</summary>
    event Action<ulong, string, string?>? MapVoteCast;

    /// <summary>Fires once when map voting ends (winning map display name, winning vote count).</summary>
    event Action<string, int>? MapVotingEnded;

    /// <summary>Fires once when the plugin transitions into <see cref="MatchState.KnifeRound"/>.</summary>
    event Action? KnifeRoundStarted;

    /// <summary>Fires once when the knife round is decided, carrying the winning team.</summary>
    event Action<Team>? KnifeRoundWon;

    /// <summary>Fires when the winning captain's side selection opens, carrying their SteamID64, and never fires while captains are disabled.</summary>
    event Action<ulong>? PickingStartingSideStarted;

    /// <summary>Fires when the winning team's starting side is decided, carrying the side they kept.</summary>
    event Action<Team>? StartingSideChosen;

    /// <summary>Fires when a timeout goes live (team, duration in seconds), not when one is queued.</summary>
    event Action<Team, int>? TimeoutStarted;

    /// <summary>Fires once per second during a timeout with the remaining seconds, counting down to 1.</summary>
    event Action<int>? TimeoutTick;

    /// <summary>Fires when the active timeout ends, carrying the team that was on timeout.</summary>
    event Action<Team>? TimeoutEnded;

    /// <summary>Fires when a timeout vote opens, carrying the team it runs for.</summary>
    event Action<Team>? TimeoutVoteStarted;

    /// <summary>Fires when a timeout vote is cast (voter SteamID64, voteYes, team).</summary>
    event Action<ulong, bool, Team>? TimeoutVoteCast;

    /// <summary>Fires when a timeout vote resolves (team, passed).</summary>
    event Action<Team, bool>? TimeoutVoteResult;

    /// <summary>Fires when a surrender vote opens, carrying the team it runs for.</summary>
    event Action<Team>? SurrenderVoteStarted;

    /// <summary>Fires when a surrender vote is cast (voter SteamID64, voteYes, team).</summary>
    event Action<ulong, bool, Team>? SurrenderVoteCast;

    /// <summary>Fires when a surrender vote resolves (team, passed).</summary>
    event Action<Team, bool>? SurrenderVoteResult;

    /// <summary>Fires when a vote kick opens (team, target SteamID64, initiator SteamID64).</summary>
    event Action<Team, ulong, ulong>? VoteKickStarted;

    /// <summary>Fires when a vote-kick vote is cast, carrying the running tally alongside the voter.</summary>
    event Action<VoteKickCastEventArgs>? VoteKickCast;

    /// <summary>Fires when a vote kick resolves (team, passed).</summary>
    event Action<Team, bool>? VoteKickResult;

    /// <summary>Fires once when the competitive match ends (winning team, CT score, T score), with <see cref="Team.None"/> for a draw.</summary>
    event Action<Team, int, int>? MatchEnded;

    /// <summary>Fires when <c>!captain</c> passes its checks while built-in menus are suppressed (admin SteamID64, requested side or null).</summary>
    event Action<ulong, Team?>? CaptainMenuRequested;

    /// <summary>Fires when <c>!volunteer_captain</c> passes its checks while built-in menus are suppressed (player SteamID64, requested side or null).</summary>
    event Action<ulong, Team?>? VolunteerCaptainMenuRequested;

    /// <summary>Fires when <c>!revote</c> passes its state check while built-in menus are suppressed, carrying the requesting player's SteamID64.</summary>
    event Action<ulong>? MapVoteMenuRequested;

    /// <summary>Ready human players by SteamID64, never including bots.</summary>
    IReadOnlyList<ulong> GetReadyPlayers();

    /// <summary>Ready count as the plugin itself counts it, including bots as implicitly ready while <c>TestMode</c> is on.</summary>
    int GetEffectiveReadyCount();

    /// <summary>Returns whether the given SteamID64 is currently marked ready.</summary>
    bool IsPlayerReady(ulong steamId);

    /// <summary>Raw <c>MinimumReadyPlayers</c> config value, which ignores <c>RequireAllConnectedPlayersToBeReady</c>.</summary>
    int GetMinimumReadyPlayers();

    /// <summary>Ready denominator resolved for the current lobby, pairing exactly with <see cref="GetEffectiveReadyCount"/>.</summary>
    int GetPlayersRequiredToStart();

    /// <summary>Whether the plugin requires every connected player to be ready before advancing.</summary>
    bool GetRequireAllConnectedPlayersToBeReady();

    /// <summary>Current CT captain SteamID64, or null when unset or invalid.</summary>
    ulong? GetCtCaptain();

    /// <summary>Current T captain SteamID64, or null when unset or invalid.</summary>
    ulong? GetTCaptain();

    /// <summary>SteamID64 of the captain entitled to choose the starting side, or null when nobody is.</summary>
    ulong? GetStartingSidePicker();

    /// <summary>Current CT team display name, or the CS2 default when unset.</summary>
    string GetCtTeamName();

    /// <summary>Current T team display name, or the CS2 default when unset.</summary>
    string GetTTeamName();

    /// <summary>Live CT and T scores, or <c>(0, 0)</c> when game rules are unavailable.</summary>
    (int Ct, int T) GetMatchScore();

    /// <summary>Map vote tallies keyed by map display name, empty outside the MapVoting state.</summary>
    IReadOnlyDictionary<string, int> GetMapVoteTallies();

    /// <summary>Map display names currently up for vote in menu order, empty outside the MapVoting state.</summary>
    IReadOnlyList<string> GetVoteableMapDisplayNames();

    /// <summary>Every map configured in <c>maps.jsonc</c> in file order, independent of match state and re-read per call so a config reload lands.</summary>
    IReadOnlyList<MapPoolEntry> GetConfiguredMapPool();

    /// <summary>Seconds remaining on the map vote deadline, or 0 outside the MapVoting state.</summary>
    int GetMapVoteSecondsRemaining();

    /// <summary>Map display name the given player currently backs, or null when they have not voted.</summary>
    string? GetPlayerMapVote(ulong steamId);

    /// <summary>Team whose turn it is to pick, or null outside the PickingTeam phase.</summary>
    Team? GetActivePickingTeam();

    /// <summary>Currently-unpicked SteamID64s during the PickingTeam phase, excluding bots.</summary>
    IReadOnlyList<ulong> GetUnpickedPlayers();

    /// <summary>1-based pick counter within the current PickingTeam phase, or 0 outside it.</summary>
    int GetCurrentPickIndex();

    /// <summary>Seconds remaining on the active timeout, or 0 when none is running.</summary>
    int GetActiveTimeoutRemainingSeconds();

    /// <summary>Team on the active timeout, or null when none is running.</summary>
    Team? GetActiveTimeoutTeam();

    /// <summary>Timeouts CT has left for this match.</summary>
    int GetRemainingTimeoutsCt();

    /// <summary>Timeouts T has left for this match.</summary>
    int GetRemainingTimeoutsT();

    /// <summary>Current CT vote-kick target SteamID64, or null when none is active.</summary>
    ulong? GetVoteKickTargetCt();

    /// <summary>Current T vote-kick target SteamID64, or null when none is active.</summary>
    ulong? GetVoteKickTargetT();

    /// <summary>CT vote-kick tally as (yes, cast, eligible), all zero when no vote is active.</summary>
    (int Yes, int Cast, int Eligible) GetVoteKickTallyCt();

    /// <summary>T vote-kick tally as (yes, cast, eligible), all zero when no vote is active.</summary>
    (int Yes, int Cast, int Eligible) GetVoteKickTallyT();

    /// <summary>Team whose surrender vote is in progress, or null when none is active.</summary>
    Team? GetActiveSurrenderVoteTeam();

    /// <summary>Surrender vote tally as (yes, cast, eligible), all zero when no vote is active.</summary>
    (int Yes, int Cast, int Eligible) GetSurrenderVoteTally();

    /// <summary>Resolves a key through MixScrims' translation files, using the player's locale when they are connected.</summary>
    string GetLocalizedString(ulong steamId, string key, params object[] args);

    /// <summary>Suppresses or restores every built-in menu, overriding config until the plugin unloads.</summary>
    void SetBuiltInMenusSuppressed(bool suppressed);

    /// <summary>Suppresses or restores every built-in center-HTML broadcast, overriding config until the plugin unloads.</summary>
    void SetBuiltInCenterHtmlSuppressed(bool suppressed);

    /// <summary>Current effective value of the built-in menu suppression switch.</summary>
    bool AreBuiltInMenusSuppressed();

    /// <summary>Current effective value of the built-in center-HTML suppression switch.</summary>
    bool IsBuiltInCenterHtmlSuppressed();

    /// <summary>Whether captains are enabled (<c>!DisableCaptains</c>).</summary>
    bool GetCaptainsEnabled();

    /// <summary>Whether the team picking phase is skipped (<c>SkipTeamPicking</c>).</summary>
    bool GetSkipTeamPickingEnabled();

    /// <summary>Whether the map voting phase is skipped (<c>SkipMapVoting</c>).</summary>
    bool GetSkipMapVotingEnabled();

    /// <summary>Whether players may volunteer as captain (<c>AllowVolunteerCaptains</c>).</summary>
    bool GetAllowVolunteerCaptainsEnabled();

    /// <summary>Timeout duration in seconds (<c>TimeoutDurationSeconds</c>).</summary>
    int GetTimeoutDurationSeconds();

    /// <summary>Total timeouts allocated per team at the start of a match (<c>Timeouts</c>).</summary>
    int GetTotalTimeoutsPerTeam();

    /// <summary>Default vote window in seconds (<c>DefaultVoteTimeSeconds</c>).</summary>
    int GetDefaultVoteTimeSeconds();

    /// <summary>Casts or changes a map vote, as a no-op unless the state is MapVoting and the map is currently up for vote.</summary>
    void CastMapVote(ulong steamId, string mapDisplayName);

    /// <summary>Casts a yes/no vote on the in-flight timeout vote, as a no-op unless the caller is an eligible member of that team who has not voted.</summary>
    void CastTimeoutVote(ulong steamId, bool voteYes);

    /// <summary>Casts a yes/no vote on the in-flight surrender vote, under the same guards as <see cref="CastTimeoutVote"/>.</summary>
    void CastSurrenderVote(ulong steamId, bool voteYes);

    /// <summary>Casts a yes/no vote on the named team's vote kick, which is explicit because CT and T can run one simultaneously.</summary>
    void CastVoteKickVote(ulong steamId, Team team, bool voteYes);

    /// <summary>Drives the full team-pick pipeline, as a no-op unless the state is PickingTeam and the caller captains the active picking team.</summary>
    void PickPlayerForTeam(ulong captainSteamId, ulong pickedSteamId);

    /// <summary>Unpicked player slots during the PickingTeam phase, including the bots <see cref="GetUnpickedPlayers"/> cannot address.</summary>
    IReadOnlyList<int> GetUnpickedPlayerSlots();

    /// <summary>Slot-keyed <see cref="PickPlayerForTeam"/> taking an <c>IPlayer.PlayerID</c>, so bots can be picked.</summary>
    void PickPlayerForTeamBySlot(ulong captainSteamId, int pickedSlot);

    /// <summary>Volunteers a player as captain of the given team, honouring the same checks the <c>!volunteer_captain</c> command performs.</summary>
    void VolunteerAsCaptain(ulong steamId, Team team);

    /// <summary>Records a starting-side choice after the knife round, one-shot per phase, as a no-op unless the state is PickingStartingSide and the caller is eligible.</summary>
    void ChooseStartingSide(ulong steamId, bool stay);

    /// <summary>Stops MixScrims advancing the match on its own, leaving every transition to the caller until cleared, which <c>Unload</c> must do.</summary>
    void SetPhaseProgressionHeld(bool held);

    /// <summary>Current value of the phase-progression hold.</summary>
    bool IsPhaseProgressionHeld();

    /// <summary>Overrides the coin toss for the next team-picking phase only, or restores it when passed null.</summary>
    void SetNextPickingTeam(Team? team);

    /// <summary>Pending pick-order override, or null when the next phase will toss for it.</summary>
    Team? GetNextPickingTeam();

    /// <summary>Retrieves the current state of the match.</summary>
    MatchState GetCurrentMatchState();
    /// <summary>Sets the current match state to the specified value, bypassing normal transition logic.</summary>
    void SetMatchState(MatchState state);

    /// <summary>Retrieves the current operational state of the plugin.</summary>
    PluginState GetCurrentPluginState();

    /// <summary>Sets the current operational state of the plugin.</summary>
    void SetPluginState(PluginState state);

    /// <summary>Sets the display name for the Counter-Terrorists team.</summary>
    void SetCounterTerroristsTeamName(string name);

    /// <summary>Sets the display name for the Terrorists team.</summary>
    void SetTerroristsTeamName(string name);

    /// <summary>Enters the warmup phase.</summary>
    void StartWarmup();

    /// <summary>Opens map voting over the configured map pool.</summary>
    void StartMapVoting();

    /// <summary>Enters the team picking phase.</summary>
    void StartTeamPicking();

    /// <summary>Starts a CT timeout.</summary>
    void StartTimeoutCt();

    /// <summary>Starts a T timeout.</summary>
    void StartTimeoutT();

    /// <summary>Stops the active timeout.</summary>
    void StopTimeout();

    /// <summary>Forces a CT surrender, ending the match.</summary>
    void SurrenderCt();

    /// <summary>Forces a T surrender, ending the match.</summary>
    void SurrenderT();

    /// <summary>Starts the competitive match, moving the playing rosters onto their sides and every other player to spectator.</summary>
    void StartMatch();

    /// <summary>Starts a knife round, sealing the pick rosters when it follows the pick phase and leaving them untouched from any other state.</summary>
    void StartKnifeRound();

    /// <summary>Cancels the current match, resetting match state along with the rosters, captains and ready list.</summary>
    void CancelMatch();

    /// <summary>Changes to the given official or workshop map, which after a match needs <see cref="CancelMatch"/> first to clear the rosters.</summary>
    void ChangeMap(string mapName = "", string workshopId = "");

    /// <summary>Marks every not-already-ready player as ready.</summary>
    void ForceAllPlayersToReady();

    /// <summary>Clears every player's ready mark.</summary>
    void ForceAllPlayersToUnready();

    /// <summary>SteamID64s on the CT picked roster.</summary>
    List<ulong> GetPickedCtPlayers();

    /// <summary>SteamID64s on the T picked roster.</summary>
    List<ulong> GetPickedTPlayers();

    /// <summary>Appends to the CT picked roster list only; <see cref="PickPlayerForTeam"/> is what drives the picking phase.</summary>
    void AddPlayerToPickedCtPlayers(ulong steamId);

    /// <summary>Appends to the T picked roster list only; <see cref="PickPlayerForTeam"/> is what drives the picking phase.</summary>
    void AddPlayerToPickedTPlayers(ulong steamId);

    /// <summary>Removes from the CT picked roster list only, without moving the player or rewinding the picking phase.</summary>
    void RemovePlayerFromPickedCtPlayers(ulong steamId);

    /// <summary>Removes from the T picked roster list only, without moving the player or rewinding the picking phase.</summary>
    void RemovePlayerFromPickedTPlayers(ulong steamId);

    /// <summary>SteamID64s on the active CT roster.</summary>
    List<ulong> GetPlayingCtPlayers();

    /// <summary>SteamID64s on the active T roster.</summary>
    List<ulong> GetPlayingTPlayers();

    /// <summary>Appends a connected player to the active CT roster list only; <see cref="StartMatch"/> is what moves them onto the team.</summary>
    void AddPlayerToPlayingCtPlayers(ulong steamId);

    /// <summary>Appends a connected player to the active T roster list only; <see cref="StartMatch"/> is what moves them onto the team.</summary>
    void AddPlayerToPlayingTPlayers(ulong steamId);

    /// <summary>Removes from the active CT roster list only, without moving the player or altering the match phase.</summary>
    void RemovePlayerFromPlayingCtPlayers(ulong steamId);

    /// <summary>Removes from the active T roster list only, without moving the player or altering the match phase.</summary>
    void RemovePlayerFromPlayingTPlayers(ulong steamId);

    /// <summary>The whole punishment queue; the <paramref name="steamId"/> argument is ignored.</summary>
    List<ulong> GetPlayersWaitingForPunishment(ulong steamId);

    /// <summary>Queues a player for punishment.</summary>
    void AddPlayerToWaitingForPunishmentList(ulong steamId);

    /// <summary>Dequeues a player from the punishment queue.</summary>
    void RemovePlayerFromWaitingForPunishmentList(ulong steamId);

    /// <summary>Admin-force sets the CT captain, bypassing every <see cref="VolunteerAsCaptain"/> eligibility check.</summary>
    void SetCtCaptain(ulong steamId);

    /// <summary>Admin-force sets the T captain, bypassing every <see cref="VolunteerAsCaptain"/> eligibility check.</summary>
    void SetTCaptain(ulong steamId);

    /// <summary>Kicks every connected human absent from both playing rosters.</summary>
    void KickNotPlayingPlayers(string? reason = "");

    /// <summary>Kicks every connected human absent from both picked rosters.</summary>
    void KickNotPickedPlayers(string? reason = "");

    /// <summary>Blocks or allows new players joining the match, and is disabled automatically when the match ends.</summary>
    void PreventNewPlayersJoining(bool value = false);
}
