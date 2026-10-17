using System.Diagnostics;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class QueueChecks
{
    private static void Check(CaseResult result,bool condition,string description)
    { result.Checks.Add(new{Passed=condition,Description=description});if(!condition)throw new InvalidDataException(description); }
    private static async Task Wait(Func<bool> condition,string description,int seconds=40)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try{while(!condition())await Task.Delay(50,deadline.Token);}
        catch(OperationCanceledException)when(deadline.IsCancellationRequested){throw new TimeoutException(description);}
    }
    private static void Wave(string path,int frequency,double seconds)
    {
        var frames=checked((int)Math.Round(seconds*AudioFormat.SampleRate));
        using var writer=new BinaryWriter(File.Create(path));writer.Write("RIFF"u8);writer.Write(36+frames*4);writer.Write("WAVEfmt "u8);
        writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);
        writer.Write("data"u8);writer.Write(frames*4);
        for(var i=0;i<frames;i++){var sample=(short)(7000*Math.Sin(2*Math.PI*frequency*i/48000));writer.Write(sample);writer.Write(sample);}
    }
    public static async Task RunAsync(string ffmpeg,string icecastRoot,string root,CaseResult result)
    {
        Directory.CreateDirectory(root);var music=Path.Combine(root,"music");var jingles=Path.Combine(root,"jingles");var sweepers=Path.Combine(root,"sweepers");
        foreach(var folder in new[]{music,jingles,sweepers})Directory.CreateDirectory(folder);
        Wave(Path.Combine(music,"alpha.wav"),440,6);Wave(Path.Combine(music,"beta.wav"),880,2.8);
        Wave(Path.Combine(jingles,"jingle.wav"),1320,.8);Wave(Path.Combine(sweepers,"sweeper.wav"),660,.8);
        await using var origin=new Sandbox(icecastRoot,Path.Combine(root,"origin"));await origin.StartAsync();
        var outputs=new[]{origin.Output("queue-aac"),origin.Output("queue-he",CodecProfile.HeAacV2_64),origin.Output("queue-flac",CodecProfile.OggFlac)};
        var settings=new StationSettings("queue",[],outputs,AutoStart:false);
        await using var journal=new EventJournal(Path.Combine(root,"playback.db"));await journal.InitializeDesiredStateAsync(settings);
        var kinds=StationBranding.InventoryKinds([settings.Id]);
        await using var library=new StationLibrary(journal,ffmpeg,root,new[]{settings.Id}.Concat(kinds.Keys),contentKinds:kinds);
        using var branding=new StationBranding(journal,library,[settings.Id]);using var queues=new StationQueues(journal,library,[settings.Id]);
        await branding.InitializeAsync();await library.InitializeAsync();await queues.InitializeAsync();
        await library.SetRootAsync(settings.Id,music,"music");
        await library.SetRootAsync(StationBranding.InventoryKey(settings.Id,ContentKind.Jingle),jingles,"jingles");
        await library.SetRootAsync(StationBranding.InventoryKey(settings.Id,ContentKind.Sweeper),sweepers,"sweepers");
        await Wait(()=>{library.Wake();var pool=branding.Source(settings.Id).Snapshot();return library.ReadyInventory(settings.Id).ReadyCount==2&&pool?.JingleCount==1&&pool.SweeperCount==1;},"Real library/branding validation did not finish.");
        await branding.SaveAsync(settings.Id,0,new(new(true,2),new(true,3)),"cadence");
        var ids=new List<string>();
        async Task<string> Add(string name)
        {
            var page=library.Page(settings.Id,"",0);var asset=page.Items.Single(a=>a.RelativePath==name+".wav");var id=Guid.NewGuid().ToString("N");
            await queues.EnqueueAsync(settings.Id,new(page.Root!.Revision,asset.Id,asset.EditVersion,asset.SourceStamp),(await queues.SnapshotAsync(settings.Id)).Revision,id);ids.Add(id);return id;
        }
        var first=await Add("alpha");var second=await Add("beta");var third=await Add("alpha");var fourth=await Add("beta");
        await journal.RequestRunningIntentAsync(settings.Id,true,"run");
        await using var engine=new StationEngine(ffmpeg,settings,journal,planner:queues.Planner(settings),branding:branding.Source(settings.Id));
        void Refresh(string station,bool available){if(station==settings.Id)engine.RefreshNext();}
        queues.Changed+=Refresh;
        var probes=new List<Probe>();var clocks=new Dictionary<Probe,long>();
        result.Details["scope"]="Synthetic typed queue/library/engine integration and independently decoded AAC-LC, FDK HE-AACv2 and Ogg FLAC. Live append/reorder, locked-current rejection, repeated acknowledgement and 2/3-song branding cadence. No browser/mouse, physical devices, production map, worst-case loudness or long-duration acceptance.";
        try
        {
            await engine.StartAsync();
            await Wait(()=>engine.Completion.IsCompleted||engine.Snapshot().Outputs.All(o=>o.State=="SourceConnected"),"Queue outputs did not connect.");
            if(engine.Completion.IsCompleted)await engine.Completion;
            foreach(var output in outputs){var probe=new Probe(ffmpeg,origin.Port,output.Mount);probes.Add(probe);clocks.Add(probe,Stopwatch.GetTimestamp());}
            await Wait(()=>engine.Snapshot().CurrentQueueEntryId==first,"First queued item never entered the programme.");
            var before=engine.Snapshot();var queue=await queues.SnapshotAsync(settings.Id);var rejected=false;
            try{await queues.EditAsync(settings.Id,first,queue.Revision,QueueEdit.Remove,"reject-current");}catch(InvalidOperationException){rejected=true;}
            Check(result,rejected,"The current queued occurrence cannot be removed while it is on air.");
            queue=await queues.SnapshotAsync(settings.Id);var moveRevision=queue.Revision;
            await queues.EditAsync(settings.Id,fourth,moveRevision,QueueEdit.MoveUp,"move-live");
            await queues.EditAsync(settings.Id,fourth,moveRevision,QueueEdit.MoveUp,"move-live");
            var fifth=await Add("alpha");
            var after=engine.Snapshot();
            Check(result,before.Outputs.Zip(after.Outputs).All(pair=>pair.First.EncoderPid==pair.Second.EncoderPid&&pair.First.EncoderStarts==pair.Second.EncoderStarts&&pair.First.Connections==pair.Second.Connections),
                "Live queue changes preserve every encoder identity and source session.");
            Check(result,(await queues.SnapshotAsync(settings.Id)).Items.Select(e=>e.Id).SequenceEqual(new[]{first,second,fourth,third,fifth}),
                "Live order and appended occurrence read back without a duplicate after retry.");
            await Wait(()=>engine.Completion.IsCompleted||journal.PublishedQueue(settings.Id)!.Entries.Count==0,"Five queued music items did not finish.");
            if(engine.Completion.IsCompleted)await engine.Completion;
            await Task.Delay(1500);
            var snapshot=engine.Snapshot();result.Details["station"]=snapshot;
            Check(result,snapshot.Outputs.All(o=>o.EncoderStarts==1&&o.Connections==1&&o.DroppedBlocks==0&&o.State=="SourceConnected"),
                "All three continuous codec branches retain one encoder/session with zero dropped PCM blocks.");
            var heard=probes.Select(p=>new{Audio=p.Snapshot(),Elapsed=Stopwatch.GetElapsedTime(clocks[p]).TotalSeconds}).ToArray();result.Details["decodedListeners"]=heard;
            var expected=new[]{440,880,1320,880,660,440,1320,440};
            foreach(var listener in heard)
            {
                Check(result,listener.Audio.Error is null&&listener.Audio.DecodedSeconds>=listener.Elapsed-3&&listener.Audio.LongestSilentRun<=2,
                    listener.Audio.Mount+": independent native decoding follows elapsed time without an unexplained silent interval over 40 ms.");
                Check(result,listener.Audio.FirstTones.Take(expected.Length).SequenceEqual(expected),
                    listener.Audio.Mount+": decoded tones establish the queued order and inserted jingle/sweeper sequence.");
            }
            var events=EventJournal.ReadEvents(journal.DatabasePath);var ended=events.Where(e=>e.Type=="ended"&&e.QueueEntryId is not null).ToArray();
            Check(result,ended.Select(e=>e.QueueEntryId).SequenceEqual(new[]{first,second,fourth,third,fifth}),"Each queued occurrence ends exactly once in the committed order.");
            var sinceJingle=0;var sinceSweeper=0;var jingleCount=0;var sweeperCount=0;
            foreach(var e in events.Where(e=>e.Type=="ended"))
            {
                if(e.Kind==ContentKind.Music){sinceJingle++;sinceSweeper++;}
                if(e.Kind==ContentKind.Jingle){Check(result,sinceJingle==2,"A jingle follows two completed music items, including queued music.");sinceJingle=0;jingleCount++;}
                if(e.Kind==ContentKind.Sweeper){Check(result,sinceSweeper==3,"A sweeper follows three completed music items on its independent counter.");sinceSweeper=0;sweeperCount++;}
            }
            Check(result,jingleCount>=2&&sweeperCount>=1,"Both independent branding cadences executed in the decoded programme.");
            Check(result,EventJournal.Verify(journal.DatabasePath),"Actual queue attempts retain a valid immutable journal chain.");
            result.Details["queuedOccurrences"]=ended;result.Details["brandingCounts"]=new{jingleCount,sweeperCount};
        }
        finally
        {
            queues.Changed-=Refresh;
            await Task.WhenAll(probes.Select(p=>p.DisposeAsync().AsTask()));
            await journal.RequestRunningIntentAsync(settings.Id,false,"stop");await engine.DisposeAsync();
        }
    }
}
