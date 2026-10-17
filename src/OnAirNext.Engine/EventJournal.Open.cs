using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    private static FileStream AcquireWriterOwnership(string database)
    {
        try
        {
            // Retain the sidecar; unlinking a released lock can race with the next owner.
            // The OS releases the exclusive handle even after a hard process kill.
            return new FileStream(database + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("The playback database is already owned or its writer lock is unavailable. No second engine was started.", ex);
        }
    }

    private static void ValidateExistingSchema(string database)
    {
        if (!File.Exists(database) || new FileInfo(database).Length == 0) return;
        try
        {
            // Do this before WAL pragmas, table creation or additive migrations.
            using var connection = OpenReader(database);
            using var table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_info'";
            if ((long)table.ExecuteScalar()! != 1)
                throw new InvalidDataException("The existing database is not a versioned OnAir Next playback journal. It was not adopted.");
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_info";
            using var reader = version.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.GetInt64(0) != 1 || reader.Read())
                throw new InvalidDataException("Unsupported or ambiguous journal schema; explicit migration is required.");
        }
        catch (Exception ex) when (ex is SqliteException or FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("The existing playback journal could not be validated. No migration was attempted.", ex);
        }
    }
}
