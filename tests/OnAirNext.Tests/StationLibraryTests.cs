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
