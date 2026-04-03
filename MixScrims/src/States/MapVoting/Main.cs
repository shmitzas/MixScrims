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
        var playerName = player.Name ?? $"#{player.PlayerID}";

        if (cfg.DetailedLogging)
            logger.LogInformation("Player {Player} voted for map {Map}", playerName, mapDisplayName);

        var votedMap = cfg.Maps.FirstOrDefault(m => string.Equals(m.DisplayName, mapDisplayName, StringComparison.OrdinalIgnoreCase));
        if (votedMap == null)
        {
            logger.LogError("RegisterMapVote: Map not found in configuration: {Map}", mapDisplayName);
            PrintMessageToPlayer(player, Core.Localizer["error.map_not_found", mapDisplayName]);
            DisplayMapVotingMenu(player);
            return;
        }

        var previouslyVoted = votedMaps.FirstOrDefault(m => m.VotedBy.Any(v => v == player.PlayerID));
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
            votedMaps.Add(new VotedMap { Map = votedMap, Votes = 1, VotedBy = [player.PlayerID] });
            votes = 1;
        }

        PrintMessageToPlayer(player, Core.Localizer["announcement.map.voted", playerName, votedMap.DisplayName, votes]);

        CloseMenuForPlayer(player);
    }

    /// <summary>
    /// Displays a map voting menu to the specified player, allowing them to revote on a list of maps.
    /// </summary>
    internal void DisplayMapVotingMenu(IPlayer player)
    {
        if (mapVotingMenu == null)
        {
            logger.LogError("DisplayMapVotingMenu: mapVotingMenu is null");
            return;
        }

        if (player == null || !IsPlayerValid(player) || IsBot(player))
            return;

        try
        {
            Core.MenusAPI.OpenMenuForPlayer(player, mapVotingMenu);
        }
        catch (Exception ex)
        {
            logger.LogError("Error displaying map voting menu to {Player}: {Error}", player.Name, ex);
        }
    }

    /// <summary>
    /// Announces the map selected for the match and updates the match state accordingly.
    /// </summary>
    internal void AnnouncePickedMap()
    {
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

        mixScrimsService.SetMatchState(MatchState.MapChosen);

        VotedMap pickedMap = GetMostVotedMap();
        PrintMessageToAllPlayers(Core.Localizer["announcement.map.chosen", pickedMap.Map.DisplayName, pickedMap.Votes]);
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
