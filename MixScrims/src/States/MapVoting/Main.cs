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
    // Votable map list + deadline snapshot for IMixScrims consumers (v2.0.0+). Rebuilt every
    // StartMapVotingPhase and cleared when the vote closes.
    internal List<string> currentVoteMapNames = new();
    internal DateTime? mapVoteDeadline = null;

    // Slots still owed a mid-vote menu. Membership is what stops a later retry reopening a
    // vote the player has already answered.
    private readonly HashSet<int> mapVotePendingJoiners = [];

    // Retried because IsPlayerValid demands a pawn, which a joiner lacks for a second or two.
    private static readonly float[] MapVoteJoinerRetryDelays = [2f, 5f, 10f];

    /// <summary>
    /// Presents map voting options to players and starts the map voting phase
    /// </summary>
    internal void StartMapVotingPhase()
    {
        StopPreMatchAnnouncementTimers();

        if (cfg.DetailedLogging)
            logger.LogInformation("StartMapVotingPhase");
        mixScrimsService.SetMatchState(MatchState.MapVoting);
        votedMaps.Clear();
        mapVotePendingJoiners.Clear();
        PrintMessageToAllPlayers(Core.Localizer["announcement.state_changed.map_voting"]);

        var mapsToVote = GetMapsToVote();
        if (mapsToVote.Count == 0)
        {
            PrintMessageToAllPlayers(Core.Localizer["error.no_maps_configured"]);
            logger.LogError("No maps available for voting. Check your configuration.");
            mixScrimsService.SetMatchState(MatchState.Reset);
            return;
        }

        // shuffle maps order
        mapsToVote = mapsToVote.OrderBy(_ => Guid.NewGuid()).ToList();

        // Snapshot the votable maps + deadline for IMixScrims consumers before we build the menu.
        currentVoteMapNames = mapsToVote.Select(m => m.DisplayName).ToList();
        mapVoteDeadline = DateTime.UtcNow.AddSeconds(cfg.DefaultVoteTimeSeconds);
        mixScrimsService.RaiseMapVotingStarted(currentVoteMapNames.AsReadOnly(), cfg.DefaultVoteTimeSeconds);

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
                logger.LogInformation("  - {Map}", map.DisplayName);
            var button = new ButtonMenuOption(map.DisplayName);
            button.Click += async (sender, args) =>
            {
                RegisterMapVoteByName(args.Player, map.DisplayName);
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

        var token = Core.Scheduler.DelayBySeconds(cfg.DefaultVoteTimeSeconds, AnnouncePickedMap);
        Core.Scheduler.StopOnMapChange(token);
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

        // SwiftlyS2 dispatches built-in menu clicks through Task.Run, so callers reach us on a
        // thread-pool thread where every native read below is an uncatchable AV. PlayerID is a
        // plain managed field, so it is the only thing safe to carry across the hop.
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

        var votedMap = mapsConfig.Maps.FirstOrDefault(m => string.Equals(m.DisplayName, mapDisplayName, StringComparison.OrdinalIgnoreCase));
        if (votedMap == null)
        {
            logger.LogError("RegisterMapVote: Map not found in configuration: {Map}", mapDisplayName);
            PrintMessageToPlayer(player, Core.Localizer["error.map_not_found", mapDisplayName]);
            DisplayMapVotingMenu(player);
            return;
        }

        var previouslyVoted = votedMaps.FirstOrDefault(m => m.VotedBy.Any(v => v == player.PlayerID));
        string? previousDisplayName = previouslyVoted?.Map?.DisplayName;
        if (previouslyVoted != null)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("{Player} already voted for {Prev}. Removing vote...", playerName, previouslyVoted.Map.DisplayName);
            previouslyVoted.Votes = Math.Max(0, previouslyVoted.Votes - 1);
            previouslyVoted.VotedBy.Remove(player.PlayerID);
        }

        var existingVote = votedMaps.FirstOrDefault(m => m.Map.MapName == votedMap.MapName);
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
            mixScrimsService.RaiseMapVoteCast(voterSid, votedMap.DisplayName, previousDisplayName);

        PrintMessageToAllPlayers(Core.Localizer["announcement.map.voted", playerName, votedMap.DisplayName, votes]);

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
    /// Opens the vote for a player who connected after <see cref="StartMapVotingPhase"/> ran,
    /// covering both the built-in menu and the suppressed consumer-rendered path. Deferred,
    /// never opened inline: the connect hook is the call stack where opening a menu crashed the host.
    /// </summary>
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
                    if (MatchState != MatchState.MapVoting) return;
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
        // The vote timer is only StopOnMapChange, so a phase abandoned before it elapses
        // leaves it armed — it would otherwise fire in Warmup and force-load a map.
        if (MatchState != MatchState.MapVoting)
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
        PrintMessageToAllPlayers(Core.Localizer["announcement.map.chosen", pickedMap.Map.DisplayName, pickedMap.Votes]);
        mixScrimsService.RaiseMapVotingEnded(pickedMap.Map.DisplayName, pickedMap.Votes);
        // Voting is closed — clear the snapshot state so consumers get an empty map list
        // between votes rather than the stale one.
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
