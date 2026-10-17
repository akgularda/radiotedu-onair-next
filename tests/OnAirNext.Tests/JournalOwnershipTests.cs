using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class JournalOwnershipTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return Path.Combine(root, "ownership.db");
    }

    private static void CreateForeignDatabase(string database, string schema)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = schema;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task ASecondWriterIsRejectedWhileReadersAndTheOriginalWriterKeepWorking()
    {
        var database = Database();
        await using (var first = new EventJournal(database))
        {
            Assert.Throws<IOException>(() => new EventJournal(database));
            var entry = new PlaybackEvent("event", "occurrence", "station", "started", "asset", "Artist", "Song",
                ContentKind.Music, DateTimeOffset.UtcNow, 0);
            await first.AppendAsync(entry);
            Assert.Equal(entry, Assert.Single(EventJournal.ReadEvents(database)));
            Assert.True(EventJournal.Verify(database));
        }
        await using var reopened = new EventJournal(database);
        Assert.Single(EventJournal.ReadEvents(database));
    }

    [Theory]
    [InlineData("CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info VALUES(999); CREATE TABLE future_only(value TEXT); INSERT INTO future_only VALUES('preserve')")]
    [InlineData("CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info VALUES(1),(1)")]
    [InlineData("CREATE TABLE schema_info(version INTEGER NOT NULL)")]
    [InlineData("CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserve')")]
    public void UnsupportedDatabasesRemainByteIdenticalAndReleaseTheirOwnershipHandle(string schema)
    {
        var database = Database();
        CreateForeignDatabase(database, schema);
        var before = File.ReadAllBytes(database);
        Assert.Throws<InvalidDataException>(() => new EventJournal(database));
        Assert.Equal(before, File.ReadAllBytes(database));
        Assert.False(File.Exists(database + "-wal"));
        using var released = new FileStream(database + ".writer.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void CorruptDatabaseIsRejectedWithoutCreatingANewJournalOverIt()
    {
        var database = Database();
        var before = "Not an SQLite database. Preserve the incident evidence."u8.ToArray();
        File.WriteAllBytes(database, before);
        Assert.Throws<InvalidDataException>(() => new EventJournal(database));
        Assert.Equal(before, File.ReadAllBytes(database));
        using var released = new FileStream(database + ".writer.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
