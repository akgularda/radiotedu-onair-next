using System.Globalization;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class ExistingRosterLab
{
    private static readonly string[] StationIds = ["1", "2", "4", "5", "8", "9", "10", "11"];

    internal static IReadOnlyList<StationOutputConfiguration> Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is <= 0 or > 65536)
            throw new InvalidDataException("Choose a bounded redacted legacy output inventory.");
        using var inventory = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
        var stations = inventory.RootElement.GetProperty("stations").EnumerateArray().Select(station =>
        {
            var id = station.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var outputs = station.GetProperty("outputs").EnumerateArray().Select((output, i) =>
            {
                var codec = output.GetProperty("codec").GetString();
                var kbps = output.GetProperty("kbps").GetInt32();
                var profile = (codec, kbps) switch
                {
                    ("aac_low_192", 192) => CodecProfile.AacLc192,
                    ("aac_he_v2_64", 64) => CodecProfile.HeAacV2_64,
                    ("ogg_flac_lossless", 0) => CodecProfile.OggFlac,
                    _ => throw new InvalidDataException("The inventory has an unexpected codec or bitrate.")
                };
                // These placeholders are for roster validation only. The lab
                // creates fresh loopback outputs before constructing any engine.
                return new OutputSettings($"roster-{id}-{i}", "stream.radiotedu.com", 11154,
                    output.GetProperty("mount").GetString()!, "validation-placeholder", profile, Commissioned: true,
                    Information: new("Loopback inventory fixture", "", "Music", "https://example.invalid", false));
            }).ToArray();
            return new StationOutputConfiguration(id, outputs);
        }).ToArray();
        return new ProductionOutputConfiguration(1, stations, ProductionOutputRoster.Existing16).Validate(StationIds).Stations;
    }
}
