using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class StationLibraryTests
{
    private static string Root()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static StationSettings Settings => new("a", [], [], AutoStart: false);
    private static async Task WaitAsync(StationLibrary library, Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) { library.Wake(); await Task.Delay(100, deadline.Token); }
    }

    [Fact]
    public async Task RoleInventoriesShareOneValidationLaneWithoutMixingTheMusicPlanner()
    {
        var root=Root();var music=Path.Combine(root,"songs");var jingles=Path.Combine(root,"identifiers");var sweepers=Path.Combine(root,"sweepers");
        foreach(var folder in new[]{music,jingles,sweepers})Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(music,"song.wav"),"fixture");
        await File.WriteAllTextAsync(Path.Combine(jingles,"one.wav"),"fixture");
        await File.WriteAllTextAsync(Path.Combine(jingles,"two.wav"),"fixture");
        await File.WriteAllTextAsync(Path.Combine(sweepers,"sweeper.wav"),"fixture");
        await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));var active=0;var maximum=0;
        await journal.InitializeDesiredStateAsync(Settings);
        var jingleKey=StationBranding.InventoryKey("a",ContentKind.Jingle);var sweeperKey=StationBranding.InventoryKey("a",ContentKind.Sweeper);
        async Task<AudioMetadata> Probe(string path,CancellationToken token)
        {
            var current=Interlocked.Increment(ref active);maximum=Math.Max(maximum,current);
            try{await Task.Delay(30,token);return new("Fixture",Path.GetFileNameWithoutExtension(path),.2);}
            finally{Interlocked.Decrement(ref active);}
        }
        await using var library=new StationLibrary(journal,"unused",root,["a",jingleKey,sweeperKey],Probe,
            new Dictionary<string,ContentKind>{{jingleKey,ContentKind.Jingle},{sweeperKey,ContentKind.Sweeper}});
        using var branding=new StationBranding(journal,library,["a"]);await branding.InitializeAsync();
        await library.InitializeAsync();
        await library.SetRootAsync("a",music,"music");await library.SetRootAsync(jingleKey,jingles,"jingles");await library.SetRootAsync(sweeperKey,sweepers,"sweepers");
        await WaitAsync(library,()=>library.ReadyInventory("a").ReadyCount==1&&library.ReadyInventory(jingleKey).ReadyCount==2&&library.ReadyInventory(sweeperKey).ReadyCount==1);
        var frozen=library.ReadyInventory(jingleKey,1);
        Assert.Equal(2,frozen.ReadyCount);Assert.Equal(ContentKind.Jingle,Assert.Single(frozen.Items).Kind);
        Assert.Equal(ContentKind.Sweeper,Assert.Single(library.ReadyInventory(sweeperKey).Items).Kind);
        var planner=library.Planner(Settings)!;Assert.Equal(1,planner.AvailableItems);Assert.Equal(ContentKind.Music,planner.Select(0).Item.Kind);
        Assert.Equal(1,maximum);
        var empty=Path.Combine(root,"empty");Directory.CreateDirectory(empty);await library.SetRootAsync(jingleKey,empty,"replace");
        Assert.Empty(library.ReadyInventory(jingleKey).Items);Assert.Single(frozen.Items);
        Assert.Throws<ArgumentException>(()=>library.ReadyInventory("unknown"));
        Assert.Throws<ArgumentOutOfRangeException>(()=>library.ReadyInventory("a",0));
    }

    [Fact]
    public async Task UnsupportedOrUnconfiguredInventoryRolesAreRejected()
    {
        var root=Root();await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));
        Assert.Throws<ArgumentException>(()=>new StationLibrary(journal,"unused",root,["a"],contentKinds:
            new Dictionary<string,ContentKind>{{"missing",ContentKind.Jingle}}));
        Assert.Throws<ArgumentException>(()=>new StationLibrary(journal,"unused",root,["a"],contentKinds:
            new Dictionary<string,ContentKind>{{"a",ContentKind.Advertisement}}));
    }

    [Fact]
    public async Task ShuffledCyclesConsumeEverySongOnceAndAvoidABoundaryRepeat()
    {
        var root = Root(); var music = Path.Combine(root, "music"); Directory.CreateDirectory(music);
        for (var i = 0; i < 6; i++) await File.WriteAllTextAsync(Path.Combine(music, "song-" + i + ".wav"), "fixture");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        await using var library = new StationLibrary(journal, "unused", root, ["a"], (path, _) => Task.FromResult(new AudioMetadata("Artist", Path.GetFileNameWithoutExtension(path), 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "folder");
        var planner = library.Planner(Settings)!;
        await WaitAsync(library, () => planner.AvailableItems == 6);
        var first = Enumerable.Range(0, 6).Select(i => planner.Select(i).Item.Id).ToArray();
        var second = Enumerable.Range(6, 6).Select(i => planner.Select(i).Item.Id).ToArray();
        Assert.Equal(6, first.Distinct().Count()); Assert.Equal(6, second.Distinct().Count());
        Assert.Equal(first.Order(), second.Order());
        Assert.NotEqual(first.Last(), second.First());
        Assert.False(first.SequenceEqual(second));
    }

    [Fact]
    public async Task RescanRetriesARejectedFileWithoutRequiringAFileEdit()
    {
        var root = Root(); var music = Path.Combine(root, "music"); Directory.CreateDirectory(music);
        await File.WriteAllTextAsync(Path.Combine(music, "song.wav"), "fixture");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        var valid = false; var probes = 0;
        await using var library = new StationLibrary(journal, "unused", root, ["a"], (_, _) =>
        {
            Interlocked.Increment(ref probes);
            return Volatile.Read(ref valid) ? Task.FromResult(new AudioMetadata("Artist", "Song", 10))
                : Task.FromException<AudioMetadata>(new InvalidDataException("Injected invalid audio"));
        });
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "folder");
        await WaitAsync(library, () => library.Page("a", "", 0).Items.SingleOrDefault()?.State == "Invalid");
        Volatile.Write(ref valid, true); await library.RescanAsync("a", "rescan");
        await WaitAsync(library, () => library.Planner(Settings)!.AvailableItems == 1);
        var count = probes; await library.RescanAsync("a", "rescan");
        library.Wake(); await Task.Delay(150);
        Assert.Equal(count, probes);
        Assert.Equal("Ready", Assert.Single(library.Page("a", "", 0).Items).State);
    }

    [Fact]
    public async Task ValidationToolFailureKeepsPreviouslyReadyMusicAvailable()
    {
        var root = Root(); var music = Path.Combine(root, "music"); Directory.CreateDirectory(music);
        await File.WriteAllTextAsync(Path.Combine(music, "healthy.wav"), "fixture");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        var unavailable = true;
        await using var library = new StationLibrary(journal, "unused", root, ["a"], (path, _) =>
            Path.GetFileName(path) == "new.wav" && Volatile.Read(ref unavailable)
                ? Task.FromException<AudioMetadata>(new LibraryValidationUnavailableException("Injected unavailable worker", new IOException()))
                : Task.FromResult(new AudioMetadata("Artist", Path.GetFileName(path), 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "folder");
        var planner = library.Planner(Settings)!;
        await WaitAsync(library, () => planner.AvailableItems == 1);
        await File.WriteAllTextAsync(Path.Combine(music, "new.wav"), "new fixture");
        await WaitAsync(library, () => library.Status("a").State == "Validation paused");
        Assert.Equal(1, planner.AvailableItems);
        Assert.Equal("healthy.wav", planner.Select(0).Item.Title);
        Volatile.Write(ref unavailable, false); await library.RescanAsync("a", "rescan");
        await WaitAsync(library, () => planner.AvailableItems == 2);
    }

    [Fact]
    public async Task IsolatedImportNeverWritesIntoAnExternalLiveFolderAndUiCanChooseManagedStorage()
    {
        var root = Root(); var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
        var source = Path.Combine(root, "source.wav"); await File.WriteAllTextAsync(source, "original");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        await using var library = new StationLibrary(journal, "unused", Path.Combine(root, "private"), ["a"], (_, _) => Task.FromResult(new AudioMetadata("Artist", "Song", 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", external, "folder");
        Assert.False(library.CanImportFiles(library.Page("a", "", 0).Root));
        await library.ImportAsync("a", [source], "external-import");
        await WaitAsync(library, () => library.Page("a", "", 0).Import?.State == "Failed");
        Assert.Empty(Directory.GetFileSystemEntries(external));
        await library.UseManagedRootAsync("a", "managed");
        Assert.True(library.CanImportFiles(library.Page("a", "", 0).Root));
        await library.ImportAsync("a", [source], "managed-import");
        await WaitAsync(library, () => library.Page("a", "", 0).Import?.State == "Completed");
        Assert.Single(library.Page("a", "", 0).Items);
        Assert.Empty(Directory.GetFileSystemEntries(external));
        Assert.Equal("original", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task StableAddChangeAndRemovalUpdateThePlannerWithoutDeletingHistory()
    {
        var root = Root(); var music = Path.Combine(root, "music"); Directory.CreateDirectory(Path.Combine(music, "subfolder"));
        var song = Path.Combine(music, "subfolder", "song.wav");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        var validations = 0;
        await using var library = new StationLibrary(journal, "unused", root, ["a"], (_, _) => Task.FromResult(new AudioMetadata("Artist", "Verified " + Interlocked.Increment(ref validations), 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "folder");
        await File.WriteAllTextAsync(song, "first fixture");
        var planner = library.Planner(Settings)!;
        await WaitAsync(library, () => library.Page("a", "", 0).Ready == 1 && planner.AvailableItems == 1);
        Assert.Equal("Verified 1", planner.Select(0).Item.Title);
        await File.WriteAllTextAsync(song, "a changed and longer fixture");
        await WaitAsync(library, () => planner.AvailableItems == 1 && planner.Select(0).Item.Title == "Verified 2");
        Assert.Equal("Verified 2", planner.Select(0).Item.Title);
        File.Delete(song);
        await WaitAsync(library, () => library.Page("a", "", 0).Missing == 1);
        await WaitAsync(library, () => planner.AvailableItems == 0);
        Assert.Throws<InvalidOperationException>(() => planner.Select(0));
        Assert.Equal(1, library.Page("a", "", 0).Total);
    }

    [Fact]
    public async Task UnavailableFolderPreservesCatalogAndReturnsWithoutADeveloperCommand()
    {
        var root = Root(); var music = Path.Combine(root, "music"); Directory.CreateDirectory(music);
        await File.WriteAllTextAsync(Path.Combine(music, "song.wav"), "fixture");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        await using var library = new StationLibrary(journal, "unused", root, ["a"], (_, _) => Task.FromResult(new AudioMetadata("Artist", "Song", 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "folder");
        await WaitAsync(library, () => library.Page("a", "", 0).Ready == 1);
        var planner = library.Planner(Settings)!;
        Directory.Move(music, music + "-offline");
        await WaitAsync(library, () => library.Status("a").State == "Unavailable");
        Assert.Equal(1, library.Page("a", "", 0).Ready); // historical validation, not present drive availability
        Assert.Equal(0, planner.AvailableItems);
        Directory.Move(music + "-offline", music);
        await WaitAsync(library, () => planner.AvailableItems == 1);
        Assert.Equal("Song", planner.Select(0).Item.Title);
    }

    [Fact]
    public async Task InterruptedImportResumesItsDurableJobWithoutOverwritingOrDuplicatingTheCopy()
    {
        var root = Root(); var source = Path.Combine(root, "operator-song.wav"); await File.WriteAllTextAsync(source, "original fixture");
        await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        var reachedProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StationLibrary(journal, "unused", root, ["a"], async (_, token) =>
        { reachedProbe.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new("", "", 1); });
        await first.InitializeAsync(); await first.ImportAsync("a", [source, source], "import");
        await reachedProbe.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await first.DisposeAsync(); await first.DisposeAsync();
        Assert.Equal("Pending", journal.PendingLibraryImport()!.State);
        await using var second = new StationLibrary(journal, "unused", root, ["a"], (_, _) => Task.FromResult(new AudioMetadata("Artist", "Operator song", 10)));
        await second.InitializeAsync();
        await WaitAsync(second, () => second.Page("a", "", 0).Import?.State == "Completed");
        Assert.Single(second.Page("a", "", 0).Items);
        Assert.Equal("original fixture", await File.ReadAllTextAsync(source));
        Assert.Single(Directory.GetFiles(second.Page("a", "", 0).Root!.Path, "*.wav"));
        Assert.Empty(Directory.GetFiles(second.Page("a", "", 0).Root!.Path, "*.partial"));
    }
}
