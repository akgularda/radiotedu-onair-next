using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LibraryEditingWorkerTests
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
    public async Task RemovalUpdatesEligibleRotationAndRestorationWaitsForValidation()
    {
        var directory = Root(); var music = Path.Combine(directory, "music"); Directory.CreateDirectory(music);
        await File.WriteAllTextAsync(Path.Combine(music, "one.wav"), "first fixture");
        await File.WriteAllTextAsync(Path.Combine(music, "two.wav"), "second fixture");
        await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings); var pause = false; var probes = 0;
        await using var library = new StationLibrary(journal, "unused", directory, ["a"], (path, _) =>
        {
            Interlocked.Increment(ref probes);
            return Volatile.Read(ref pause) ? Task.FromException<AudioMetadata>(new LibraryValidationUnavailableException("Injected paused validator", new IOException()))
                : Task.FromResult(new AudioMetadata("Artist", Path.GetFileNameWithoutExtension(path), 10));
        });
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "root");
        var planner = library.Planner(Settings)!; await WaitAsync(library, () => planner.AvailableItems == 2);
        var page = library.Page("a", "one", 0); var asset = Assert.Single(page.Items); var revision = page.Root!.Revision;
        var heard = Enumerable.Range(0,2).Select(i => planner.Select(i).Item).Single(x => x.Id == asset.Id);
        var readback = await library.EditAssetAsync("a", revision, asset.Id, 0, LibraryEditKind.Metadata, "metadata", "Manual artist", "Manual title");
        Assert.Equal("Manual title", readback.Title); Assert.Equal("one", heard.Title);
        Assert.Contains(Enumerable.Range(0,2).Select(i => planner.Select(i).Item), x => x.Id == asset.Id && x.Title == "Manual title");
        await library.EditAssetAsync("a", revision, asset.Id, 1, LibraryEditKind.Retire, "remove");
        Assert.Equal(1, planner.AvailableItems);
        Assert.All(Enumerable.Range(0,8).Select(i => planner.Select(i).Item), item => Assert.NotEqual(asset.Id,item.Id));
        var beforeRestore = probes; Volatile.Write(ref pause,true);
        var restored = await library.EditAssetAsync("a", revision, asset.Id, 2, LibraryEditKind.Restore, "restore");
        Assert.Equal("Invalid", restored.State); Assert.Equal(1, planner.AvailableItems);
        await WaitAsync(library, () => library.Status("a").State == "Validation paused");
        Assert.Equal(1, planner.AvailableItems); Volatile.Write(ref pause,false);
        await WaitAsync(library, () => planner.AvailableItems == 2);
        Assert.True(probes >= beforeRestore + 2);
        Assert.Equal("Manual title", library.Page("a", "Manual title", 0).Items.Single().Title);
        Assert.Equal("first fixture", await File.ReadAllTextAsync(Path.Combine(music,"one.wav")));
    }

    [Fact]
    public async Task MetadataEditCannotReviveCachedSongsOnAnUnavailableDrive()
    {
        var directory = Root(); var music = Path.Combine(directory, "music"); var offline = Path.Combine(directory, "offline");
        Directory.CreateDirectory(music); await File.WriteAllTextAsync(Path.Combine(music, "song.wav"), "fixture");
        await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        await journal.InitializeDesiredStateAsync(Settings);
        await using var library = new StationLibrary(journal, "unused", directory, ["a"], (_, _) => Task.FromResult(new AudioMetadata("Artist", "Song", 10)));
        await library.InitializeAsync(); await library.SetRootAsync("a", music, "root");
        var planner = library.Planner(Settings)!; await WaitAsync(library, () => planner.AvailableItems == 1);
        var page = library.Page("a", "", 0); var asset = Assert.Single(page.Items);
        foreach(var path in new[]{music,offline}) Assert.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,Path.GetFullPath(path));
        Directory.Move(music, offline);
        await WaitAsync(library, () => library.Status("a").State == "Unavailable" && planner.AvailableItems == 0);
        var edited = await library.EditAssetAsync("a", page.Root!.Revision, asset.Id, 0, LibraryEditKind.Metadata, "edit", "Manual", "Offline edit");
        Assert.Equal("Offline edit", edited.Title); Assert.Equal(0, planner.AvailableItems);
        Directory.Move(offline, music);
        await WaitAsync(library, () => planner.AvailableItems == 1);
        Assert.Equal("Offline edit", planner.Select(0).Item.Title);
    }
    [Fact]
    public async Task ReanalysisKeepsThePreviousValidatedGainUntilTheNewScanCommits()
    {
        var directory=Root();var music=Path.Combine(directory,"music");Directory.CreateDirectory(music);
        var file=Path.Combine(music,"song.wav");await File.WriteAllTextAsync(file,"synthetic catalog fixture");
        var signature=new FileInfo(file);var refreshing=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioMetadata Metadata(double level)=>new("Artist","Song",60,new(new string('A',64),signature.Length,signature.LastWriteTimeUtc.Ticks,60,level,-10,4,DateTimeOffset.UtcNow));
        await using var journal=new EventJournal(Path.Combine(directory,"catalog.db"));await journal.InitializeDesiredStateAsync(Settings);
        await using var library=new StationLibrary(journal,"unused",directory,["a"],async (_,token)=>
        {
            if(!Volatile.Read(ref refreshing))return Metadata(-18);
            entered.TrySetResult();await release.Task.WaitAsync(token);return Metadata(-26);
        });
        await library.InitializeAsync();await library.SetRootAsync("a",music,"root");var planner=library.Planner(Settings)!;
        await WaitAsync(library,()=>planner.AvailableItems==1);
        var page=library.Page("a","",0);var item=Assert.Single(page.Items);Assert.Equal(-5,SourceNormalization.Decide(planner.Select(0).Item.Loudness).AppliedDb);
        Volatile.Write(ref refreshing,true);
        var queued=await library.EditAssetAsync("a",page.Root!.Revision,item.Id,0,LibraryEditKind.AnalyzeLoudness,"analysis");
        Assert.True(queued.AnalysisRequested);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1,planner.AvailableItems);Assert.Equal(-5,SourceNormalization.Decide(planner.Select(0).Item.Loudness).AppliedDb);
        release.TrySetResult();await WaitAsync(library,()=>!library.Page("a","",0).Items.Single().AnalysisRequested && SourceNormalization.Decide(planner.Select(0).Item.Loudness).AppliedDb==3);
        Assert.Equal(1,planner.AvailableItems);Assert.Equal(3,SourceNormalization.Decide(planner.Select(0).Item.Loudness).AppliedDb);
    }

}
