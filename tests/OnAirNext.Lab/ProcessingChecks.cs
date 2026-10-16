using System.Runtime.InteropServices;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class ProcessingChecks
{
    private static void Check(CaseResult result,bool ok,string text){result.Checks.Add(new{Passed=ok,Description=text});if(!ok)throw new InvalidDataException(text);}
    public static async Task RunAsync(string ffmpeg,string root,CaseResult result)
    {
        Directory.CreateDirectory(root);
        var a=Fixture(root,"a",1200,0.03f);var b=Fixture(root,"b",480,0.03f,ContentKind.Advertisement);var c=Fixture(root,"c",480,0.03f,ContentKind.Sweeper);
        await FiniteAsync(ffmpeg,root,result,[a with{DurationSeconds=.01},b,c]);
        await PreparedQueueAsync(ffmpeg,root,result,[a,b,c]);
        await StopRecoveryAsync(ffmpeg,root,result,a,b);
        await BufferedSkipAsync(ffmpeg,root,result,a,b);
        await SlowAuditAsync(ffmpeg,root,result,Fixture(root,"long",96000,0.03f));
        await MixedPeakAsync(ffmpeg,root,result,Fixture(root,"mix",192000,0.8f));
        Check(result,ChildProcess.ActiveProcesses.Count==0,"Processing qualification closes every owned decoder/validation worker.");
        result.Details["scope"]="Synthetic final-mix limiting, prepared/audible EOF and Stop/Skip/recovery ownership, and bounded asynchronous audit only. No physical mic/cue/recording, encoded worst-case headroom, HE-AAC/all18 or production acceptance.";
    }
    private static async Task FiniteAsync(string ffmpeg,string root,CaseResult result,MediaItem[] items)
    {
        // First clip is physically 1,200 frames; its displayed duration deliberately disagrees. Decoder EOF is authoritative.
        await using var journal=new EventJournal(Path.Combine(root,"finite.db"));var sink=new Capture(journal.DatabasePath);
        await using var engine=new StationEngine(ffmpeg,new("processing-finite",items,[],Repeat:false),journal,[sink]);
        await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        var events=EventJournal.ReadEvents(journal.DatabasePath);var state=engine.Snapshot();
        Check(result,sink.EventsAtStart==0,"Preroll may prepare an entire short programme without committing its start or EOF.");
        Check(result,sink.Frames==2160&&state.Samples==2160&&state.CompletedItems==3,"The finite processed programme preserves all 2,160 real frames without delay padding.");
        Check(result,!sink.FutureTerminalObserved,"No completed event precedes its actual output frame boundary.");
        Check(result,events.Where(x=>x.Type=="started").Select(x=>x.SamplePosition).SequenceEqual(new long[]{0,1200,1680})&&events.Where(x=>x.Type=="ended").Select(x=>x.SamplePosition).SequenceEqual(new long[]{1200,1680,2160}),"Prepared short music/ad/sweeper boundaries retain their exact output sample positions.");
        Check(result,EventJournal.Verify(journal.DatabasePath),"Processed finite events preserve the immutable journal chain.");
        result.Details["finite"]=new{State=state,Events=events,OutputFrames=sink.Frames};
    }

    private static async Task PreparedQueueAsync(string ffmpeg,string root,CaseResult result,MediaItem[] items)
    {
        await using var journal=new EventJournal(Path.Combine(root,"queue.db"));
        await journal.TransactionAsync((connection,transaction)=>{using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="CREATE TABLE studio_queue(id TEXT PRIMARY KEY,state TEXT NOT NULL);INSERT INTO studio_queue VALUES('q0','pending'),('q1','pending'),('q2','pending');";command.ExecuteNonQuery();return true;});
        var planner=new QueueFixture(items);var sink=new Capture(journal.DatabasePath);
        await using var engine=new StationEngine(ffmpeg,new("processing-queue",[],[]),journal,[sink],planner);
        await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        Check(result,planner.MaximumExclusions>=3&&events.Where(x=>x.Type=="started").Select(x=>x.QueueEntryId).SequenceEqual(new[]{"q0","q1","q2"}),"Three in-flight queued sources are selected once even before any prior completion can commit.");
        var completed=await journal.TransactionAsync((connection,transaction)=>{using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT COUNT(*) FROM studio_queue WHERE state='completed'";return Convert.ToInt32(command.ExecuteScalar());});
        Check(result,completed==3&&sink.EventsAtStart==0&&!sink.FutureTerminalObserved,"Queue consumption follows published terminal boundaries and their durable transactions.");
        result.Details["preparedQueue"]=new{MaximumExclusions=planner.MaximumExclusions,CompletedRows=completed,Events=events};
    }
    private sealed class QueueFixture(MediaItem[] items):IPlayoutPlanner
    {
        public int? AvailableItems=>items.Length;public int MaximumExclusions{get;private set;}
        public PlayoutSelection Select(int rotationIndex,string? excludeQueueEntryId=null)
            =>SelectPrepared(rotationIndex,excludeQueueEntryId is null?new HashSet<string>():new HashSet<string>{excludeQueueEntryId})??throw new InvalidOperationException("No fixture source remains.");
        public PlayoutSelection? SelectPrepared(int rotationIndex,IReadOnlySet<string> excludedQueueEntries)
        {
            MaximumExclusions=Math.Max(MaximumExclusions,excludedQueueEntries.Count);
            for(var i=0;i<items.Length;i++)if(!excludedQueueEntries.Contains("q"+i))return new(items[i],"q"+i);
            return null;
        }
    }

    private static async Task StopRecoveryAsync(string ffmpeg,string root,CaseResult result,MediaItem a,MediaItem b)
    {
        await using var journal=new EventJournal(Path.Combine(root,"stop.db"));StationEngine? running=null;Task? stopped=null;
        var first=new Capture(journal.DatabasePath);first.AfterFirst=()=>stopped=running!.DisposeAsync().AsTask();
        running=new StationEngine(ffmpeg,new("processing-stop",[a,b],[],Repeat:false),journal,[first]);
        await running.StartAsync();await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));if(stopped is not null)await stopped;
        var before=EventJournal.ReadEvents(journal.DatabasePath);var checkpoint=await journal.CheckpointAsync("processing-stop");
        Check(result,first.Frames==960&&before.Count(x=>x.Type=="started")==1&&before.Single(x=>x.Type=="started").ItemId==a.Id,"Stop after the first output block never announces the already prepared next source.");
        Check(result,!before.Any(x=>x.Type=="ended")&&checkpoint is{State:"Stopped",RotationIndex:0,ItemSamples:960,ProgramSamples:960},"Stop preserves the audible unfinished source, not a future prepared terminal/checkpoint.");
        var next=new Capture(journal.DatabasePath);await using(var restarted=new StationEngine(ffmpeg,new("processing-stop",[a,b],[],Repeat:false),journal,[next]))
        {await restarted.StartAsync();await restarted.Completion.WaitAsync(TimeSpan.FromSeconds(15));Check(result,restarted.Snapshot().Samples==2640,"Restart adds the entire unfinished source and successor to the committed output clock.");}
        var events=EventJournal.ReadEvents(journal.DatabasePath);var starts=events.Where(x=>x.Type=="started"&&x.ItemId==a.Id).ToArray();
        Check(result,starts.Length==2&&starts[1].PreviousOccurrenceId==starts[0].OccurrenceId&&Math.Abs(next.FirstLeft-FirstSample(0.03f))<1e-8,"Recovery links a new attempt and restarts the unfinished source from its original first sample.");
        Check(result,events.Count(x=>x.Type=="ended"&&x.ItemId==b.Id)==1,"A prepared but unheard successor is consumed only when actually published after restart.");
        result.Details["stopRecovery"]=new{Checkpoint=checkpoint,Events=events};
    }
    private static async Task BufferedSkipAsync(string ffmpeg,string root,CaseResult result,MediaItem a,MediaItem b)
    {
        await using var journal=new EventJournal(Path.Combine(root,"skip.db"));StationEngine? running=null;Task? skip=null;
        var sink=new Capture(journal.DatabasePath);sink.AfterFirst=()=>skip=running!.SkipAsync("buffered-skip");
        await using(var engine=new StationEngine(ffmpeg,new("processing-skip",[a,b],[],Repeat:false),journal,[sink]))
        {running=engine;await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(15));if(skip is not null)await skip;}
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        Check(result,events.Count(x=>x.Type=="ended"&&x.ItemId==b.Id)==1&&!events.Any(x=>x.Type=="skipped"&&x.ItemId==b.Id),"Skip observed in an already buffered EOF window never skips the unheard next item.");
        Check(result,!await journal.HasPendingSkipAsync("processing-skip"),"A late accepted Skip is consumed by the finished occurrence instead of surviving into another item.");
        Check(result,EventJournal.Verify(journal.DatabasePath),"Buffered Skip preserves immutable exact-boundary history.");
        result.Details["bufferedSkip"]=events;
    }
    private static async Task SlowAuditAsync(string ffmpeg,string root,CaseResult result,MediaItem item)
    {
        await using var journal=new EventJournal(Path.Combine(root,"audit.db"));var sink=new Capture(journal.DatabasePath,false);
        await using var engine=new StationEngine(ffmpeg,new("processing-audit",[item],[]),journal,[sink]);
        await engine.StartAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while(sink.Frames<4800)await Task.Delay(10,deadline.Token);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=journal.TransactionAsync((_,_)=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException("Injected audit hold exceeded its fixture deadline.");return true;});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));var before=sink.Frames;await Task.Delay(350,deadline.Token);var after=sink.Frames;
            Check(result,after-before>=12000,"Programme audio keeps advancing through a 350 ms journal-writer stall, including the checkpoint interval.");
            result.Details["auditStall"]=new{BeforeFrames=before,AfterFrames=after,ElapsedMilliseconds=350};
        }
        finally{release.Set();await held;}
        await engine.DisposeAsync();Check(result,engine.Snapshot().AuditRejectedEvents==0&&EventJournal.Verify(journal.DatabasePath),"The bounded audit backlog drains durably after storage returns.");
    }
    private static async Task MixedPeakAsync(string ffmpeg,string root,CaseResult result,MediaItem item)
    {
        await using var journal=new EventJournal(Path.Combine(root,"mix.db"));var sink=new Capture(journal.DatabasePath,false);
        await using var engine=new StationEngine(ffmpeg,new("processing-mix",[item],[],Repeat:false),journal,[sink]);engine.Live.SetEnabled(true);
        using var stop=new CancellationTokenSource();var feeding=Task.Run(async()=>
        {try{while(!stop.IsCancellationRequested){var block=PcmBlock.Rent();MemoryMarshal.Cast<byte,float>(block.Memory.Span).Fill(.9f);engine.Live.Push(block);await Task.Delay(20,stop.Token);}}catch(OperationCanceledException)when(stop.IsCancellationRequested){}});
        try{await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(15));}
        finally{await stop.CancelAsync();await feeding;}
        var meter=new StereoLoudnessMeter48k(TimeSpan.FromSeconds(5));meter.Feed(sink.Samples.ToArray());meter.Finish();var reading=meter.Read();var state=engine.Snapshot();
        Check(result,sink.Frames==192000&&state.Samples==192000,"Synthetic mic/music mix retains exact finite programme length.");
        Check(result,reading.TruePeakDbtp<=-1.499&&state.Limiter?.LimitedFrames>0&&state.Limiter.MaximumGainReductionDb>1,"The final mixed signal is genuinely oversampled and limited after microphone ducking/mixing.");
        result.Details["mixedPeak"]=new{Reading=reading,State=state};
    }
    private sealed class Capture(string database,bool inspectHistory=true):IProgramOutput
    {
        private long _frames;public long Frames=>Interlocked.Read(ref _frames);public List<float> Samples{get;}=new();
        public long EventsAtStart{get;private set;}public bool FutureTerminalObserved{get;private set;}public float FirstLeft=>Samples[0];public Action? AfterFirst{get;set;}
        public void Start(){EventsAtStart=EventJournal.ReadEvents(database).Count;}
        public bool Publish(PcmBlock block)
        {
            var first=Frames==0;Samples.AddRange(MemoryMarshal.Cast<byte,float>(block.Memory.Span).ToArray());Interlocked.Add(ref _frames,block.Length/AudioFormat.BytesPerSampleFrame);
            if(inspectHistory&&EventJournal.ReadEvents(database).Any(x=>(x.Type is "ended" or "skipped")&&x.SamplePosition>Frames))FutureTerminalObserved=true;
            if(first)AfterFirst?.Invoke();return true;
        }
        public OutputSnapshot Snapshot()=>new("processing-sink","Ready",0,0,Frames,0,0,null,null);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static float FirstSample(float amplitude)=>(float)(amplitude*Math.Sin(.31));
    private static MediaItem Fixture(string root,string name,int frames,float amplitude,ContentKind kind=ContentKind.Music)
    {
        var path=Path.Combine(root,name+".wav");using var writer=new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);writer.Write(36+frames*8);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)3);writer.Write((short)2);writer.Write(48000);writer.Write(384000);writer.Write((short)8);writer.Write((short)32);writer.Write("data"u8);writer.Write(frames*8);
        for(var i=0;i<frames;i++){var sample=(float)(amplitude*Math.Sin(2*Math.PI*440*i/48000+.31));writer.Write(sample);writer.Write(sample*.75f);}
        return new(name,path,"Synthetic engineering signal",name,kind,(double)frames/48000);
    }
}
