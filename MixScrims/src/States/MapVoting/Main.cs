using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;
using MixScrims.Contract;

namespace MixScrims;

public partial class MixScrims
{
    internal List<VotedMap> votedMaps { get; set; } = [];
    internal IMenuAPI? mapVotingMenu { get; set; } = null;
    // Rebuilt every StartMapVotingPhase and cleared when the vote closes.
    internal List<string> currentVoteMapNames = new();
    internal DateTime? mapVoteDeadline = null;

    // A vote running without owning the match state: the caller keeps the result instead of
    // MixScrims loading it. Mirrors rtvTriggeredMapVote, which likewise only alters the exit.
    internal bool deferredMapVoteActive = false;

    // Bumped by every open and every close, so an armed deadline callback from a superseded
    // vote recognises itself as stale. Cancelling the token alone cannot cover the close paths.
    internal int mapVoteEpoch = 0;

    /// <summary>Whether a vote is accepting ballots, in either mode.</summary>
    internal bool MapVoteIsOpen => MatchState == MatchState.MapVoting || deferredMapVoteActive;

    // Membership is what stops a later retry reopening a vote the player has already answered.
    private readonly HashSet<int> mapVotePendingJoiners = [];

    // Retried because IsPlayerValid demands a pawn, which a joiner lacks for a second or two.
    private static readonly float[] MapVoteJoinerRetryDelays = [2f, 5f, 10f];

    /// <summary>
    /// Presents map voting options to players and starts the map voting phase
    /// </summary>
    internal void StartMapVotingPhase() => StartMapVotingCore(deferred: false, cfg.DefaultVoteTimeSeconds);

    /// <summary>
    /// Opens a vote that leaves the match state alone and parks the result for the caller.
    /// </summary>
    /// <returns>Whether the vote opened.</returns>
    internal bool StartDeferredMapVotePhase(int voteSeconds)
    {
        if (MatchState == MatchState.MapVoting)
        {
            logger.LogWarning("StartDeferredMapVote: ignored, a map voting phase already owns the match state.");
            return false;
        }
        if (deferredMapVoteActive)
        {
            logger.LogWarning("StartDeferredMapVote: ignored, a deferred vote is already running.");
            return false;
        }

        return StartMapVotingCore(deferred: true, voteSeconds > 0 ? voteSeconds : cfg.DefaultVoteTimeSeconds);
    }

    private bool StartMapVotingCore(bool deferred, int voteSeconds)
    {
        StopPreMatchAnnouncementTimers();

        if (cfg.DetailedLogging)
            logger.LogInformation("StartMapVotingPhase (deferred={Deferred}, seconds={Seconds})", deferred, voteSeconds);
        if (!deferred)
            mixScrimsService.SetMatchState(MatchState.MapVoting);
        // Assigned rather than only set: a native vote started over a deferred one supersedes it.
        deferredMapVoteActive = deferred;
        mapVoteEpoch++;
        votedMaps.Clear();
        mapVotePendingJoiners.Clear();
        // Deferred callers frame the vote themselves; this line describes a phase change.
        if (!deferred)
            PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.map_voting"]);

        var mapsToVote = GetMapsToVote();
        if (mapsToVote.Count == 0)
        {
            if (deferred)
            {
                deferredMapVoteActive = false;
                logger.LogWarning("StartDeferredMapVote: no maps are available to vote on.");
                return false;
            }

            PrintMessageToAllPlayers(Core.Localizer["error.no_maps_configured"]);
            logger.LogError("No maps available for voting. Check your configuration.");
            mixScrimsService.SetMatchState(MatchState.Reset);
            return false;
        }

        // shuffle maps order
        mapsToVote = mapsToVote.OrderBy(_ => Guid.NewGuid()).ToList();

        currentVoteMapNames = mapsToVote.Select(m => m.Name).ToList();
        mapVoteDeadline = DateTime.UtcNow.AddSeconds(voteSeconds);
        mixScrimsService.RaiseMapVotingStarted(currentVoteMapNames.AsReadOnly(), voteSeconds);

        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.map_voting"])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        if (cfg.DetailedLogging)
            logger.LogInformation("StartMapVotingPhase: {Count} maps available", mapsToVote.Count);

        foreach (var map in mapsToVote)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("  - {Map}", map.Name);
            var button = new ButtonMenuOption(map.Name);
            button.Click += async (sender, args) =>
            {
                RegisterMapVoteByName(args.Player, map.Name);
                await ValueTask.CompletedTask;
            };
            builder.AddOption(button);
        }

        mapVotingMenu = null;
        mapVotingMenu = builder.Build();

        var players = GetPlayers();
        foreach (var player in players)
        {
            if (player == null || !IsPlayerValid(player) || IsBot(player))
                continue;

            DisplayMapVotingMenu(player);
        }

