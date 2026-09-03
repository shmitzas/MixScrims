using SwiftlyS2.Shared.Players;

namespace MixScrims.Contract;

public interface IMixScrims : IDisposable
{
    // =========================================================================
    // Events (v2.0.0+)
    // =========================================================================
    //
    // Fire on the main game thread as a side effect of the state mutation that
    // triggered them. Subscribers should keep handlers cheap and non-throwing;
    // an unhandled exception in a handler is caught by the plugin and logged
    // rather than escalating, but repeatedly-throwing handlers WILL be logged
    // as warnings.
    //
    // For consumers that connect / hot-reload AFTER an event fired, every
    // event listed here has a matching snapshot query below so state can be
    // read without waiting for the next fire.

    /// <summary>
    /// Fires whenever the match state transitions between two distinct
    /// <see cref="MatchState"/> values via <see cref="SetMatchState"/>.
    /// Not fired for no-op transitions (state == new state).
    /// </summary>
    event Action<MatchState, MatchState>? MatchStateChanged;

    /// <summary>
    /// Fires whenever the plugin operational mode changes between
    /// <see cref="PluginState.Production"/> and <see cref="PluginState.Staging"/>.
    /// Not fired for no-op transitions.
    /// </summary>
    event Action<PluginState, PluginState>? PluginStateChanged;

    /// <summary>
    /// Fires when a player's ready status changes during Warmup or MapChosen
    /// (payload: SteamID64, isReady). Bots in TestMode are implicitly ready
    /// and never trigger this event.
    /// </summary>
    event Action<ulong, bool>? PlayerReadyChanged;

    /// <summary>
    /// Fires when a captain slot is filled (payload: team, newCaptainSteamId).
    /// If a captain is replaced, <see cref="CaptainRemoved"/> fires FIRST for
    /// the outgoing captain, then this fires for the incoming one.
    /// </summary>
    event Action<Team, ulong>? CaptainAssigned;

    /// <summary>
    /// Fires when a captain slot is vacated (payload: team, oldCaptainSteamId).
    /// See <see cref="CaptainAssigned"/> for reassignment ordering.
    /// </summary>
    event Action<Team, ulong>? CaptainRemoved;

    /// <summary>
    /// Fires once at the start of the PickingTeam phase after the starting
    /// team is randomly chosen (payload: starting team, CT or T).
    /// </summary>
    event Action<Team>? TeamPickingStarted;

    /// <summary>
    /// Fires whenever a player is added to a picked-team roster during team
    /// picking (payload: team, pickedSteamId, pickIndex). <c>pickIndex</c> is
    /// the 1-based sequence within the whole picking phase; captains' implicit
    /// self-picks occupy indices 1 and 2.
    /// <para>
    /// <c>pickedSteamId</c> is <c>0</c> when the pick was a bot — bots share
    /// SteamID 0 and cannot be identified through it. Treat this event as a
    /// "the pool changed, re-read it" trigger and resolve identity through
    /// <see cref="GetUnpickedPlayerSlots"/>, which is slot-keyed and includes
    /// bots. The roster is already updated when this fires.
    /// </para>
    /// </summary>
    event Action<Team, ulong, int>? PlayerPickedForTeam;

    /// <summary>
    /// Fires once when the map voting phase opens (payload: voteable map
    /// display names in the order they were presented, deadline in seconds
    /// from now). Deadline mirrors <c>DefaultVoteTimeSeconds</c>.
    /// </summary>
    event Action<IReadOnlyList<string>, int>? MapVotingStarted;

    /// <summary>
    /// Fires whenever a player casts (or changes) a map vote (payload:
    /// voter SteamID64, chosen map display name, previous map display name
    /// or null on first vote).
    /// </summary>
    event Action<ulong, string, string?>? MapVoteCast;

    /// <summary>
    /// Fires once when the map voting phase ends (payload: winning map
    /// display name, winning vote count). Vote count is 0 when the winner
    /// was picked at random due to zero votes.
    /// </summary>
    event Action<string, int>? MapVotingEnded;

    /// <summary>
    /// Fires once when the plugin transitions into <see cref="MatchState.KnifeRound"/>.
    /// </summary>
    event Action? KnifeRoundStarted;

