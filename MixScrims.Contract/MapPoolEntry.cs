namespace MixScrims.Contract;

/// <summary>
/// One entry of MixScrims' configured map pool, as returned by <see cref="IMixScrims.GetConfiguredMapPool"/>.
/// Uses a record because the two adjacent strings would be transposable silently in a tuple.
/// </summary>
/// <param name="Name">Name shown in menus, and one of the two values <see cref="IMixScrims.ChangeMap"/> accepts as its <c>mapName</c>.</param>
/// <param name="Id">The map's identifier — a bare workshop item id with any <c>ws:</c> prefix already stripped, or an official map name such as <c>de_mirage</c>. Never empty, and accepted by <see cref="IMixScrims.ChangeMap"/> as its <c>mapName</c> for either kind of map.</param>
public sealed record MapPoolEntry(
    string Name,
    string Id)
{
    /// <summary>Whether the entry loads from the Workshop. Derived from <see cref="Id"/>, so the two cannot disagree.</summary>
    public bool IsWorkshop =>
        Id.StartsWith("ws:", StringComparison.OrdinalIgnoreCase) || long.TryParse(Id, out _);
}
