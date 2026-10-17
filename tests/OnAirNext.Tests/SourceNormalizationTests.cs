using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class SourceNormalizationTests
{
    private static SourceLoudness Measured(double? loudness = -18, double? peak = -4) => new(new string('A',64),100,123,60,loudness,peak,5,DateTimeOffset.UtcNow);
    [Theory]
    [InlineData(-18,-4,-5,"Target gain")]
    [InlineData(-30,-20,7,"Target gain")]
    [InlineData(-50,-40,12,"Boost constrained")]
    [InlineData(-30,-3,1.6,"Peak constrained")]
    public void FixedGainHonorsTargetPeakAndBoostBound(double loudness,double peak,double expected,string state)
    {
        var gain=SourceNormalization.Decide(Measured(loudness,peak));
        Assert.Equal(expected,gain.AppliedDb,8); Assert.Equal(state,gain.State);
        Assert.True(gain.PredictedTruePeakDbtp <= SourceNormalization.SourceCeilingDbtp+1e-9);
        Assert.Equal(Math.Pow(10,expected/20),gain.Linear,12);
    }
    [Fact]
    public void SilenceAndMissingMeasurementsNeverInventTargetLoudness()
    {
        var silent=SourceNormalization.Decide(Measured(null,null));
        Assert.Equal(0,silent.AppliedDb);Assert.Null(silent.PredictedLufs);Assert.Equal("No gated programme",silent.State);
        var unknown=SourceNormalization.Decide(null);Assert.Equal("Unmeasured",unknown.State);Assert.Null(unknown.PredictedLufs);
        var transient=SourceNormalization.Decide(Measured(null,0));Assert.Equal(-1.4,transient.AppliedDb);Assert.Null(transient.PredictedLufs);
    }
    private static string Summary(string integrated="-23.1",string peak="-8.0") => $"""
        [Parsed_ebur128] Summary:
          Integrated loudness:
            I: {integrated} LUFS
            Threshold: -33.0 LUFS
          Loudness range:
            LRA: 4.2 LU
          True peak:
            Peak: {peak} dBFS
        """;
    [Fact]
    public void ParserRequiresFinalCompleteSummaryAndWithholdsUnsettledRange()
    {
        var result=SourceLoudness.FromSummary(Summary("-70.0","-inf")+"\n"+Summary(),new string('A',64),100,123,20);
        Assert.Equal(-23.1,result.IntegratedLufs);Assert.Equal(-8,result.TruePeakDbtp);Assert.Null(result.LoudnessRangeLu);
        var silence=SourceLoudness.FromSummary(Summary("-70.0","-inf"),new string('B',64),100,123,60);
        Assert.Null(silence.IntegratedLufs);Assert.Null(silence.TruePeakDbtp);Assert.Null(silence.LoudnessRangeLu);
        Assert.Throws<InvalidDataException>(()=>SourceLoudness.FromSummary(Summary()+"\nSummary:\n I: -20.0 LUFS",new string('A',64),100,123,60));
        Assert.Throws<ArgumentException>(()=>SourceNormalization.Decide(Measured(double.NaN)));
    }
    [Fact]
    public async Task MeasurementSurvivesReopenAndReanalysisDoesNotWithdrawTheValidatedSource()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        var db=Path.Combine(root,"catalog.db");
        await using(var journal=new EventJournal(db))
        {
            await journal.InitializeLibraryAsync();await journal.InitializeDesiredStateAsync(new("a",[],[],AutoStart:false));
            await journal.SetLibraryRootAsync("a",Path.Combine(root,"music"),"folder");
            var library=journal.LibraryRoots().Single();
            var item=new LibraryAsset("asset","song.wav","Artist","Song",60,100,123,"Ready",Loudness:Measured());
            await journal.ApplyLibraryChangesAsync(library,[item],[]);
            var measured=Assert.Single(journal.LibraryPage("a").Items);Assert.Equal(-5,measured.Normalization!.AppliedDb);
            Assert.True(await journal.ChangeLibraryAssetAsync("a",library.Revision,"asset",0,LibraryEditKind.AnalyzeLoudness,"analyze"));
            var pending=Assert.Single(journal.LibraryPage("a").Items);Assert.True(pending.AnalysisRequested);Assert.Equal("Ready",pending.State);Assert.Equal(measured.Loudness,pending.Loudness);
            Assert.False(await journal.ChangeLibraryAssetAsync("a",library.Revision,"asset",0,LibraryEditKind.AnalyzeLoudness,"analyze"));
        }
        await using(var journal=new EventJournal(db))
        {
            await journal.InitializeLibraryAsync();var item=Assert.Single(journal.LibraryPage("a").Items);
            Assert.True(item.AnalysisRequested);Assert.Equal(-5,item.Normalization!.AppliedDb);
            var library=journal.LibraryRoots().Single();
            await Assert.ThrowsAsync<ArgumentException>(()=>journal.ApplyLibraryChangesAsync(library,[item with{Bytes=101}],[]));
            Assert.True(Assert.Single(journal.LibraryPage("a").Items).AnalysisRequested);
            await journal.ApplyLibraryChangesAsync(library,[item with{Loudness=Measured(-24,-10)}],[]);
            var replaced=Assert.Single(journal.LibraryPage("a").Items);Assert.False(replaced.AnalysisRequested);Assert.Equal(1,replaced.Normalization!.AppliedDb);
            await journal.ChangeLibraryAssetAsync("a",library.Revision,"asset",1,LibraryEditKind.Retire,"retire");
            await journal.ChangeLibraryAssetAsync("a",library.Revision,"asset",2,LibraryEditKind.Restore,"restore");
            var restored=Assert.Single(journal.LibraryPage("a").Items);Assert.Equal("Invalid",restored.State);Assert.Null(restored.Loudness);Assert.Null(restored.Normalization);
        }
    }
}