    /// <summary>
    /// Fires when the knife round is decided (payload: winning team). Fires
    /// once regardless of whether the side pick is captain-driven or team-vote.
    /// </summary>
    event Action<Team>? KnifeRoundWon;

    /// <summary>
    /// Fires when the winning captain's side-selection menu opens (payload:
    /// captain SteamID64). Does not fire when <c>DisableCaptains</c> is true
    /// (see <see cref="StartingSideChosen"/> instead).
    /// </summary>
    event Action<ulong>? PickingStartingSideStarted;

    /// <summary>
    /// Fires when the winning team's starting side is decided (payload: the
    /// side the winning team KEPT, either <see cref="Team.CT"/> or
    /// <see cref="Team.T"/>). Fires for both captain choice and team vote paths.
    /// </summary>
    event Action<Team>? StartingSideChosen;

    /// <summary>
    /// Fires when a team timeout starts (payload: team, duration in seconds).
    /// Only fires when a timeout actually goes live — queued timeouts fire
    /// this event when they eventually run, not when they get queued.
    /// </summary>
    event Action<Team, int>? TimeoutStarted;

    /// <summary>
    /// Fires once per second while a timeout is active (payload: remaining
    /// seconds, counting down from the timeout duration to 1). Does not fire
    /// at 0 — the <see cref="TimeoutEnded"/> event handles the transition.
    /// </summary>
    event Action<int>? TimeoutTick;

    /// <summary>
    /// Fires when the active timeout ends (payload: team that was on timeout).
    /// Fires regardless of whether the plugin proceeds directly to another
    /// queued timeout or resumes the match.
    /// </summary>
    event Action<Team>? TimeoutEnded;

    /// <summary>
    /// Fires when a timeout vote opens (payload: team the vote is running for).
    /// </summary>
    event Action<Team>? TimeoutVoteStarted;

    /// <summary>
    /// Fires whenever a timeout vote is cast (payload: voter SteamID64,
    /// voteYes, team the vote is running for).
    /// </summary>
    event Action<ulong, bool, Team>? TimeoutVoteCast;

    /// <summary>
    /// Fires when a timeout vote resolves (payload: team, passed).
    /// </summary>
    event Action<Team, bool>? TimeoutVoteResult;

    /// <summary>
    /// Fires when a surrender vote opens (payload: team the vote is running for).
    /// </summary>
    event Action<Team>? SurrenderVoteStarted;

    /// <summary>
    /// Fires whenever a surrender vote is cast (payload: voter SteamID64,
    /// voteYes, team the vote is running for).
    /// </summary>
    event Action<ulong, bool, Team>? SurrenderVoteCast;

    /// <summary>
    /// Fires when a surrender vote resolves (payload: team, passed). A passing
    /// vote is followed by the natural match reset flow — no separate
    /// "surrendered" event is fired.
    /// </summary>
    event Action<Team, bool>? SurrenderVoteResult;

    /// <summary>
    /// Fires when a vote kick opens (payload: team, target SteamID64,
    /// initiator SteamID64).
    /// </summary>
    event Action<Team, ulong, ulong>? VoteKickStarted;

    /// <summary>
    /// Fires whenever a vote kick vote is cast. Payload is a dedicated record
    /// (see <see cref="VoteKickCastEventArgs"/>) because the natural argument
    /// list is over the plain-Action threshold.
    /// </summary>
    event Action<VoteKickCastEventArgs>? VoteKickCast;

    /// <summary>
    /// Fires when a vote kick resolves (payload: team, passed). A passing
    /// vote is followed by the target player getting kicked.
    /// </summary>
    event Action<Team, bool>? VoteKickResult;

    /// <summary>
    /// Fires once when the competitive match ends via <c>EventCsWinPanelMatch</c>
    /// (payload: winning team, CT score, T score). Winning team is derived from
    /// <c>CTScoreTotal</c> vs <c>TerroristScoreTotal</c>; a draw (equal scores)
    /// yields <see cref="Team.None"/>.
    /// </summary>
    event Action<Team, int, int>? MatchEnded;

