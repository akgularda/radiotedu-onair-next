using System.Globalization;

namespace OnAirNext.Engine;

public sealed record LegacyMusicProof(string PreviewId, int MappingRevision, int LegacyStationId,
    string LegacyStationName, string TargetStationId, string OriginalMusicFolder, string SelectedMusicFolder,
    long ExpectedLibraryRevision, string SettingsFingerprint, LegacyBackupProof Backup, DateTimeOffset CheckedAtUtc)
{
    public void Validate()
    {
        LegacyImportPreview.ValidateId(PreviewId);
        if (MappingRevision < 1 || LegacyStationId < 1 || string.IsNullOrWhiteSpace(LegacyStationName) || LegacyStationName.Length > 128 ||
            string.IsNullOrWhiteSpace(TargetStationId) || TargetStationId.Length > 128 || ExpectedLibraryRevision is < 0 or long.MaxValue ||
            string.IsNullOrWhiteSpace(OriginalMusicFolder) || OriginalMusicFolder.Length > 4096 ||
            string.IsNullOrWhiteSpace(SelectedMusicFolder) || SelectedMusicFolder.Length > 4096 || !Hash(SettingsFingerprint) || CheckedAtUtc == default ||
            Backup is null || !Hash(Backup.Sha256) || Backup.Bytes is <= 0 or > 1073741824 ||
            string.IsNullOrWhiteSpace(Backup.FileName) || Backup.FileName.Length > 255 || Backup.FileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !DateTimeOffset.TryParse(Backup.VerifiedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("The saved music assignment proof is incomplete. Review its mapping again.");
    }
    public static bool PathsOverlap(string first, string second)
    {
        if (!Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second)) return false;
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Prefix(string value) => Path.EndsInDirectorySeparator(value) ? value : value + Path.DirectorySeparatorChar;
        return first.Equals(second, comparison) || first.StartsWith(Prefix(second), comparison) || second.StartsWith(Prefix(first), comparison);
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
public sealed record LegacyMusicAssignment(string CommandId, string StationId, long RootRevision,
    DateTimeOffset AppliedAtUtc, LegacyMusicProof Proof);
