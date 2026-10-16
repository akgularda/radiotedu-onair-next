using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static partial class ProcessingChecks
{
    private static BrandingSource Branding(MediaItem jingle,MediaItem? sweeper=null,
        BrandingCollisionOrder order=BrandingCollisionOrder.JingleFirst)
    {
        var source=new BrandingSource();
        source.Publish(new BrandingRotation(new(new(true,2),new(sweeper is not null,3),order),
            sweeper is null?[jingle]:[jingle,sweeper]));
        return source;
    }

    private static async Task BrandingChecksAsync(string ffmpeg,string root,CaseResult result)
    {
        foreach(var order in Enum.GetValues<BrandingCollisionOrder>())
            await BrandingOrderAsync(ffmpeg,root,result,order);
        await BrandingSkipAsync(ffmpeg,root,result);
        await BrandingFailureAsync(ffmpeg,root,result);
        await BrandingRecoveryAsync(ffmpeg,root,result);
    }

    private static async Task BrandingOrderAsync(string ffmpeg,string root,CaseResult result,BrandingCollisionOrder order)
    {
        var name="branding-"+order;var music=Enumerable.Range(1,7).Select(i=>Fixture(root,name+i,36000,.03f)).ToArray();
        var j=Fixture(root,name+"-j",4800,.12f,ContentKind.Jingle);
        var s=Fixture(root,name+"-s",4800,.06f,ContentKind.Sweeper);
        await using var journal=new EventJournal(Path.Combine(root,name+".db"));
        var sink=new Capture(journal.DatabasePath);var maximumChildren=0;
        sink.AfterEach=()=>maximumChildren=Math.Max(maximumChildren,ChildProcess.ActiveProcesses.Count);
        await using var engine=new StationEngine(ffmpeg,new(name,music,[],Repeat:false),journal,[sink],branding:Branding(j,s,order));
        await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var events=EventJournal.ReadEvents(journal.DatabasePath);var starts=events.Where(x=>x.Type=="started").ToArray();
        var collision=order==BrandingCollisionOrder.JingleFirst?new[]{j.Id,s.Id}:new[]{s.Id,j.Id};
        var expected=new[]{music[0].Id,music[1].Id,j.Id,music[2].Id,s.Id,music[3].Id,j.Id,music[4].Id,music[5].Id}
            .Concat(collision).Append(music[6].Id);
        Check(result,starts.Select(x=>x.ItemId).SequenceEqual(expected),$"{order}: actual audible starts include both collision clips without consuming a music position.");
        Check(result,sink.Frames==276000&&!sink.FutureTerminalObserved&&events.Count(x=>x.Type=="ended")==12,
            $"{order}: seven physical songs and five branding clips preserve all 276,000 frames and exact committed boundaries.");
        foreach(var start in starts.Where(x=>x.Kind is ContentKind.Jingle or ContentKind.Sweeper))
        {
            var end=events.Single(x=>x.Type=="ended"&&x.OccurrenceId==start.OccurrenceId);
            var amplitude=start.Kind==ContentKind.Jingle?.12:.06;
            var sum=0d;for(var i=start.SamplePosition;i<end.SamplePosition;i++)sum+=Math.Pow(sink.Samples[checked((int)(i*2))],2);
            var rms=Math.Sqrt(sum/(end.SamplePosition-start.SamplePosition));
            Check(result,end.SamplePosition-start.SamplePosition==4800&&Math.Abs(rms-amplitude/Math.Sqrt(2))<.0002,
                $"{order}: {start.Kind} occurrence {start.OccurrenceId} has its real non-silent PCM amplitude and duration.");
        }
        var progress=await journal.BrandingProgressAsync(name);
        Check(result,progress is{CompletedMusic:7,Cadence:{MusicSinceJingle:1,MusicSinceSweeper:1,CompletedJingles:3,CompletedSweepers:2}},
            $"{order}: durable cadence matches completed audible music and both independently completed branding roles.");
        Check(result,maximumChildren<=4&&EventJournal.Verify(journal.DatabasePath),$"{order}: bounded decoder ownership and immutable journal remain intact.");
        result.Details[name]=new{Events=events,OutputFrames=sink.Frames,MaximumChildren=maximumChildren,Progress=progress};
    }

    private static async Task BrandingSkipAsync(string ffmpeg,string root,CaseResult result)
    {
        const string name="branding-skip";
        var music=Enumerable.Range(1,6).Select(i=>Fixture(root,name+i,i==2?192000:36000,.03f)).ToArray();
        var j=Fixture(root,name+"-j",4800,.12f,ContentKind.Jingle);var s=Fixture(root,name+"-s",4800,.06f,ContentKind.Sweeper);
        await using var journal=new EventJournal(Path.Combine(root,name+".db"));var sink=new Capture(journal.DatabasePath);
        StationEngine? running=null;Task? skip=null;
        sink.AfterEach=()=>{var state=running!.Snapshot();if(skip is null&&state.CurrentAssetId==music[1].Id&&state.CurrentItemSamples>=12000)skip=running.SkipAsync(name);};
        await using var engine=new StationEngine(ffmpeg,new(name,music,[],Repeat:false),journal,[sink],branding:Branding(j,s));running=engine;
        await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(30));if(skip is not null)await skip;
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        Check(result,skip is not null&&events.Count(x=>x.Type=="skipped"&&x.ItemId==music[1].Id)==1,
            "Skip is accepted during the second song, whose predicted completion had prepared a jingle.");
        var expected=new[]{music[0].Id,music[1].Id,music[2].Id,j.Id,music[3].Id,s.Id,music[4].Id,j.Id,music[5].Id};
        Check(result,events.Where(x=>x.Type=="started").Select(x=>x.ItemId).SequenceEqual(expected),
            "An interrupted song cannot make its predicted jingle due; the ready ordinary successor plays first.");
        var progress=await journal.BrandingProgressAsync(name);
        Check(result,progress is{CompletedMusic:5,Cadence:{MusicSinceJingle:1,MusicSinceSweeper:2,CompletedJingles:2,CompletedSweepers:1}}&&
            !sink.FutureTerminalObserved&&EventJournal.Verify(journal.DatabasePath),"Skip projection, durable cadence and audible journal agree without counting an interrupted song.");
        result.Details[name]=new{Events=events,OutputFrames=sink.Frames,Progress=progress};
    }

    private static async Task BrandingFailureAsync(string ffmpeg,string root,CaseResult result)
    {
        const string name="branding-failure";
        var music=Enumerable.Range(1,4).Select(i=>Fixture(root,name+i,36000,.03f)).ToArray();
        var missing=new MediaItem(name+"-missing",Path.Combine(root,"does-not-exist.wav"),"Synthetic",name,ContentKind.Jingle);
        await using var journal=new EventJournal(Path.Combine(root,name+".db"));var sink=new Capture(journal.DatabasePath);
        await using var engine=new StationEngine(ffmpeg,new(name,music,[],Repeat:false),journal,[sink],branding:Branding(missing));
        await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var events=EventJournal.ReadEvents(journal.DatabasePath);var progress=await journal.BrandingProgressAsync(name);
        Check(result,events.Where(x=>x.Type=="started").Select(x=>x.ItemId).SequenceEqual(music.Select(x=>x.Id))&&
            events.Count(x=>x.Type=="ended")==4&&sink.Frames==144000,"A failed branding decoder cannot consume a song, add silence frames or fault a healthy finite music rotation.");
        Check(result,events.Count(x=>x.Type=="decode_failed"&&x.ItemId==missing.Id)==1&&
            progress is{CompletedMusic:4,JingleAttemptMusic:2,Cadence:{CompletedJingles:0,MusicSinceJingle:4}},
            "A missing jingle is attempted once, keeps its unpaid debt and never consumes its completed-play ordinal.");
        Check(result,!sink.FutureTerminalObserved&&EventJournal.Verify(journal.DatabasePath),"Failed branding preserves exact audible history and hash-chain integrity.");
        result.Details[name]=new{Events=events,OutputFrames=sink.Frames,Progress=progress};
    }

    private static async Task BrandingRecoveryAsync(string ffmpeg,string root,CaseResult result)
    {
        const string name="branding-recovery";
        var music=Enumerable.Range(1,3).Select(i=>Fixture(root,name+i,36000,.03f)).ToArray();
        var j=Fixture(root,name+"-j",28800,.12f,ContentKind.Jingle);var source=Branding(j);
        await using var journal=new EventJournal(Path.Combine(root,name+".db"));StationEngine? running=null;Task? stopped=null;
        var sink=new Capture(journal.DatabasePath);
        sink.AfterEach=()=>{var state=running!.Snapshot();if(stopped is null&&state.CurrentAssetId==j.Id&&state.CurrentItemSamples>=4800)stopped=running.DisposeAsync().AsTask();};
        running=new StationEngine(ffmpeg,new(name,music,[],Repeat:false),journal,[sink],branding:source);
        await running.StartAsync();await running.Completion.WaitAsync(TimeSpan.FromSeconds(30));if(stopped is not null)await stopped;
        var saved=await journal.CheckpointAsync(name);var before=EventJournal.ReadEvents(journal.DatabasePath);
        Check(result,stopped is not null&&saved is{HoldsRotation:true,RotationIndex:2,State:"Stopped"}&&saved.Item?.Id==j.Id&&
            !before.Any(x=>x.Type=="ended"&&x.ItemId==j.Id),"Stopping inside an automatic jingle retains the unheard third song and an unfinished held-rotation checkpoint.");
        Check(result,await journal.RequiresHeldRotationRecoveryAsync(),"Interrupted branding explicitly requires a compatible recovery reader.");
        var resumed=new Capture(journal.DatabasePath);
        await using(var next=new StationEngine(ffmpeg,new(name,music,[],Repeat:false),journal,[resumed],branding:source))
        {await next.StartAsync();await next.Completion.WaitAsync(TimeSpan.FromSeconds(30));}
        var events=EventJournal.ReadEvents(journal.DatabasePath);var starts=events.Where(x=>x.Type=="started"&&x.ItemId==j.Id).ToArray();
        Check(result,starts.Length==2&&starts[1].PreviousOccurrenceId==starts[0].OccurrenceId&&
            Math.Abs(resumed.FirstLeft-FirstSample(.12f))<1e-8&&resumed.Frames==64800,
            "Recovery restarts unfinished branding at sample zero, links the attempt, and then plays the full retained third song.");
        var progress=await journal.BrandingProgressAsync(name);
        Check(result,progress is{CompletedMusic:3,Cadence:{CompletedJingles:1,MusicSinceJingle:1}}&&
            events.Count(x=>x.Type=="ended"&&x.ItemId==music[2].Id)==1&&EventJournal.Verify(journal.DatabasePath),
            "An interrupted branding attempt is counted only on its later audible completion; ordinary music is neither duplicated nor omitted.");
        result.Details[name]=new{Checkpoint=saved,Events=events,OutputFrames=resumed.Frames,Progress=progress};
    }
}