    // =========================================================================
    // Menu request events (v2.1.0+)
    // =========================================================================
    //
    // MixScrims owns the !captain / !volunteer_captain chat commands (argument
    // parsing plus the "managemix" permission gate on !captain), so a consumer
    // cannot re-register them without a command-name collision. Instead, when
    // built-in menus are suppressed MixScrims announces the request and lets the
    // consumer render its own picker.
    //
    // BOTH events are INERT unless SetBuiltInMenusSuppressed(true) is in effect
    // (or the SuppressBuiltInMenus config flag is set). With suppression off,
    // MixScrims handles the command itself exactly as it always has.

    /// <summary>
    /// Fires when <c>!captain</c> passes its permission, state and argument
    /// checks AND built-in menus are suppressed (payload: requesting admin
    /// SteamID64, requested side). <c>side</c> is the parsed team when the admin
    /// supplied an argument (<c>!captain ct</c>), or <c>null</c> when they typed
    /// the bare command. Render your own picker and terminate the flow at
    /// <see cref="SetCtCaptain"/> / <see cref="SetTCaptain"/>.
    /// </summary>
    event Action<ulong, Team?>? CaptainMenuRequested;

    /// <summary>
    /// Fires when <c>!volunteer_captain</c> passes its state and argument checks
    /// AND built-in menus are suppressed (payload: requesting player SteamID64,
    /// requested side or <c>null</c> for the bare command). Render your own
    /// picker and terminate the flow at <see cref="VolunteerAsCaptain"/>, which
    /// performs the same eligibility checks the chat command would have.
    /// </summary>
    event Action<ulong, Team?>? VolunteerCaptainMenuRequested;

    /// <summary>
    /// Raised when a player asks to (re)open the map-vote ballot via
    /// <c>!revote</c> <b>and</b> built-in menus are suppressed. Fires only after
    /// the command's own state validation passes (match state must be
    /// <see cref="MatchState.MapVoting"/>), so a consumer can reopen its ballot
    /// without re-implementing those checks. Terminate the flow at
    /// <see cref="CastMapVote"/>.
    /// </summary>
    event Action<ulong>? MapVoteMenuRequested;

    // =========================================================================
    // Snapshot queries (v2.0.0+)
    // =========================================================================
    //
    // Every event above has a matching read here so consumers that connect or
    // hot-reload AFTER a state change can catch up without waiting for the next
    // fire. All methods are safe to call from any state — they return
    // sensible zero / empty values when the queried state isn't active.

    // -- Ready system --

    /// <summary>Returns the list of currently-ready human players (SteamID64).</summary>
    /// <remarks>
    /// Bots are <b>never</b> present here — they all share SteamID 0, which would
    /// collapse them onto one slot. For a ready <i>counter</i> use
    /// <see cref="GetEffectiveReadyCount"/> instead; this list is only correct for
    /// per-player questions.
    /// </remarks>
    IReadOnlyList<ulong> GetReadyPlayers();

    /// <summary>
    /// Ready count as the plugin itself counts it, including bots as implicitly
    /// ready while <c>TestMode</c> is on. This is the numerator MixScrims prints in
    /// its own ready announcement and uses for state transitions — mirror it rather
    /// than taking <c>GetReadyPlayers().Count</c>, which reads 0 in a bot lobby.
    /// </summary>
    int GetEffectiveReadyCount();

    /// <summary>Returns whether the given SteamID64 is currently marked ready.</summary>
    bool IsPlayerReady(ulong steamId);
    /// <summary>
    /// Minimum players required to advance out of Warmup / MapChosen (from
    /// <c>MinimumReadyPlayers</c> config).
    /// </summary>
    /// <remarks>
    /// Raw config value. It ignores <c>RequireAllConnectedPlayersToBeReady</c>, so
    /// for a ready counter's denominator use
    /// <see cref="GetPlayersRequiredToStart"/>.
    /// </remarks>
    int GetMinimumReadyPlayers();

    /// <summary>
    /// Denominator the plugin resolves for the current lobby: honours
    /// <c>RequireAllConnectedPlayersToBeReady</c> and counts bots, so it pairs
    /// exactly with <see cref="GetEffectiveReadyCount"/>.
    /// </summary>
    int GetPlayersRequiredToStart();

