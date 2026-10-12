namespace MixScrims;

public class MapsConfig
{
    public List<MapDetails> Maps { get; set; } =
    [
        new() { Name = "Mirage", Id = "de_mirage" },
        new() { Name = "Dust2", Id = "de_dust2" },
        new() { Name = "Inferno", Id = "de_inferno" },
        new() { Name = "Anubis", Id = "de_anubis" },
        new() { Name = "Overpass", Id = "de_overpass" },
        new() { Name = "Ancient", Id = "de_ancient" },
        new() { Name = "Ancient Night", Id = "de_ancient_night" },
        new() { Name = "Nuke", Id = "de_nuke" },
        new() { Name = "Vertigo", Id = "de_vertigo" }
    ];
}
