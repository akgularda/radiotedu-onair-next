using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LegacyMusicAssignmentTests
{
    private static string Root()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "music-assignment-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static async Task Initialize(EventJournal journal)
    {
        await journal.InitializeLibraryAsync();
        await journal.InitializeDesiredStateAsync(new("a", [], [], AutoStart: false));
        await journal.InitializeDesiredStateAsync(new("b", [], [], AutoStart: false));
    }
    private static LegacyMusicProof Proof(string root, long revision = 0) => new(Guid.NewGuid().ToString("N"), 1, 4, "Pop", "a",
        Path.Combine(root, "legacy-pop"), Path.Combine(root, "selected-pop"), revision, new string('a', 64),
        new("synthetic.db.dpapi", new string('b', 64), 50, "2026-10-16T08:00:00Z"), DateTimeOffset.UtcNow);
    [Fact]
    public async Task MusicAndEvidenceCommitTogetherAndTheSameCommandSurvivesRestartWithoutReauthorizing()
    {
        var root = Root(); var database = Path.Combine(root, "catalog.db"); var proof = Proof(root);
        await using (var journal = new EventJournal(database))
        {
            await Initialize(journal);
            Assert.True(await journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "apply", proof, () => { }));
            var page = journal.LibraryPage("a"); var receipt = Assert.IsType<LegacyMusicAssignment>(page.LegacyMusicAssignment);
            Assert.Equal("apply", receipt.CommandId); Assert.Equal(proof, receipt.Proof); Assert.Equal(page.Root!.Revision, receipt.RootRevision);
            Assert.False((await journal.InitializeDesiredStateAsync(new("a", [], [], AutoStart: false))).RequestedRunning);
        }
        await using (var journal = new EventJournal(database))
        {
            await Initialize(journal);
            var retained = journal.LegacyMusicAssignment("apply")!; Assert.Equal(proof, retained.Proof);
            Assert.False(await journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "apply", retained.Proof, () => throw new InvalidOperationException("Fresh source checks may expire after a completed command.")));
            Assert.Equal(1, journal.LibraryPage("a").Root!.Revision);
            await journal.SetLibraryRootAsync("a", Path.Combine(root, "later-choice"), "later");
            Assert.Null(journal.LibraryPage("a").LegacyMusicAssignment);
            Assert.False(await journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "apply", retained.Proof, () => throw new InvalidOperationException()));
            Assert.Equal(Path.Combine(root, "later-choice"), journal.LibraryPage("a").Root!.Path);
            Assert.Equal(1, journal.LegacyMusicAssignment("apply")!.RootRevision);
        }
        Assert.False(Directory.Exists(proof.SelectedMusicFolder));
    }
    [Fact]
    public async Task ARejectedAccessGateDoesNotLeaveARootOrConsumeTheRequest()
    {
        var root = Root(); var proof = Proof(root); await using var journal = new EventJournal(Path.Combine(root, "catalog.db")); await Initialize(journal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "access", proof, () => throw new UnauthorizedAccessException()));
        Assert.Empty(journal.LibraryRoots()); Assert.Null(journal.LegacyMusicAssignment("access"));
        Assert.True(await journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "access", proof, () => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "access", proof with { MappingRevision = 2 }, () => { }));
        Assert.Equal(1, journal.LibraryPage("a").Root!.Revision);
    }
    [Fact]
    public async Task StaleLibraryVersionsAndOverlappingOtherChannelsCannotBeOverwritten()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db")); await Initialize(journal);
        await journal.SetLibraryRootAsync("a", Path.Combine(root, "current-a"), "first"); var stale = Proof(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLegacyMusicRootAsync("a", stale.SelectedMusicFolder, "stale", stale, () => { }));
        Assert.Null(journal.LegacyMusicAssignment("stale"));
        await journal.SetLibraryRootAsync("b", Path.Combine(root, "shared", "b"), "other");
        var overlap = Proof(root, 1) with { SelectedMusicFolder = Path.Combine(root, "shared") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLegacyMusicRootAsync("a", overlap.SelectedMusicFolder, "overlap", overlap, () => { }));
        Assert.Equal(Path.Combine(root, "current-a"), journal.LibraryPage("a").Root!.Path);
        var independent = overlap with { SelectedMusicFolder = Path.Combine(root, "shared-neighbor") };
        Assert.True(await journal.SetLegacyMusicRootAsync("a", independent.SelectedMusicFolder, "independent", independent, () => { }));
        Assert.Equal(Path.Combine(root, "shared", "b"), journal.LibraryPage("b").Root!.Path);
    }
    [Fact]
    public async Task DamagedOptionalProofIsVisibleWithoutPreventingRootRecovery()
    {
        var root = Root(); var database = Path.Combine(root, "catalog.db"); var proof = Proof(root);
        await using (var journal = new EventJournal(database)) { await Initialize(journal); await journal.SetLegacyMusicRootAsync("a", proof.SelectedMusicFolder, "apply", proof, () => { }); }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE library_legacy_music SET payload='{}' WHERE command_id='apply'"; command.ExecuteNonQuery();
        }
        await using (var journal = new EventJournal(database))
        {
            await Initialize(journal); Assert.Equal(proof.SelectedMusicFolder, Assert.Single(journal.LibraryRoots()).Path);
            Assert.Throws<InvalidDataException>(() => journal.LibraryPage("a"));
        }
    }
}