    /// <summary>
    /// Whether the plugin requires every connected player to be ready before
    /// advancing (<c>RequireAllConnectedPlayersToBeReady</c> config).
    /// </summary>
    bool GetRequireAllConnectedPlayersToBeReady();

    // -- Captains + team names --

    /// <summary>Current CT captain SteamID64 or null if unset / invalid.</summary>
    ulong? GetCtCaptain();

    /// <summary>Current T captain SteamID64 or null if unset / invalid.</summary>
    ulong? GetTCaptain();

    /// <summary>
    /// SteamID64 of the captain entitled to choose the starting side, or null when
    /// nobody is: captains are disabled (the winning team votes instead), the picker
    /// is a bot (MixScrims resolves those automatically), or the phase isn't active.
    /// </summary>
    /// <remarks>
    /// Snapshot pair for <see cref="PickingStartingSideStarted"/>. That event is
    /// raised once and skipped entirely if the captain's player ref went stale, so a
    /// consumer that only listens for it can miss the phase with no way to recover.
    /// Drive the prompt off this read on entering <see cref="MatchState.PickingStartingSide"/>.
    /// </remarks>
    ulong? GetStartingSidePicker();

    /// <summary>
    /// Current CT team display name (last value written via
    /// <see cref="SetCounterTerroristsTeamName"/>), or the CS2 default if unset.
    /// </summary>
    string GetCtTeamName();

    /// <summary>
    /// Current T team display name (last value written via
    /// <see cref="SetTerroristsTeamName"/>), or the CS2 default if unset.
    /// </summary>
    string GetTTeamName();

    // -- Match score --

    /// <summary>
    /// Reads live team scores from CS2's <c>MatchData</c> (<c>CTScoreTotal</c>,
    /// <c>TerroristScoreTotal</c>). Returns <c>(0, 0)</c> when game rules are
    /// unavailable (early load, map transition).
    /// </summary>
    (int Ct, int T) GetMatchScore();

    // -- Map voting --

    /// <summary>
    /// Current map vote tallies keyed by map display name. Empty outside the
    /// MapVoting state.
    /// </summary>
    IReadOnlyDictionary<string, int> GetMapVoteTallies();

    /// <summary>
    /// Map display names on the current ballot (in menu order). Empty outside
    /// the MapVoting state.
    /// </summary>
    IReadOnlyList<string> GetVoteableMapDisplayNames();

    /// <summary>
    /// Seconds remaining on the map vote deadline. Returns 0 outside the
    /// MapVoting state or after the deadline has elapsed.
    /// </summary>
    int GetMapVoteSecondsRemaining();

    /// <summary>
    /// Returns the map display name the given player currently backs during
    /// the map vote, or null if they haven't voted or aren't voting.
    /// </summary>
    string? GetPlayerMapVote(ulong steamId);

    // -- Picking phase --

    /// <summary>
    /// Team whose turn it is to pick during the PickingTeam phase, or null
    /// when not in the phase / picking is complete.
    /// </summary>
    Team? GetActivePickingTeam();

    /// <summary>
    /// Currently-unpicked SteamID64s during the PickingTeam phase, or an
    /// empty list outside the phase.
    /// </summary>
    IReadOnlyList<ulong> GetUnpickedPlayers();

    /// <summary>
    /// 1-based pick counter within the current PickingTeam phase (captains'
    /// implicit self-picks occupy 1 and 2). Returns 0 outside the phase.
    /// </summary>
    int GetCurrentPickIndex();

    // -- Timeout --

    /// <summary>
    /// Seconds remaining on the active timeout. Returns 0 outside the Timeout
    /// state or after the timeout has elapsed.
    /// </summary>
    int GetActiveTimeoutRemainingSeconds();

    /// <summary>
    /// Team on the active timeout, or null when no timeout is active.
    /// </summary>
    Team? GetActiveTimeoutTeam();

    /// <summary>
    /// Remaining timeouts for CT (starts at <c>Timeouts</c> config, decrements
    /// each time a CT timeout runs).
    /// </summary>
    int GetRemainingTimeoutsCt();

    /// <summary>Remaining timeouts for T.</summary>
    int GetRemainingTimeoutsT();

