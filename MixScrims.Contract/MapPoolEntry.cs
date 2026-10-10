namespace MixScrims.Contract;

/// <summary>
/// One entry of MixScrims' configured map pool, as returned by <see cref="IMixScrims.GetConfiguredMapPool"/>.
/// Uses a record because the four fields include three adjacent strings, which a tuple would let a caller transpose silently.
/// </summary>
/// <param name="MapName">Map file name such as <c>de_mirage</c>, and one of the two values <see cref="IMixScrims.ChangeMap"/> accepts as its <c>mapName</c>.</param>
/// <param name="DisplayName">Name shown in menus, and the other value <see cref="IMixScrims.ChangeMap"/> accepts as its <c>mapName</c>.</param>
/// <param name="WorkshopId">Workshop item id with any <c>ws:</c> prefix already stripped, or empty for an official map.</param>
/// <param name="CanBeVoted">Whether the map is eligible for map voting; one excluded from votes is still loadable through <see cref="IMixScrims.ChangeMap"/>.</param>
public sealed record MapPoolEntry(
    string MapName,
    string DisplayName,
    string WorkshopId,
    bool CanBeVoted)
{
    /// <summary>Whether the entry loads from the Workshop. Derived from <see cref="WorkshopId"/>, so the two cannot disagree.</summary>
    public bool IsWorkshop => !string.IsNullOrEmpty(WorkshopId);
}
