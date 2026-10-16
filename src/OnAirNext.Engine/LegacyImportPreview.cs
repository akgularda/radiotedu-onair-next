namespace OnAirNext.Engine;

public sealed record LegacyStationMapping(int LegacyStationId, string? TargetStationId);
public sealed record LegacySavedOutput(string Host, int Port, string Mount, string Profile, int BitrateKbps, bool Enabled, bool HasCredential);
public sealed record LegacyStationPreview(int Id, string Name, string? MusicFolder, string? JingleFolder,
    string? SweeperTrackIds, bool SweepersEnabled, int SweeperEvery, LegacySavedOutput? PrimaryOutput, string[] Warnings);
public sealed record LegacyBackupProof(string FileName, string Sha256, long Bytes, string? VerifiedAt);
public sealed record LegacyImportPreview(string Id, string LegacyRoot, string State, DateTimeOffset CreatedAtUtc,
    int Revision = 0, LegacyStationPreview[]? Stations = null, LegacyStationMapping[]? Mappings = null,
    LegacyBackupProof? Backup = null, string? SettingsFingerprint = null, string? Error = null,
    long CheckedBytes = 0, bool ProductionChanged = false)
{
    public const string Scope = "Saved primary-output and folder mapping review only. No channels are imported, armed, stopped or started. Secondary outputs, protected credentials, branding assets and rollback must still be qualified.";
    public static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
            throw new ArgumentException("Choose a saved migration preview.");
    }
    public static void ValidateMappings(LegacyImportPreview preview, LegacyStationMapping[] mappings, IReadOnlySet<string> targets)
    {
        var sourceIds = (preview.Stations ?? []).Select(station => station.Id).ToHashSet();
        if (mappings.Length != sourceIds.Count || mappings.Select(mapping => mapping.LegacyStationId).Distinct().Count() != mappings.Length ||
            mappings.Any(mapping => !sourceIds.Contains(mapping.LegacyStationId) || mapping.TargetStationId is not null && !targets.Contains(mapping.TargetStationId)))
            throw new ArgumentException("Map every legacy station once to a known channel or Leave with former program.");
        var selected = mappings.Where(mapping => mapping.TargetStationId is not null).ToArray();
        if (selected.Length == 0 || selected.Select(mapping => mapping.TargetStationId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Choose distinct target channels for the stations to import.");
        var outputs = selected.Select(mapping => preview.Stations!.Single(station => station.Id == mapping.LegacyStationId).PrimaryOutput).Where(output => output is not null).Select(output => output!).ToArray();
        if (outputs.Any(output => output.Mount.Equals("/en", StringComparison.OrdinalIgnoreCase) || output.Mount.Equals("/fr", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("EN and FR remain with their external owners. Leave them with the former program.");
        if (outputs.Where(output => output.Enabled).Select(output => (Host: output.Host.ToLowerInvariant(), output.Port, output.Mount)).Distinct().Count() != outputs.Count(output => output.Enabled))
            throw new ArgumentException("Two selected stations have the same saved primary destination. Resolve ownership before saving this mapping.");
    }
}
