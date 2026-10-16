using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed partial class LegacyImportTests
{
    [Fact]
    public async Task MusicProposalRetainsTheBackupAndAllowsAReviewedFolderOverrideWithoutChangingLegacyData()
    {
        var fixture = Fixture(); var database = Path.Combine(fixture.Source, "cleanroom.db");
        var before = SHA256.HashData(File.ReadAllBytes(database)); var id = Guid.NewGuid().ToString("N");
        var allStopped = true;
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => allStopped);
        previews.Initialize(); previews.Start(id, fixture.Source); await Finished(previews);
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, 0, "4", Path.Combine(fixture.Root, "selected-pop"), 0));
        var saved = previews.SaveMappings(id, 0, [new(4, "4"), new(9, "9")]);
        allStopped = false; // Existing mapping can prepare another group while a sibling is active.
        var selected = Path.Combine(fixture.Root, "selected-pop");
        var proof = previews.PrepareMusicAssignment(id, saved.Revision, "4", selected, 0);
        Assert.Equal(saved.Backup, proof.Backup); Assert.Equal(4, proof.LegacyStationId); Assert.Equal("4", proof.TargetStationId);
        Assert.Equal(Path.Combine(fixture.Source, "Pop"), proof.OriginalMusicFolder); Assert.Equal(selected, proof.SelectedMusicFolder);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database))); Assert.False(Directory.Exists(selected));
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision - 1, "4", selected, 0));
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision, "unknown", selected, 0));
        allStopped = true;
        previews.SaveMappings(id, saved.Revision, [new(4, "4"), new(9, null)]);
        Assert.Throws<InvalidOperationException>(() => previews.RequireCurrentMusicMapping(proof));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedLegacyMusicOrIdentityRequiresAnotherPreview(bool rename)
    {
        var fixture = Fixture(); var id = Guid.NewGuid().ToString("N");
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true);
        previews.Initialize(); previews.Start(id, fixture.Source); await Finished(previews);
        var saved = previews.SaveMappings(id, 0, [new(4, "4"), new(9, null)]);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Source, "cleanroom.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = rename ? "UPDATE stations SET name='Replaced station' WHERE id=4" : "UPDATE station_settings SET value='changed music source' WHERE station_id=4 AND key='music_library_folder'";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision, "4", Path.Combine(fixture.Root, "selected"), 0));
    }
    [Fact]
    public async Task SharedMusicAndJingleParentsAreRejectedBeforeAnAssignment()
    {
        var fixture = Fixture(); var id = Guid.NewGuid().ToString("N");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Source, "cleanroom.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO station_settings VALUES(9,'music_library_folder',$path)";
            command.Parameters.AddWithValue("$path", Path.Combine(fixture.Source, "Energize")); command.ExecuteNonQuery();
        }
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true);
        previews.Initialize(); previews.Start(id, fixture.Source); await Finished(previews);
        var saved = previews.SaveMappings(id, 0, [new(4, "4"), new(9, "9")]);
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision, "4", fixture.Source, 0));
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision, "4", Path.Combine(fixture.Source, "Jingles", "Pop", "nested"), 0));
        Assert.Throws<InvalidOperationException>(() => previews.PrepareMusicAssignment(id, saved.Revision, "4", Path.Combine(fixture.Source, "Energize"), 0));
        Assert.NotNull(previews.PrepareMusicAssignment(id, saved.Revision, "4", Path.Combine(fixture.Source, "Pop-extra"), 0));
    }
}