        var epoch = mapVoteEpoch;
        var token = Core.Scheduler.DelayBySeconds(voteSeconds, () =>
        {
            // Stale once any later open or close bumped the epoch, which is what keeps an
            // early close from being followed by this callback announcing a second time.
            if (mapVoteEpoch != epoch) return;
            AnnouncePickedMap();
        });
        Core.Scheduler.StopOnMapChange(token);
        return true;
    }

    /// <summary>
    /// Closes a running deferred vote immediately and announces the winner.
    /// </summary>
    /// <returns>Whether a deferred vote was running.</returns>
    internal bool EndDeferredMapVoteNow()
    {
        if (!deferredMapVoteActive) return false;
        AnnouncePickedMap();
        return true;
    }

    /// <summary>
    /// Registers a player's vote by map display name.
    /// </summary>
    internal void RegisterMapVoteByName(IPlayer player, string mapDisplayName)
    {
        if (player is null)
        {
            logger.LogError("RegisterMapVoteByName: player is null");
            return;
        }

        // Built-in menu clicks arrive off the game thread; only PlayerID survives the hop.
        var slot = player.PlayerID;
        Core.Scheduler.NextTick(() => RegisterMapVoteOnGameThread(slot, mapDisplayName));
    }

    private void RegisterMapVoteOnGameThread(int slot, string mapDisplayName)
    {
        var player = Core.PlayerManager.GetPlayer(slot);
        if (player is null || !IsPlayerValid(player))
        {
            logger.LogWarning("RegisterMapVoteByName: slot {Slot} left before its {Map} vote could be counted.", slot, mapDisplayName);
            return;
        }

        var playerName = SafePlayerName(player);

        if (cfg.DetailedLogging)
            logger.LogInformation("Player {Player} voted for map {Map}", playerName, mapDisplayName);

        var votedMap = mapsConfig.Maps.FirstOrDefault(m => string.Equals(m.Name, mapDisplayName, StringComparison.OrdinalIgnoreCase));
        if (votedMap == null)
        {
            logger.LogError("RegisterMapVote: Map not found in configuration: {Map}", mapDisplayName);
            PrintMessageToPlayer(player, Core.Localizer["error.map_not_found", mapDisplayName]);
            DisplayMapVotingMenu(player);
            return;
        }

        var previouslyVoted = votedMaps.FirstOrDefault(m => m.VotedBy.Any(v => v == player.PlayerID));
        string? previousDisplayName = previouslyVoted?.Map?.Name;
        if (previouslyVoted != null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("{Player} already voted for {Prev}. Removing vote...", playerName, previouslyVoted.Map.Name);
            previouslyVoted.Votes = Math.Max(0, previouslyVoted.Votes - 1);
            previouslyVoted.VotedBy.Remove(player.PlayerID);
        }

        var existingVote = votedMaps.FirstOrDefault(m =>
            string.Equals(m.Map.CanonicalId, votedMap.CanonicalId, StringComparison.OrdinalIgnoreCase));
        int votes;
        if (existingVote != null)
        {
            existingVote.Votes++;
            existingVote.VotedBy.Add(player.PlayerID);
            votes = existingVote.Votes;
        }
        else
        {
            votedMaps.Add(new VotedMap { Map = votedMap, Votes = 1, VotedBy = new List<int> { player.PlayerID } });
            votes = 1;
        }

        // Fire AFTER tallies are updated so a subscriber reading GetMapVoteTallies inside
        // the handler sees the post-cast counts.
        ulong voterSid;
        try { voterSid = player.SteamID; } catch { voterSid = 0; }
        if (voterSid != 0)
            mixScrimsService.RaiseMapVoteCast(voterSid, votedMap.Name, previousDisplayName);

        PrintMessageToAllPlayers(Core.Localizer["announcement.map.voted", playerName, votedMap.Name, votes]);

        CloseMenuForPlayer(player);
    }

    /// <summary>
    /// Displays a map voting menu to the specified player, allowing them to revote on a list of maps.
    /// </summary>
    internal void DisplayMapVotingMenu(IPlayer player)
    {
        if (player == null || !IsPlayerValid(player) || IsBot(player))
            return;

        if (suppressBuiltInMenus)
        {
            // A consumer owns the vote UI — announce the request instead of
            // opening ours, so !revote keeps working under suppression.
            mixScrimsService.RaiseMapVoteMenuRequested(player.SteamID);
            return;
        }

        if (mapVotingMenu == null)
        {
            logger.LogError("DisplayMapVotingMenu: mapVotingMenu is null");
            return;
        }

        try
        {
            Core.MenusAPI.OpenMenuForPlayer(player, mapVotingMenu);
        }
        catch (Exception ex)
        {
            logger.LogError("Error displaying map voting menu to {Player}: {Error}", SafePlayerName(player), ex);
        }
    }

    /// <summary>
    /// Opens the map vote for a player who connected after <see cref="StartMapVotingPhase"/> ran.
    /// </summary>
    /// <remarks>Always deferred - opening a menu inline on the connect hook crashes the host.</remarks>
    internal void ScheduleMapVoteForJoiner(int playerSlot)
    {
        if (!mapVotePendingJoiners.Add(playerSlot))
            return;

        foreach (var delaySeconds in MapVoteJoinerRetryDelays)
        {
            var token = Core.Scheduler.DelayBySeconds(delaySeconds, () =>
            {
                try
                {
                    if (!MapVoteIsOpen) return;
                    if (!mapVotePendingJoiners.Contains(playerSlot)) return;

                    var player = Core.PlayerManager.GetPlayer(playerSlot);
                    if (player == null || !IsPlayerValid(player) || IsBot(player)) return;

                    mapVotePendingJoiners.Remove(playerSlot);
                    if (cfg.DetailedLogging)
                        logger.LogInformation("ScheduleMapVoteForJoiner: opening the vote for slot {Slot} after {Delay}s.", playerSlot, delaySeconds);
                    DisplayMapVotingMenu(player);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ScheduleMapVoteForJoiner: failed to open the vote for slot {Slot}.", playerSlot);
                }
            });
            Core.Scheduler.StopOnMapChange(token);
        }
    }

    /// <summary>
    /// Announces the map selected for the match and updates the match state accordingly.
    /// </summary>
    internal void AnnouncePickedMap()
    {
        var deferred = deferredMapVoteActive;

        // The vote timer is only StopOnMapChange, so an abandoned phase leaves it armed to fire
        // in Warmup and force-load a map.
        if (!deferred && MatchState != MatchState.MapVoting)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("AnnouncePickedMap: ignored, map voting is no longer running (state={State}).", MatchState);
            return;
        }

        var players = GetPlayers();

        foreach (var player in players)
        {
            if (player == null)
            {
                logger.LogError("AnnouncePickedMap: player is null");
                continue;
            }

            var currentMenu = Core.MenusAPI.GetCurrentMenu(player);
            if (mapVotingMenu != null && currentMenu != null)
            {
                Core.MenusAPI.CloseMenuForPlayer(player, currentMenu);
            }
        }

        if (deferred)
        {
            // Closing before the deadline is what makes "30s or match end, whichever is
            // shorter" work: the bump strands the armed callback.
            deferredMapVoteActive = false;
            mapVoteEpoch++;

            VotedMap deferredPick = GetMostVotedMap();
            PrintMessageToAllPlayers(Core.Localizer["announcement.map.chosen", deferredPick.Map.Name, deferredPick.Votes]);
            mixScrimsService.RaiseMapVotingEnded(deferredPick.Map.Name, deferredPick.Votes);
            currentVoteMapNames.Clear();
            mapVotePendingJoiners.Clear();
            mapVoteDeadline = null;
            // No state change and no map load - the caller owns the result.
            return;
        }

        if (rtvTriggeredMapVote)
        {
            // RTV-driven map vote: behave like a manual !map. Stay in Warmup across the
            // map load (LoadSelectedMap captures stateBeforeMapLoading = Warmup, and
            // mapLoadedFromMatchFlow stays false so HandleMapChosenNewMapLoad restores Warmup).
            mixScrimsService.SetMatchState(MatchState.Warmup);
            rtvTriggeredMapVote = false;
        }
        else
        {
            mixScrimsService.SetMatchState(MatchState.MapChosen);
            mapLoadedFromMatchFlow = true;
        }

        VotedMap pickedMap = GetMostVotedMap();
        PrintMessageToAllPlayers(Core.Localizer["announcement.map.chosen", pickedMap.Map.Name, pickedMap.Votes]);
        mixScrimsService.RaiseMapVotingEnded(pickedMap.Map.Name, pickedMap.Votes);
        currentVoteMapNames.Clear();
        mapVotePendingJoiners.Clear();
        mapVoteDeadline = null;
        LoadSelectedMap(pickedMap.Map);
    }

    /// <summary>
    /// Return the map with the most votes. If there is an error, a random map is selected.
    /// </summary>
    internal VotedMap GetMostVotedMap()
    {
        var mostVotedMap = votedMaps.OrderByDescending(m => m.Votes).FirstOrDefault();

        if (mostVotedMap == null)
        {
            logger.LogWarning("GetMostVotedMap: mostVotedMap is null, picking random map");
            return new()
            {
                Map = GetRandomMap(),
                Votes = 0,
                VotedBy = []
            };
        }

        return mostVotedMap;
    }
}