    // -- Vote kick --

    /// <summary>Current CT vote-kick target SteamID64, or null when none active.</summary>
    ulong? GetVoteKickTargetCt();

    /// <summary>Current T vote-kick target SteamID64, or null when none active.</summary>
    ulong? GetVoteKickTargetT();

    /// <summary>
    /// Current CT vote-kick tally (yes count, votes cast, eligible voters).
    /// All three are 0 when no vote is active.
    /// </summary>
    (int Yes, int Cast, int Eligible) GetVoteKickTallyCt();

    /// <summary>Same shape as <see cref="GetVoteKickTallyCt"/> for the T team.</summary>
    (int Yes, int Cast, int Eligible) GetVoteKickTallyT();

    // -- Surrender vote --

    /// <summary>Team whose surrender vote is in progress, or null when none active.</summary>
    Team? GetActiveSurrenderVoteTeam();

    /// <summary>
    /// Current surrender vote tally (yes count, votes cast, eligible voters).
    /// All three are 0 when no vote is active.
    /// </summary>
    (int Yes, int Cast, int Eligible) GetSurrenderVoteTally();

    // -- Localization pass-through --

    /// <summary>
    /// Resolves a localization string via MixScrims' translation files. When
    /// <paramref name="steamId"/> matches a currently-connected player their
    /// preferred locale is used; otherwise the server default is used. Missing
    /// keys are returned verbatim by the SwiftlyS2 localizer.
    /// </summary>
    string GetLocalizedString(ulong steamId, string key, params object[] args);

    // =========================================================================
    // Built-in presentation suppression (v2.0.0+)
    // =========================================================================
    //
    // A consumer plugin that wants to render the same information through a
    // richer UI (CustomHUD, external dashboard, etc.) can suppress the
    // built-in menus and center-HTML broadcasts entirely. Both switches are
    // opt-in — the built-in presentation is on by default.

    /// <summary>
    /// Runtime override of the <c>SuppressBuiltInMenus</c> config flag. When
    /// suppressed, MixScrims skips every <c>Core.MenusAPI.OpenMenuForPlayer</c>
    /// site (captain admin menu, side pick, map vote, team pick, surrender
    /// vote, timeout vote, vote kick). Overrides config until the plugin unloads.
    /// </summary>
    void SetBuiltInMenusSuppressed(bool suppressed);

    /// <summary>
    /// Runtime override of the <c>SuppressBuiltInCenterHtml</c> config flag.
    /// When suppressed, MixScrims skips every <c>SendCenterHTML</c> broadcast
    /// (ready counter, timeout timer, vote kick progress, surrender result).
    /// Overrides config until the plugin unloads.
    /// </summary>
    void SetBuiltInCenterHtmlSuppressed(bool suppressed);

    /// <summary>Current effective value of the built-in menu suppression switch.</summary>
    bool AreBuiltInMenusSuppressed();

    /// <summary>Current effective value of the built-in center-HTML suppression switch.</summary>
    bool IsBuiltInCenterHtmlSuppressed();

    // =========================================================================
    // Config value getters (v2.0.0+)
    // =========================================================================

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

    // =========================================================================
    // Match flow drivers (v2.1.0+)
    // =========================================================================
    //
    // Suppressing the built-in menus removes the only way a player could cast a
    // vote or make a pick. These drivers are the replacement input path: each is
    // a guarded pass-through to the same internal handler the built-in menu
    // button invokes, so the full downstream pipeline (tallies, events, phase
    // progression) runs identically.
    //
    // Every driver is a NO-OP when its preconditions aren't met (wrong match
    // state, unknown SteamID, player not on the voting team, no vote open,
    // caller isn't the active picker). Rejections log a warning on the server
    // and never throw back into the caller.

    /// <summary>
    /// Casts or changes <paramref name="steamId"/>'s map vote. No-op unless the
    /// match state is <see cref="MatchState.MapVoting"/>, the player is
    /// connected, and <paramref name="mapDisplayName"/> is on the current ballot
    /// (see <see cref="GetVoteableMapDisplayNames"/>). Raises
    /// <see cref="MapVoteCast"/> on success.
    /// </summary>
    void CastMapVote(ulong steamId, string mapDisplayName);

