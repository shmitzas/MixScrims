namespace MixScrims;

public class MapsConfig
{
    public List<MapDetails> Maps { get; set; } =
    [
        new() { MapName = "de_mirage", DisplayName = "Mirage", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_dust2", DisplayName = "Dust2", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_inferno", DisplayName = "Inferno", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_anubis", DisplayName = "Anubis", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_overpass", DisplayName = "Overpass", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_ancient", DisplayName = "Ancient", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_ancient_night", DisplayName = "Ancient Night", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_nuke", DisplayName = "Nuke", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false },
        new() { MapName = "de_vertigo", DisplayName = "Vertigo", WorkshopId = "", CanBeVoted = true, IsWorkshopMap = false }
    ];
}
