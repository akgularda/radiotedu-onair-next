using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    public Task<bool> SetLegacyMusicRootAsync(string station, string path, string commandId,
        LegacyMusicProof proof, Action authorizeNewAssignment)
    {
        ArgumentNullException.ThrowIfNull(proof); ArgumentNullException.ThrowIfNull(authorizeNewAssignment);
        proof.Validate(); path = NormalizeLibraryPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (proof.TargetStationId != station || !string.Equals(path, proof.SelectedMusicFolder, comparison))
            throw new ArgumentException("The music assignment belongs to a different channel or folder.");
        if (Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!).Equals(path, comparison))
            throw new ArgumentException("Choose one station music subfolder, rather than a whole drive.");
        var payload = JsonSerializer.Serialize(proof);
        if (Encoding.UTF8.GetByteCount(payload) > 16384) throw new ArgumentException("Music assignment proof exceeds its size limit.");
        return TransactionAsync((c, t) =>
        {
            if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
            if (!AdmitControl(c, t, station, "library-legacy-root", payload, commandId)) return false;
            if ((ReadLibraryRoot(c, t, station)?.Revision ?? 0) != proof.ExpectedLibraryRevision)
                throw new InvalidOperationException("This channel's music folder changed. Reopen the assignment before applying it.");
            using (var other = LibraryCommand(c, t, "SELECT path FROM library_roots WHERE station_id<>$station", ("$station", station)))
            using (var rows = other.ExecuteReader())
                while (rows.Read()) if (LegacyMusicProof.PathsOverlap(path, rows.GetString(0)))
                    throw new InvalidOperationException("Choose a music folder separate from the other new channels.");
            authorizeNewAssignment(); // Fast cached gates only. Source inspection happens before entering this transaction.
            using (var root = LibraryCommand(c, t, """
                INSERT INTO library_roots VALUES($station,$path,1)
                ON CONFLICT(station_id) DO UPDATE SET path=excluded.path,revision=revision+1
                """, ("$station", station), ("$path", path))) root.ExecuteNonQuery();
            var revision = ReadLibraryRoot(c, t, station)!.Revision;
            using var receipt = LibraryCommand(c, t, """
                INSERT INTO library_legacy_music VALUES($id,$station,$revision,$payload,$hash,$at)
                """, ("$id", commandId), ("$station", station), ("$revision", revision), ("$payload", payload),
                ("$hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))),
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            receipt.ExecuteNonQuery(); return true;
        });
    }
    public LegacyMusicAssignment? LegacyMusicAssignment(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 128) throw new ArgumentException("Invalid assignment request.");
        using var c = OpenReader(DatabasePath); using var t = c.BeginTransaction(deferred: true);
        using var cmd = LibraryCommand(c, t, "SELECT command_id,station_id,revision,payload,payload_hash,created_utc FROM library_legacy_music WHERE command_id=$id", ("$id", commandId));
        return ReadLegacyMusicAssignment(cmd);
    }
    private static LegacyMusicAssignment? CurrentLegacyMusicAssignment(SqliteConnection c, SqliteTransaction t, LibraryRoot root)
    {
        using var cmd = LibraryCommand(c, t, "SELECT command_id,station_id,revision,payload,payload_hash,created_utc FROM library_legacy_music WHERE station_id=$station AND revision=$revision",
            ("$station", root.StationId), ("$revision", root.Revision));
        var result = ReadLegacyMusicAssignment(cmd);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (result is not null && !string.Equals(root.Path, result.Proof.SelectedMusicFolder, comparison))
            throw new InvalidDataException("The library folder differs from its retained music assignment.");
        return result;
    }
    private static LegacyMusicAssignment? ReadLegacyMusicAssignment(SqliteCommand cmd)
    {
        using var row = cmd.ExecuteReader(); if (!row.Read()) return null;
        var payload = row.GetString(3);
        if (Encoding.UTF8.GetByteCount(payload) > 16384 || row.GetString(4) != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
            throw new InvalidDataException("The retained music assignment proof failed its checksum. No music configuration was changed.");
        var proof = JsonSerializer.Deserialize<LegacyMusicProof>(payload) ?? throw new InvalidDataException("The retained music assignment proof is invalid.");
        proof.Validate();
        if (proof.TargetStationId != row.GetString(1) || proof.ExpectedLibraryRevision + 1 != row.GetInt64(2))
            throw new InvalidDataException("The retained music assignment belongs to a different library revision.");
        if (!DateTimeOffset.TryParse(row.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.None, out var appliedAt))
            throw new InvalidDataException("The retained music assignment timestamp is invalid.");
        return new(row.GetString(0), row.GetString(1), row.GetInt64(2), appliedAt, proof);
    }
}