    /// <summary>
    /// Casts a Yes/No vote on the in-flight timeout vote. No-op when no timeout
    /// vote is open, the player is unknown / disconnected, the player isn't on
    /// the team the vote is running for, or they already voted. Raises
    /// <see cref="TimeoutVoteCast"/> on success.
    /// </summary>
    void CastTimeoutVote(ulong steamId, bool voteYes);

    /// <summary>
    /// Casts a Yes/No vote on the in-flight surrender vote. Same guard set as
    /// <see cref="CastTimeoutVote"/>. Raises <see cref="SurrenderVoteCast"/> on
    /// success.
    /// </summary>
    void CastSurrenderVote(ulong steamId, bool voteYes);

    /// <summary>
    /// Casts a Yes/No vote on a vote kick. <paramref name="team"/> disambiguates
    /// which side's vote is being answered — CT and T can run vote kicks
    /// simultaneously. No-op when that team has no vote open, the player is
    /// unknown / disconnected, the player isn't on <paramref name="team"/>, or
    /// they already voted. Raises <see cref="VoteKickCast"/> on success.
    /// </summary>
    void CastVoteKickVote(ulong steamId, Team team, bool voteYes);

    /// <summary>
    /// Drives the FULL team-pick pipeline: the pick index advances,
    /// <see cref="PlayerPickedForTeam"/> fires, the picked player is moved onto
    /// the team, and the phase progresses to the knife round once picking
    /// completes. This is what a consumer-built pick menu must call — the
    /// <c>AddPlayerToPicked*Players</c> methods only touch the roster list.
    /// No-op unless the match state is <see cref="MatchState.PickingTeam"/> and
    /// <paramref name="captainSteamId"/> is the captain of the currently-active
    /// picking team (see <see cref="GetActivePickingTeam"/>).
    /// </summary>
    void PickPlayerForTeam(ulong captainSteamId, ulong pickedSteamId);

    /// <summary>
    /// Slot-keyed companion to <see cref="GetUnpickedPlayers"/>. Bots have a
    /// SteamID of <c>0</c> and are therefore not addressable by the SteamID
    /// overloads, but MixScrims' own picking flow <b>does</b> allow picking
    /// them — which matters in <c>TestMode</c> lobbies where the pickable pool
    /// is mostly bots. Use this (with <see cref="PickPlayerForTeamBySlot"/>)
    /// to build a pick menu that behaves like the built-in one.
    /// Returns the player slot (<c>IPlayer.PlayerID</c>) of every valid,
    /// not-yet-picked player, bots included. Empty unless the match state is
    /// <see cref="MatchState.PickingTeam"/>.
    /// </summary>
    IReadOnlyList<int> GetUnpickedPlayerSlots();

    /// <summary>
    /// Slot-keyed companion to <see cref="PickPlayerForTeam"/>, so bots (SteamID
    /// <c>0</c>) can be picked. Same guards and same full pipeline: the pick
    /// index advances, <see cref="PlayerPickedForTeam"/> fires, the player is
    /// moved onto the team, and the phase progresses once picking completes.
    /// No-op unless the match state is <see cref="MatchState.PickingTeam"/> and
    /// <paramref name="captainSteamId"/> is the captain of the currently-active
    /// picking team.
    /// </summary>
    /// <param name="captainSteamId">The captain making the pick. Captains are always human, so a SteamID is safe here.</param>
    /// <param name="pickedSlot">Player slot (<c>IPlayer.PlayerID</c>) of the pick target.</param>
    void PickPlayerForTeamBySlot(ulong captainSteamId, int pickedSlot);

    /// <summary>
    /// Volunteers <paramref name="steamId"/> as captain of <paramref name="team"/>,
    /// honouring the same checks the <c>!volunteer_captain</c> chat command
    /// performs (<c>AllowVolunteerCaptains</c> enabled, captains not disabled,
    /// Warmup / MapLoading / MapChosen state, target slot still free, player not
    /// already a captain). Distinct from <see cref="SetCtCaptain"/> /
    /// <see cref="SetTCaptain"/>, which are admin-force setters that bypass
    /// those checks.
    /// </summary>
    void VolunteerAsCaptain(ulong steamId, Team team);

