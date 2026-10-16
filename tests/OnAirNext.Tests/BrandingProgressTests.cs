using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class BrandingProgressTests
{
    private static string Database()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return Path.Combine(root,"branding-progress.db");
    }
    private static readonly BrandingRules Rules=new(new(true,2),new(true,3));
    private static readonly MediaItem Jingle=new("j","j.wav","Fixture","Jingle",ContentKind.Jingle);
    private static readonly MediaItem Sweeper=new("s","s.wav","Fixture","Sweeper",ContentKind.Sweeper);
    private static PlaybackEvent Entry(ContentKind kind,string occurrence,string type="started")
        =>new(Guid.NewGuid().ToString("N"),occurrence,"station",type,occurrence,"Fixture",occurrence,kind,DateTimeOffset.UtcNow,0);
    private static async Task<PlaybackEvent> Complete(EventJournal journal,ContentKind kind,string occurrence,string type="ended")
    {
        var previous=EventJournal.ReadEvents(journal.DatabasePath).LastOrDefault();
        var start=Entry(kind,occurrence) with{SamplePosition=previous?.SamplePosition??0,AtUtc=previous?.AtUtc??DateTimeOffset.UtcNow};
        await journal.AppendAsync(start);
        var end=start with{EventId=Guid.NewGuid().ToString("N"),Type=type,SamplePosition=start.SamplePosition+48000,AtUtc=start.AtUtc.AddSeconds(1)};
        await journal.AppendAsync(end);return end;
    }

    [Fact]
    public async Task SessionProjectionMatchesCommittedJournalWithoutRebasingOrCountingPreparation()
    {
        await using var journal=new EventJournal(Database());
        var projected=await journal.BrandingProgressAsync("station");
        var sequence=new[]{(ContentKind.Music,"ended"),(ContentKind.Music,"skipped"),(ContentKind.Music,"ended"),
            (ContentKind.Jingle,"skipped"),(ContentKind.Music,"ended"),(ContentKind.Sweeper,"failed"),
            (ContentKind.Advertisement,"ended"),(ContentKind.Program,"ended"),(ContentKind.Music,"ended"),
            (ContentKind.Jingle,"ended"),(ContentKind.Sweeper,"ended")};
        for(var i=0;i<sequence.Length;i++)
        {
            var (kind,type)=sequence[i];var prediction=projected.Project(kind,"ended");
            Assert.Equal(projected,await journal.BrandingProgressAsync("station"));
            projected=projected.Project(kind,type);await Complete(journal,kind,"occurrence-"+i,type);
            Assert.Equal(projected,await journal.BrandingProgressAsync("station"));
            if(type=="ended")Assert.Equal(prediction,projected);
        }
        Assert.Equal(4,projected.CompletedMusic);
        Assert.Equal(1,projected.Cadence.CompletedJingles);Assert.Equal(1,projected.Cadence.CompletedSweepers);
        Assert.True(EventJournal.Verify(journal.DatabasePath));
    }

    [Fact]
    public void FailedOrSkippedBrandingIsHeldUntilAnActualSongCompletes()
    {
        var pool=new BrandingRotation(Rules,[Jingle,Sweeper]);
        var progress=new BrandingProgress(new("station",6,6),6);
        Assert.Equal(ContentKind.Jingle,pool.DecideProjected(progress).Item!.Kind);
        progress=progress.Project(ContentKind.Jingle,"skipped");
        Assert.Equal(BrandingKinds.Jingle,pool.DecideProjected(progress).Held);
        Assert.Equal(ContentKind.Sweeper,pool.DecideProjected(progress).Item!.Kind);
        progress=progress.Project(ContentKind.Sweeper,"decode_failed");
        Assert.Null(pool.DecideProjected(progress).Item);
        Assert.Equal(BrandingKinds.Jingle|BrandingKinds.Sweeper,pool.DecideProjected(progress).Held);
        Assert.Null(pool.DecideProjected(progress.Project(ContentKind.Music,"skipped")).Item);
        progress=progress.Project(ContentKind.Music,"ended");
        Assert.Equal(ContentKind.Jingle,pool.DecideProjected(progress).Item!.Kind);
        Assert.Equal(0,progress.Cadence.CompletedJingles);Assert.Equal(0,progress.Cadence.CompletedSweepers);
    }

    [Fact]
    public async Task ReplayedFailureCannotMoveAnOldAttemptIntoANewerMusicSlot()
    {
        var db=Database();
        await using(var journal=new EventJournal(db))
        {
            await Complete(journal,ContentKind.Music,"music-1");await Complete(journal,ContentKind.Music,"music-2");
            var failure=await Complete(journal,ContentKind.Jingle,"jingle","failed");
            Assert.Equal(BrandingKinds.Jingle,(await journal.BrandingProgressAsync("station")).Held);
            await Complete(journal,ContentKind.Music,"music-3");
            await journal.AppendAsync(failure with{EventId=Guid.NewGuid().ToString("N"),Type="decode_failed"});
            var saved=await journal.BrandingProgressAsync("station");
            Assert.Equal(3,saved.CompletedMusic);Assert.Equal(2,saved.JingleAttemptMusic);
            Assert.Equal(BrandingKinds.None,saved.Held);Assert.Equal(0,saved.Cadence.CompletedJingles);
        }
        await using(var journal=new EventJournal(db))Assert.Equal(2,(await journal.BrandingProgressAsync("station")).JingleAttemptMusic);
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task RecoveredSkipOfAnInsertionKeepsTheOrdinaryMusicIndex()
    {
        var db=Database();await using var journal=new EventJournal(db);
        await Complete(journal,ContentKind.Music,"one");await Complete(journal,ContentKind.Music,"two");
        var start=Entry(ContentKind.Jingle,"held") with{ItemId=Jingle.Id,Artist=Jingle.Artist,Title=Jingle.Title,SamplePosition=96000};
        var checkpoint=new PlayoutCheckpoint("station","Playing",2,Jingle,"held",1000,97000,true,start.AtUtc.AddSeconds(1000d/48000),HoldsRotation:true);
        await journal.AppendAsync(start,checkpoint);
        Assert.True(await journal.RequiresHeldRotationRecoveryAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.RequireRecoveryReaderAsync(null));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.RequireRecoveryReaderAsync(["unknown-reader"]));
        await journal.RequireRecoveryReaderAsync([RecoveryCompatibility.HeldRotationFeature]);
        Assert.True(await journal.RequestSkipAsync("station","skip-command","held"));
        var recovered=await journal.PrepareRecoveryAsync("station");
        Assert.NotNull(recovered);Assert.Null(recovered.Item);Assert.Equal(2,recovered.RotationIndex);Assert.False(recovered.HoldsRotation);
        var progress=await journal.BrandingProgressAsync("station");
        Assert.Equal(BrandingKinds.Jingle,progress.Held);Assert.Equal(0,progress.Cadence.CompletedJingles);
        Assert.False(await journal.RequiresHeldRotationRecoveryAsync());
        await journal.RequireRecoveryReaderAsync(null);
        Assert.Equal(recovered,await journal.PrepareRecoveryAsync("station"));Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task UnavailableInterruptedInsertionIsDiscardedWithHistoryAndNoMusicIndexLoss()
    {
        var db=Database();await using var journal=new EventJournal(db);
        var start=Entry(ContentKind.Jingle,"held") with{ItemId=Jingle.Id,Artist=Jingle.Artist,Title=Jingle.Title};
        await journal.AppendAsync(start,new("station","Playing",7,Jingle,"held",1000,1000,true,start.AtUtc.AddSeconds(1000d/48000),HoldsRotation:true));
        var pending=await journal.PrepareRecoveryAsync("station");Assert.True(pending!.HoldsRotation);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.DiscardUnavailableHeldRecoveryAsync("station","wrong"));
        var discarded=await journal.DiscardUnavailableHeldRecoveryAsync("station","held");
        Assert.Equal(7,discarded.RotationIndex);Assert.Null(discarded.Item);Assert.False(discarded.HoldsRotation);
        Assert.Equal(BrandingKinds.Jingle,(await journal.BrandingProgressAsync("station")).Held);
        Assert.Contains(EventJournal.ReadEvents(db),x=>x.OccurrenceId=="held"&&x.Type=="decode_failed"&&x.TimingQuality=="last_committed_checkpoint");
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public void DisabledRemovedOrReclassifiedAssetsCannotResumeButApprovedTagEditsCan()
    {
        Assert.Null(new BrandingRotation(BrandingRules.Disabled,[Jingle]).ResolveRecovery(Jingle));
        Assert.Null(new BrandingRotation(Rules,[]).ResolveRecovery(Jingle));
        Assert.Null(new BrandingRotation(Rules,[Jingle with{Kind=ContentKind.Sweeper}]).ResolveRecovery(Jingle));
        var retitled=Jingle with{Title="Edited title"};
        Assert.Equal(retitled,new BrandingRotation(Rules,[retitled]).ResolveRecovery(Jingle));
    }

    [Fact]
    public void FailedCandidatesAreExcludedWithoutConsumingCompletionOrdinals()
    {
        var next=Jingle with{Id="z"};var pool=new BrandingRotation(Rules,[Jingle,next]);
        var progress=new BrandingProgress(new("station",2,0),2);
        Assert.Equal(next,pool.DecideProjected(progress,new HashSet<string>{Jingle.Id}).Item);
        var missing=pool.DecideProjected(progress,new HashSet<string>{Jingle.Id,next.Id});
        Assert.Null(missing.Item);Assert.Equal(BrandingKinds.Jingle,missing.Missing);
        Assert.Equal(0,progress.Cadence.CompletedJingles);
    }

    [Fact]
    public void OrdinaryCheckpointJsonRemainsCompatibleAndInsertionIntentIsExplicit()
    {
        var ordinary=new PlayoutCheckpoint("station","Stopped",2,Jingle,"old",0,48000,false,DateTimeOffset.UtcNow);
        var json=System.Text.Json.JsonSerializer.Serialize(ordinary);Assert.DoesNotContain("HoldsRotation",json);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<PlayoutCheckpoint>(json)!.HoldsRotation);
        Assert.Contains("HoldsRotation",System.Text.Json.JsonSerializer.Serialize(ordinary with{HoldsRotation=true}));
        Assert.True(new PlayoutSelection(Jingle).AdvancesRotation);
        Assert.False(new PlayoutSelection(Jingle,HoldsRotation:true).AdvancesRotation);
        Assert.False(new PlayoutSelection(Jingle,"queue-entry").AdvancesRotation);
    }

    [Fact]
    public async Task NewMusicEpochMigrationKeepsEarlierDebtAndBrandingOrdinalsWithoutAHistoryScan()
    {
        var db=Database();
        await using(var journal=new EventJournal(db))
        {
            await Complete(journal,ContentKind.Music,"old-one");await Complete(journal,ContentKind.Music,"old-two");
            await Complete(journal,ContentKind.Jingle,"old-jingle");
        }
        // Recreate the preceding development schema in this disposable fixture.
        using(var old=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+db))
        {
            old.Open();using var command=old.CreateCommand();
            command.CommandText="""
                DROP TRIGGER cadence_jingle_completed; DROP TRIGGER cadence_sweeper_completed; DROP TRIGGER cadence_music_completed;
                DROP TABLE branding_attempts;
                ALTER TABLE station_cadence DROP COLUMN completed_music;
                ALTER TABLE station_cadence DROP COLUMN last_jingle_attempt_music;
                ALTER TABLE station_cadence DROP COLUMN last_sweeper_attempt_music;
                CREATE TRIGGER cadence_jingle_completed AFTER UPDATE OF music_since_jingle ON station_cadence WHEN NEW.music_since_jingle=0
                BEGIN UPDATE station_cadence SET completed_jingles=completed_jingles+1 WHERE station_id=NEW.station_id; END;
                CREATE TRIGGER cadence_sweeper_completed AFTER UPDATE OF music_since_sweeper ON station_cadence WHEN NEW.music_since_sweeper=0
                BEGIN UPDATE station_cadence SET completed_sweepers=completed_sweepers+1 WHERE station_id=NEW.station_id; END;
                """;
            command.ExecuteNonQuery();
        }
        var before=EventJournal.ReadEvents(db).Count();
        await using(var journal=new EventJournal(db))
        {
            var migrated=await journal.BrandingProgressAsync("station");
            Assert.Equal(new BrandingCadence("station",0,2,1,0),migrated.Cadence);
            Assert.Equal(0,migrated.CompletedMusic);Assert.Equal(-1,migrated.JingleAttemptMusic);
            Assert.Equal(before,EventJournal.ReadEvents(db).Count());
            await Complete(journal,ContentKind.Music,"new-music");await Complete(journal,ContentKind.Sweeper,"new-sweeper");
        }
        await using(var journal=new EventJournal(db))
        {
            var reopened=await journal.BrandingProgressAsync("station");
            Assert.Equal(1,reopened.CompletedMusic);Assert.Equal(1,reopened.SweeperAttemptMusic);
            Assert.Equal(1,reopened.Cadence.CompletedJingles);Assert.Equal(1,reopened.Cadence.CompletedSweepers);
        }
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public void SourcePublishesImmutablePoolChangesWithoutDuplicateNotifications()
    {
        var source=new BrandingSource();var count=0;source.Changed+=()=>count++;
        var pool=new BrandingRotation(Rules,[Jingle]);source.Publish(pool);source.Publish(pool);
        Assert.Same(pool,source.Snapshot());Assert.Equal(1,count);
        source.Publish(null);Assert.Null(source.Snapshot());Assert.Equal(2,count);
    }

    [Theory]
    [InlineData(-1,-1,-1)] [InlineData(0,1,-1)] [InlineData(0,-1,1)] [InlineData(0,-2,-1)]
    public void InvalidProjectionStateIsRejected(long music,long jingle,long sweeper)
        =>Assert.Throws<InvalidDataException>(()=>new BrandingProgress(new("station",0,0),music,jingle,sweeper).Validate());
}
