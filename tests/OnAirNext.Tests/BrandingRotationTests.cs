using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class BrandingRotationTests
{
    private static MediaItem Asset(string id, ContentKind kind) => new(id, "fixture-" + id + ".wav", "Original artist", id, kind);
    private static readonly BrandingRules Enabled = new(new(true, 2), new(true, 3));
    private static readonly MediaItem[] Assets =
    [Asset("jingle-b", ContentKind.Jingle), Asset("sweeper-b", ContentKind.Sweeper),
     Asset("jingle-a", ContentKind.Jingle), Asset("sweeper-a", ContentKind.Sweeper)];

    [Fact]
    public void DefaultRulesDoNotArmBranding()
    {
        var result = new BrandingRotation(BrandingRules.Disabled, Assets).Decide(new("station", 100, 100), 0, 0);
        Assert.Null(result.Item); Assert.Equal(BrandingKinds.None, result.Due);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 2, null)]
    [InlineData(2, 2, ContentKind.Jingle)]
    [InlineData(1, 3, ContentKind.Sweeper)]
    [InlineData(2, 3, ContentKind.Jingle)]
    [InlineData(200, 300, ContentKind.Jingle)]
    public void IndependentCountersChooseDueRole(long jingles, long sweepers, ContentKind? expected)
        => Assert.Equal(expected, new BrandingRotation(Enabled, Assets).Decide(new("station", jingles, sweepers), 0, 0).Item?.Kind);

    [Fact]
    public void SixCompletedSongsProduceTwoDistinctCadencesAndRetainTheCollision()
    {
        var rotation = new BrandingRotation(Enabled, Assets);
        long j = 0, s = 0, jp = 0, sp = 0;
        var inserted = new List<string>();
        for (var song = 1; song <= 6; song++)
        {
            j++; s++;
            while (rotation.Decide(new("station", j, s), jp, sp).Item is { } item)
            {
                inserted.Add(song + ":" + item.Id);
                // Simulate actual completion, not selection or queueing.
                if (item.Kind == ContentKind.Jingle) { j = 0; jp++; } else { s = 0; sp++; }
            }
        }
        Assert.Equal(new[] { "2:jingle-a", "3:sweeper-a", "4:jingle-b", "6:jingle-a", "6:sweeper-b" }, inserted);
    }

    [Theory]
    [InlineData(BrandingCollisionOrder.JingleFirst, ContentKind.Jingle, BrandingKinds.Sweeper)]
    [InlineData(BrandingCollisionOrder.SweeperFirst, ContentKind.Sweeper, BrandingKinds.Jingle)]
    public void CollisionIsExplicitAndOnlyCompletionCanReleaseTheOtherRole(BrandingCollisionOrder order, ContentKind first, BrandingKinds deferred)
    {
        var projected = new BrandingCadence("station", 6, 6);
        var rotation = new BrandingRotation(Enabled with { CollisionOrder = order }, Assets);
        var selected = rotation.Decide(projected, 0, 0);
        Assert.Equal(first, selected.Item!.Kind); Assert.Equal(deferred, selected.Deferred);
        Assert.Equal(BrandingKinds.Jingle | BrandingKinds.Sweeper, selected.Due);
        Assert.Equal(selected, rotation.Decide(projected, 0, 0));
        Assert.Equal(new BrandingCadence("station", 6, 6), projected);
    }

    [Fact]
    public void MissingPreferredPoolDoesNotBlockAvailableOtherRoleOrForgetDebt()
    {
        var result = new BrandingRotation(Enabled, Assets.Where(x => x.Kind == ContentKind.Sweeper))
            .Decide(new("station", 6, 6), 0, 0);
        Assert.Equal(ContentKind.Sweeper, result.Item!.Kind);
        Assert.Equal(BrandingKinds.Jingle, result.Deferred); Assert.Equal(BrandingKinds.Jingle, result.Missing);
    }

    [Fact]
    public void BothMissingPoolsReturnNoInsertionAndBothWarnings()
    {
        var result = new BrandingRotation(Enabled, []).Decide(new("station", 6, 6), 0, 0);
        Assert.Null(result.Item); Assert.Equal(result.Due, result.Deferred); Assert.Equal(result.Due, result.Missing);
    }

    [Fact]
    public void CompletedPlayOrdinalAndStableIdsControlRotationAcrossReconstruction()
    {
        var first = new BrandingRotation(Enabled, Assets);
        var restored = new BrandingRotation(Enabled, Assets.Reverse());
        foreach (var ordinal in new long[] { 0, 1, 2, long.MaxValue })
            Assert.Equal(first.Decide(new("station", 2, 0), ordinal, 0), restored.Decide(new("station", 2, 0), ordinal, 0));
        Assert.Equal("jingle-b", first.Decide(new("station", 2, 0), long.MaxValue, 0).Item!.Id);
    }

    [Fact]
    public void PoolIsFrozenBeforeAudioClockSelections()
    {
        var source = Assets.ToList(); var rotation = new BrandingRotation(Enabled, source); source.Clear();
        Assert.Equal(2, rotation.JingleCount); Assert.Equal(2, rotation.SweeperCount);
        Assert.Equal("Original artist", rotation.Decide(new("station", 2, 0), 0, 0).Item!.Artist);
    }

    [Fact]
    public void JournalSnapshotOrdinalSelectsTheNextAssetWithoutAnAttemptCounter()
    {
        var rotation = new BrandingRotation(Enabled, Assets);
        Assert.Equal("jingle-b", rotation.Decide(new("station", 2, 0, 1, 0)).Item!.Id);
        Assert.Throws<ArgumentNullException>(() => rotation.Decide(null!));
    }

    [Theory]
    [InlineData(0)] [InlineData(101)]
    public void InvalidIntervalsAreRejected(int interval)
        => Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled with { Jingle = new(true, interval) }, Assets));

    [Fact]
    public void InvalidOrderMixedRolesDuplicateIdsAndOversizePoolsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled with { CollisionOrder = (BrandingCollisionOrder)99 }, Assets));
        Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled, [Asset("song", ContentKind.Music)]));
        Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled, [Assets[0], Assets[0]]));
        Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled,
            Enumerable.Range(0, BrandingRotation.MaximumAssets + 1).Select(i => Asset("j" + i, ContentKind.Jingle))));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)] [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)] [InlineData(0, 0, 0, -1)]
    public void InvalidCompletionCountersCannotSilentlyResetCadence(long j, long s, long jp, long sp)
        => Assert.Throws<ArgumentException>(() => new BrandingRotation(Enabled, Assets).Decide(new("station", j, s), jp, sp));
}