    /// <summary>
    /// Records a starting-side choice after the knife round. Routes to the same
    /// handler the <c>!stay</c> / <c>!switch</c> chat commands and the built-in
    /// side-pick menu use, so both captain modes work transparently:
    /// <list type="bullet">
    ///   <item><description><b>Captains enabled</b> — the winning captain's
    ///     choice applies immediately (sides swap or hold) and
    ///     <see cref="StartingSideChosen"/> fires.</description></item>
    ///   <item><description><b><c>DisableCaptains</c> = true</b> — the call is
    ///     recorded as one vote from a winning-team player. Once every valid
    ///     winning-team player has voted, the majority is applied and
    ///     <see cref="StartingSideChosen"/> fires.</description></item>
    /// </list>
    /// No-op unless the match state is
    /// <see cref="MatchState.PickingStartingSide"/> and the caller is eligible
    /// to choose (winning captain, or a member of the winning team when
    /// captains are disabled).
    /// <para>
    /// The decision is one-shot per phase: once a side has been committed, later
    /// calls are ignored (logged as a warning) even though
    /// <see cref="MatchState"/> briefly still reads
    /// <see cref="MatchState.PickingStartingSide"/> while the match start is
    /// dispatched. Consumers therefore do not need to close their own prompt
    /// before the driver returns to stay correct, but should still guard
    /// double-clicks to avoid the wasted round trip.
    /// </para>
    /// </summary>
    /// <param name="steamId">The player making the choice.</param>
    /// <param name="stay"><c>true</c> to keep the current sides, <c>false</c> to swap.</param>
    void ChooseStartingSide(ulong steamId, bool stay);

    // =========================================================================
    // Original v1.x surface (preserved verbatim for backward compatibility)
    // =========================================================================

