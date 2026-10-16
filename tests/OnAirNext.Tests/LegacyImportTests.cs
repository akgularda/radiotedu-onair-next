using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LegacyImportTests
{
    private static (string Root, string Source, string Target) Fixture(bool wrongHash = false)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "migration-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "legacy"); var target = Path.Combine(root, "new");
        Directory.CreateDirectory(Path.Combine(source, "recovery-points", "daily")); Directory.CreateDirectory(target);
        var recovery = Path.Combine(source, "recovery-points", "daily", "synthetic.db.dpapi");
        var bytes = Encoding.UTF8.GetBytes("ONAIR-DPAPI-1\nSYNTHETIC UNIT FIXTURE; NOT RESTORE DATA"); File.WriteAllBytes(recovery, bytes);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(source, "cleanroom.db"), Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE stations(id INTEGER PRIMARY KEY,name TEXT NOT NULL);
            CREATE TABLE station_settings(station_id INTEGER,key TEXT,value TEXT);
            CREATE TABLE station_outputs(station_id INTEGER,icecast_host TEXT,icecast_port INTEGER,icecast_mount TEXT,stream_codec_profile TEXT,stream_bitrate_kbps INTEGER,icecast_enabled INTEGER,icecast_password TEXT);
            CREATE TABLE recovery_points(id INTEGER PRIMARY KEY,file_path TEXT,sha256 TEXT,size_bytes INTEGER,verified_at TEXT,integrity_status TEXT);
            INSERT INTO stations VALUES(4,'Pop'),(9,'Energize');
            INSERT INTO station_settings VALUES(4,'music_library_folder',$music),(4,'jingle_library_folder',$jingle),(4,'sweeper_track_ids','21,22'),(4,'sweeper_enabled','true'),(4,'sweeper_interval','3'),(4,'ai_host_enabled','true');
            INSERT INTO station_outputs VALUES(4,'127.0.0.1',18980,'/pop','aac_low_192',192,1,'synthetic-source-secret'),(9,'127.0.0.1',18980,'/energize','aac_low_192',192,1,'synthetic-source-secret');
            INSERT INTO recovery_points VALUES(1,$backup,$hash,$size,'2026-10-16T08:00:00Z','ok');
            """;
        command.Parameters.AddWithValue("$music", Path.Combine(source, "Pop")); command.Parameters.AddWithValue("$jingle", Path.Combine(source, "Jingles", "Pop"));
        command.Parameters.AddWithValue("$backup", recovery); command.Parameters.AddWithValue("$hash", wrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes))); command.Parameters.AddWithValue("$size", bytes.Length);
        command.ExecuteNonQuery(); return (root, source, target);
    }
    private static async Task<LegacyImportPreview> Finished(LegacyImportPreviews previews)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var preview = previews.Snapshot();
            if (preview is not null && preview.State is not ("Inspecting" or "Verifying backup")) return preview;
            await Task.Delay(20, timeout.Token);
        }
    }
    [Fact]
    public async Task PreviewVerifiesBackupWithoutChangingSourceOrCopyingCredentialsAndAi()
    {
        var fixture = Fixture(); var database = Path.Combine(fixture.Source, "cleanroom.db"); var before = SHA256.HashData(File.ReadAllBytes(database));
        var id = Guid.NewGuid().ToString("N");
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); previews.Initialize();
        previews.Start(id, fixture.Source); var result = await Finished(previews);
        Assert.Equal("Ready for mapping", result.State); Assert.False(result.ProductionChanged); Assert.NotNull(result.Backup);
        var stations = result.Stations!; Assert.Equal(2, stations.Length); var pop = Assert.Single(stations, station => station.Id == 4);
        Assert.Equal(Path.Combine(fixture.Source, "Pop"), pop.MusicFolder); Assert.Equal(Path.Combine(fixture.Source, "Jingles", "Pop"), pop.JingleFolder);
        Assert.True(pop.SweepersEnabled); Assert.Equal(3, pop.SweeperEvery); Assert.True(pop.PrimaryOutput!.HasCredential);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database)));
        var saved = File.ReadAllText(Path.Combine(fixture.Target, "migration-previews", id + ".json"));
        Assert.DoesNotContain("synthetic-source-secret", saved); Assert.DoesNotContain("ai_host_enabled", saved);
        Assert.Equal(result.Id, previews.Start(id, fixture.Source).Id); Assert.Equal(result.State, previews.Snapshot(id)!.State);
        Assert.Throws<ArgumentException>(() => previews.Start(id, fixture.Root));
    }
    [Fact]
    public async Task MappingReadbackSurvivesRestartAndRejectsStaleOrConflictingAssignments()
    {
        var fixture = Fixture(); var id = Guid.NewGuid().ToString("N");
        await using (var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true))
        {
            previews.Initialize(); previews.Start(id, fixture.Source); await Finished(previews);
            Assert.Throws<ArgumentException>(() => previews.SaveMappings(id, 0, [new(4, "4"), new(9, "4")]));
            Assert.Throws<ArgumentException>(() => previews.SaveMappings(id, 0, [new(4, "4")]));
            Assert.Throws<ArgumentException>(() => previews.SaveMappings(id, 0, [new(4, "unknown"), new(9, null)]));
            var saved = previews.SaveMappings(id, 0, [new(9, "9"), new(4, "4")]); Assert.Equal(1, saved.Revision);
            Assert.Equal("Mapping saved", saved.State);
            Assert.Equal(1, previews.SaveMappings(id, 0, [new(4, "4"), new(9, "9")]).Revision);
            Assert.Throws<InvalidOperationException>(() => previews.SaveMappings(id, 0, [new(4, "4"), new(9, null)]));
        }
        await using var recovered = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); recovered.Initialize();
        var plan = recovered.Snapshot()!; Assert.Equal(id, plan.Id); Assert.Equal(1, plan.Revision); Assert.Equal("Mapping saved", plan.State);
        Assert.Collection(plan.Mappings!, mapping => { Assert.Equal(4, mapping.LegacyStationId); Assert.Equal("4", mapping.TargetStationId); }, mapping => { Assert.Equal(9, mapping.LegacyStationId); Assert.Equal("9", mapping.TargetStationId); });
    }
    [Fact]
    public async Task ChangedRetainedFolderDataCannotBeUsedAsTheOriginalVerifiedPreview()
    {
        var fixture = Fixture(); var id = Guid.NewGuid().ToString("N");
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); previews.Initialize(); previews.Start(id, fixture.Source);
        var ready = await Finished(previews); var stations = ready.Stations!;
        stations[0] = stations[0] with { MusicFolder = "changed-after-verification" };
        File.WriteAllText(Path.Combine(fixture.Target, "migration-previews", id + ".json"), JsonSerializer.Serialize(ready with { Stations = stations }));
        Assert.Throws<InvalidDataException>(() => previews.SaveMappings(id, 0, [new(4, "4"), new(9, "9")]));
    }

    [Fact]
    public async Task FailedBackupDigestCannotBecomeASavedMapping()
    {
        var fixture = Fixture(true); var id = Guid.NewGuid().ToString("N");
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); previews.Initialize(); previews.Start(id, fixture.Source);
        var result = await Finished(previews); Assert.Equal("Failed", result.State); Assert.Null(result.Backup); Assert.Contains("SHA-256", result.Error);
        Assert.Throws<InvalidOperationException>(() => previews.SaveMappings(id, 0, [new(4, "4"), new(9, "9")]));
    }
    [Fact]
    public async Task ArmedOrRunningEngineCannotPrepareLegacyPreview()
    {
        var fixture = Fixture(); await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => false); previews.Initialize();
        Assert.Throws<InvalidOperationException>(() => previews.Start(Guid.NewGuid().ToString("N"), fixture.Source));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Target, "migration-previews")));
        Assert.Throws<ArgumentException>(() => previews.Start(Guid.NewGuid().ToString("N"), "relative-directory"));
    }
    [Fact]
    public async Task InterruptedPreviewIsRetainedWithoutAutomaticallyRepeatingTheBackupRead()
    {
        var fixture = Fixture(); var id = Guid.NewGuid().ToString("N"); var folder = Path.Combine(fixture.Target, "migration-previews"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new LegacyImportPreview(id, fixture.Source, "Verifying backup", DateTimeOffset.UtcNow)));
        File.WriteAllText(Path.Combine(folder, "latest"), id);
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); previews.Initialize();
        Assert.Equal("Interrupted", previews.Snapshot()!.State); Assert.Null(previews.Snapshot()!.Backup);
    }
    [Fact]
    public async Task CorruptOptionalPreviewDoesNotPreventInitializationOrAFreshUiRequest()
    {
        var fixture = Fixture(); var folder = Path.Combine(fixture.Target, "migration-previews"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "latest"), "corrupt-preview-pointer");
        await using var previews = new LegacyImportPreviews(fixture.Target, ["4", "9"], () => true); previews.Initialize();
        Assert.Equal("Unavailable", previews.Snapshot()!.State);
        var id = Guid.NewGuid().ToString("N"); previews.Start(id, fixture.Source);
        Assert.Equal("Ready for mapping", (await Finished(previews)).State);
        Assert.Equal(id, File.ReadAllText(Path.Combine(folder, "latest")));
    }

    [Fact]
    public void DuplicateDestinationsAndExternallyOwnedMountsCannotBeMapped()
    {
        var output = new LegacySavedOutput("127.0.0.1", 18980, "/pop", "aac_low_192", 192, true, true);
        var station = new LegacyStationPreview(4, "Pop", null, null, null, false, 3, output, []);
        var preview = new LegacyImportPreview(Guid.NewGuid().ToString("N"), "synthetic", "Ready for mapping", DateTimeOffset.UtcNow,
            Stations: [station, station with { Id = 9, Name = "Energize" }]);
        HashSet<string> targets = ["4", "9"]; LegacyStationMapping[] mappings = [new(4, "4"), new(9, "9")];
        Assert.Throws<ArgumentException>(() => LegacyImportPreview.ValidateMappings(preview, mappings, targets));
        preview = preview with { Stations = [station with { PrimaryOutput = output with { Mount = "/en" } }, station with { Id = 9, PrimaryOutput = null }] };
        Assert.Throws<ArgumentException>(() => LegacyImportPreview.ValidateMappings(preview, mappings, targets));
        LegacyImportPreview.ValidateMappings(preview, [new(4, null), new(9, "9")], targets);
    }
}