    /// <summary>
    /// Retrieves the current state of the match.
    /// </summary>
    MatchState GetCurrentMatchState();
    /// <summary>
    /// Sets the current match state to the specified value.
    /// </summary>
    void SetMatchState(MatchState state);
    /// <summary>
    /// Retrieves the current operational state of the plugin.
    /// </summary>
    PluginState GetCurrentPluginState();
    /// <summary>
    /// Sets the current state of the plugin.
    /// </summary>
    void SetPluginState(PluginState state);
    /// <summary>
    /// Sets the display name for the Counter-Terrorists team.
    /// </summary>
    void SetCounterTerroristsTeamName(string name);
    /// <summary>
    /// Sets the display name for the terrorists team.
    /// </summary>
    void SetTerroristsTeamName(string name);
    /// <summary>
    /// Initiates the warmup process to prepare the component for operation.
    /// </summary>
    void StartWarmup();
    /// <summary>
    /// Initiates the map voting process, allowing participants to vote for the next map.
    /// </summary>
    void StartMapVoting();
    /// <summary>
    /// Initiates the process of selecting teams for the current session.
    /// </summary>
    void StartTeamPicking();
    /// <summary>
    /// Starts the timeout cancellation token, enabling timeout monitoring for the current operation.
    /// </summary>
    void StartTimeoutCt();
    /// <summary>
    /// 
    /// </summary>
    void StartTimeoutT();
    /// <summary>
    /// Stops the timeout cancellation token, preventing any further timeout-triggered cancellation operations.
    /// </summary>
    void StopTimeout();
    /// <summary>
    /// Initiates the surrender process for the current Counter-Terrorist team.
    /// </summary>
    void SurrenderCt();
    /// <summary>
    /// Performs a surrender action for the current entity or operation.
    /// </summary>
    void SurrenderT();
    /// <summary>
    /// Begins a new match, initializing all necessary game state and resources.
    /// </summary>
    void StartMatch();
    /// <summary>
    /// Starts a knife round phase in the game, typically used to determine which team selects a side.
    /// </summary>
    void StartKnifeRound();
    /// <summary>
    /// Cancels the current match, terminating any ongoing gameplay or matchmaking process.
    /// </summary>
    void CancelMatch();
    /// <summary>
    /// Changes the current map to the specified map or workshop map.
    /// </summary>
    void ChangeMap(string mapName = "", string workshopId = "");
    /// <summary>
    /// Sets all players in the game to a ready state, regardless of their current status.
    /// </summary>
    void ForceAllPlayersToReady();
    /// <summary>
    /// Forces all players in the session to become unready, overriding their current ready status.
    /// </summary>
    void ForceAllPlayersToUnready();
    /// <summary>
    /// Retrieves a list of player identifiers that have been selected for the Counter-Terrorist team.
    /// </summary>
    List<ulong> GetPickedCtPlayers();
    /// <summary>
    /// Retrieves a list of player identifiers that have been selected.
    /// </summary>
    List<ulong> GetPickedTPlayers();
    /// <summary>
    /// Adds a player to the collection of picked players using the specified Steam ID.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// advance the picking phase. To drive team picking from a custom menu, use
    /// <see cref="PickPlayerForTeam"/>.</para>
    /// </summary>
    void AddPlayerToPickedCtPlayers(ulong steamId);
    /// <summary>
    /// Adds a player to the collection of picked players using the specified Steam ID.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// advance the picking phase. To drive team picking from a custom menu, use
    /// <see cref="PickPlayerForTeam"/>.</para>
    /// </summary>
    void AddPlayerToPickedTPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of picked players.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// rewind the picking phase.</para>
    /// </summary>
    void RemovePlayerFromPickedCtPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of picked players.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// rewind the picking phase.</para>
    /// </summary>
    void RemovePlayerFromPickedTPlayers(ulong steamId);
    /// <summary>
    /// Retrieves a list of player identifiers for all players currently in the playing state.
    /// </summary>
    List<ulong> GetPlayingCtPlayers();
    /// <summary>
    /// Retrieves a list of player identifiers for all players currently in a playing state.
    /// </summary>
    List<ulong> GetPlayingTPlayers();
    /// <summary>
    /// Adds a player to the collection of currently playing players using the specified Steam ID.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// advance the picking phase. To drive team picking from a custom menu, use
    /// <see cref="PickPlayerForTeam"/>.</para>
    /// </summary>
    void AddPlayerToPlayingCtPlayers(ulong steamId);
    /// <summary>
    /// Adds a player to the collection of currently playing players using the specified Steam ID.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// advance the picking phase. To drive team picking from a custom menu, use
    /// <see cref="PickPlayerForTeam"/>.</para>
    /// </summary>
    void AddPlayerToPlayingTPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of currently playing players.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// alter the match phase.</para>
    /// </summary>
    void RemovePlayerFromPlayingCtPlayers(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the collection of currently playing players.
    /// <para><b>Roster list manipulation only</b> — does not move the player or
    /// alter the match phase.</para>
    /// </summary>
    void RemovePlayerFromPlayingTPlayers(ulong steamId);
    /// <summary>
    /// Retrieves a list of player Steam IDs who are awaiting punishment actions associated with the specified user.
    /// </summary>
    List<ulong> GetPlayersWaitingForPunishment(ulong steamId);
    /// <summary>
    /// Adds a player, identified by their Steam ID, from the waiting-for-punishment list to the active player list or
    /// system.
    /// </summary>
    void AddPlayerToWaitingForPunishmentList(ulong steamId);
    /// <summary>
    /// Removes the player with the specified Steam ID from the waiting-for-punishmentslist.
    /// </summary>
    void RemovePlayerFromWaitingForPunishmentList(ulong steamId);
    /// <summary>
    /// Assigns the captain role to the player identified by the specified Steam ID.
    /// </summary>
    void SetCtCaptain(ulong steamId);
    /// <summary>
    /// Assigns the captain role to the player identified by the specified Steam ID.
    /// </summary>
    void SetTCaptain(ulong steamId);
    /// <summary>
    /// Kicks players who are not actively participating in the game.
    /// </summary>
    void KickNotPlayingPlayers(string? reason = "");
    /// <summary>
    /// Removes all players who have not been picked from the game session.
    /// </summary>
    void KickNotPickedPlayers(string? reason = "");
    /// <summary>
    /// Prevents additional players from joining the ongoing match. This is automatically disabled when the match ends.
    /// </summary>
    void PreventNewPlayersJoining(bool value = false);
}
