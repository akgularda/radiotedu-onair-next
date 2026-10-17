using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

// Fault injection belongs to the disposable laboratory only. In particular the
// writer stall below never opens an operator database or runs on the broadcast PC.
internal static class QueueDurableChecks
{
    private const string Station="queue-durable";
    private static void Check(CaseResult result,bool condition,string description)
    { result.Checks.Add(new{Passed=condition,Description=description});if(!condition)throw new InvalidDataException(description); }

    private static async Task Wait(Func<Task<bool>> condition,string description,int seconds=40)
    {
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed<TimeSpan.FromSeconds(seconds))
        { if(await condition())return;await Task.Delay(75); }
        throw new TimeoutException(description);
    }

    private static void Wave(string path,int opening,int tail,double seconds)
    {
        var frames=checked((int)(seconds*AudioFormat.SampleRate));
        using var writer=new BinaryWriter(File.Create(path));writer.Write("RIFF"u8);writer.Write(36+frames*4);writer.Write("WAVEfmt "u8);
        writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);
        writer.Write("data"u8);writer.Write(frames*4);
        for(var i=0;i<frames;i++)
        { var hz=i<8*AudioFormat.SampleRate?opening:tail;var sample=(short)(7000*Math.Sin(2*Math.PI*hz*i/48000));writer.Write(sample);writer.Write(sample); }
    }

    private static SqliteConnection Reader(string database)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        connection.Open();return connection;
    }
    private static PlayoutCheckpoint? Checkpoint(string database)
    {
        using var connection=Reader(database);using var query=connection.CreateCommand();
        query.CommandText="SELECT payload FROM playout_checkpoints WHERE station_id=$station";query.Parameters.AddWithValue("$station",Station);
        return query.ExecuteScalar() is string value?JsonSerializer.Deserialize<PlayoutCheckpoint>(value):null;
    }
    private static BrandingCadence Cadence(string database)
    {
        using var connection=Reader(database);using var query=connection.CreateCommand();
        query.CommandText="SELECT music_since_jingle,music_since_sweeper,completed_jingles,completed_sweepers FROM station_cadence WHERE station_id=$station";
        query.Parameters.AddWithValue("$station",Station);using var reader=query.ExecuteReader();
        return reader.Read()?new(Station,reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3)):new(Station,0,0);
    }

    public static async Task RunAsync(string ffmpeg,string icecastRoot,string sdk,string assembly,string root,CaseResult result)
    {
        if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true")
            throw new InvalidOperationException("Hard-kill and SQLite writer-stall qualification runs only on a disposable CI runner.");
        Directory.CreateDirectory(root);var music=Path.Combine(root,"music");var jingles=Path.Combine(root,"jingles");var sweepers=Path.Combine(root,"sweepers");
        foreach(var directory in new[]{music,jingles,sweepers})Directory.CreateDirectory(directory);
        Wave(Path.Combine(music,"alpha.wav"),440,880,24);Wave(Path.Combine(music,"beta.wav"),660,660,14);Wave(Path.Combine(music,"gamma.wav"),880,880,10);
        Wave(Path.Combine(jingles,"jingle.wav"),1320,1320,.8);Wave(Path.Combine(sweepers,"sweeper.wav"),440,440,.8);
        await using var origin=new Sandbox(icecastRoot,Path.Combine(root,"origin"));await origin.StartAsync();
        var outputs=new[]{origin.Output("queue-durable-aac"),origin.Output("queue-durable-he",CodecProfile.HeAacV2_64),origin.Output("queue-durable-flac",CodecProfile.OggFlac)};
        var pipe="onair-next-dev-"+Guid.NewGuid().ToString("N");var configuration=Path.Combine(root,"local.host.json");var database=Path.Combine(root,"playback.db");
        await File.WriteAllTextAsync(configuration,JsonSerializer.Serialize(new{Ffmpeg=ffmpeg,DataRoot=root,PipeName=pipe,
            Stations=new[]{new StationSettings(Station,[],outputs,AutoStart:false)}}));
        ControlHost? host=null;var generation=0;var probes=new List<Probe>();var observations=new List<object>();
        result.Details["scope"]="Disposable CI only: real host hard kills, typed IPC library/queue commands, independently decoded AAC-LC/FDK HE-AACv2/Ogg FLAC openings, a SQLite writer lock spanning decoded EOF, durable Skip retry, consumed-item retention, cadence and explicit Stop. Intentional crash/source gaps are expected; this is not continuous delivery across process death, actual-PC mouse, physical devices or 24/72-hour qualification.";

        async Task Launch()
        { host=new(sdk,assembly,configuration,pipe,Path.Combine(root,"host-"+ ++generation));await host.WaitForStationAsync(Station,generation==1?"Stopped":"Running"); }
        async Task<T> Read<T>(string action,string property,string inventory=Station)
        {
            using var response=await host!.CommandAsync(action,inventory);
            return response.RootElement.GetProperty(property).Deserialize<T>()??throw new InvalidDataException("Missing "+property);
        }
        async Task<StationSnapshot> Status()
        {
            using var response=await host!.CommandAsync("status");
            return response.RootElement.GetProperty("stations").EnumerateArray().Single(x=>x.GetProperty("Id").GetString()==Station).Deserialize<StationSnapshot>()!;
        }
        async Task Accepted(object request,string description)
        { using var response=await host!.RequestAsync(request);Check(result,response.RootElement.GetProperty("accepted").GetBoolean(),description); }
        object Command(string action,string id)=>new{Id=id,Action=action,StationId=Station};
        async Task StopProbes()
        { await Task.WhenAll(probes.Select(p=>p.DisposeAsync().AsTask()));probes.Clear(); }
        void OpenProbes()
        { foreach(var output in outputs)probes.Add(new Probe(ffmpeg,origin.Port,output.Mount)); }
        async Task Opening(int frequency,string phase)
        {
            await Wait(async()=>{var s=await Status();return s.Outputs.Count==3&&s.Outputs.All(o=>o.State=="SourceConnected");},phase+": codec sources did not connect.",15);
            OpenProbes();
            await Wait(()=>
            {
                var values=probes.Select(p=>p.Snapshot()).ToArray();
                if(values.Any(v=>v.Error is not null))throw new IOException(phase+": listener failed: "+string.Join("; ",values.Select(v=>v.Error)));
                return Task.FromResult(values.All(v=>v.DecodedSeconds>=1.2));
            },phase+": all three listener openings did not decode.",15);
            var heard=probes.Select(p=>p.Snapshot()).ToArray();observations.Add(new{phase,heard});
            foreach(var audio in heard)
                Check(result,audio.FirstTones.FirstOrDefault()==frequency&&audio.LongestSilentRun<=2,
                    phase+" "+audio.Mount+": independently decoded opening starts at "+frequency+" Hz without a silent run over40ms.");
        }
        async Task Crash(string phase)
        {
            var pid=host!.Process.Id;await host.DisposeAsync();host=null;
            Check(result,!ChildProcess.ActiveProcesses.ContainsKey(pid),phase+": the hard-killed owned host has confirmed cleanup.");
        }
        PlaybackEvent[] Events()=>EventJournal.ReadEvents(database).ToArray();
        static bool Linked(PlaybackEvent[] events,string queue,int count)
        {
            var starts=events.Where(e=>e.QueueEntryId==queue&&e.Type=="started").ToArray();
            return starts.Length==count&&starts.Skip(1).Select((e,i)=>e.PreviousOccurrenceId==starts[i].OccurrenceId&&e.OccurrenceId!=starts[i].OccurrenceId).All(x=>x);
        }
        try
        {
            await Launch();
            foreach(var assignment in new[]{(Station,music),(StationBranding.InventoryKey(Station,ContentKind.Jingle),jingles),(StationBranding.InventoryKey(Station,ContentKind.Sweeper),sweepers)})
                await Accepted(new{Id=Guid.NewGuid().ToString("N"),Action="library-root",StationId=assignment.Item1,Path=assignment.Item2},"The host saved the isolated "+assignment.Item1+" folder.");
            await Wait(async()=>
            {
                var page=await Read<LibraryPage>("library-status","page");
                var j=await Read<LibraryPage>("library-status","page",StationBranding.InventoryKey(Station,ContentKind.Jingle));
                var s=await Read<LibraryPage>("library-status","page",StationBranding.InventoryKey(Station,ContentKind.Sweeper));
                return page.Ready==3&&j.Ready==1&&s.Ready==1;
            },"The actual host did not validate the fixture folders.");
            await Accepted(new{Id="cadence",Action="branding-save",StationId=Station,Revision=0,BrandingRules=new BrandingRules(new(true,2),new(true,3))},"Independent2/3-song branding was saved through the host.");
            var page=await Read<LibraryPage>("library-status","page");
            var ids=new List<string>();object? firstAppend=null;
            foreach(var name in new[]{"alpha.wav","beta.wav","gamma.wav"})
            {
                var asset=page.Items.Single(x=>x.RelativePath==name);var queue=await Read<StationQueueView>("queue-status","queue");var id=Guid.NewGuid().ToString("N");ids.Add(id);
                var request=new{Id=id,Action="queue-append",StationId=Station,QueueRevision=queue.Revision,
                    QueueSource=new QueueLibraryReference(page.Root!.Revision,asset.Id,asset.EditVersion,asset.SourceStamp)};
                firstAppend??=request;await Accepted(request,"A validated queue occurrence was saved for "+name);
            }
            var alpha=ids[0];var beta=ids[1];var gamma=ids[2];
            await Accepted(Command("start-station","run"),"Run intent was durably accepted.");await host!.WaitForStationAsync(Station,"Running");
            await Wait(async()=>{var s=await Status();return s.CurrentQueueEntryId==alpha&&s.CurrentItemSamples>=9*AudioFormat.SampleRate;},"Alpha never passed its distinctive eight-second opening before crash.");
            Check(result,Cadence(database).MusicSinceJingle==0&&Cadence(database).MusicSinceSweeper==0,"An unfinished queued song has not advanced either completed-song counter.");
            await Crash("unfinished Alpha");await Launch();await Opening(440,"restarted Alpha");
            Check(result,Linked(Events(),alpha,2),"The restarted Alpha is a distinct attempt linked to its interrupted predecessor.");
            Check(result,(await Read<StationQueueView>("queue-status","queue")).Items.Select(x=>x.Id).SequenceEqual(ids),"A hard kill preserves every queue occurrence and its order.");
            await StopProbes();
            const string skip="skip-before-crash";
            await Accepted(Command("skip",skip),"Skip is committed before the second hard kill.");
            await Crash("accepted Skip");await Launch();await Opening(660,"after recovered Skip");
            await Accepted(Command("skip",skip),"The same Skip request can be retried after the lost acknowledgement/restart.");
            await Accepted(firstAppend!,"The original append receipt remains retryable after its occurrence was consumed.");
            var afterSkip=Events();
            // Skip admission and the kill are deliberately not a scheduler lock.
            // The next item may have published real samples before the process
            // dies. Retain/count that genuine attempt instead of assuming it away.
            var betaBeforeStall=afterSkip.Count(e=>e.QueueEntryId==beta&&e.Type=="started");
            Check(result,betaBeforeStall>=1&&Linked(afterSkip,beta,betaBeforeStall),"Every Beta attempt actually committed around the Skip/kill race has correct predecessor ownership.");
            result.Details["betaAttemptsBeforeWriterStall"]=betaBeforeStall;
            Check(result,afterSkip.Count(e=>e.QueueEntryId==alpha&&e.Type=="skipped")==1&&Linked(afterSkip,alpha,2),"Recovered/retried Skip consumes only Alpha once and never restarts it again.");
            Check(result,(await Read<StationQueueView>("queue-status","queue")).Items.Select(x=>x.Id).SequenceEqual(new[]{beta,gamma}),"Append/Skip retries do not add a duplicate or consume Beta.");
            await Wait(async()=>{var s=await Status();return s.CurrentQueueEntryId==beta&&s.CurrentItemSamples>=2*AudioFormat.SampleRate;},"Beta was not on air before the writer stall.");

            // No journal row is edited: hold SQLite's writer reservation while
            // genuine programme samples cross Beta's EOF and start Gamma. Then
            // kill before the terminal/checkpoint transaction can commit.
            using(var stall=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString()))
            {
                stall.Open();using var hold=stall.CreateCommand();hold.CommandTimeout=3;hold.CommandText="BEGIN IMMEDIATE";hold.ExecuteNonQuery();
                try
                {
                    var saved=Checkpoint(database);
                    Check(result,saved is{QueueEntryId:not null,Announced:true}&&saved.QueueEntryId==beta,"The writer stall starts from a committed Beta ownership checkpoint.");
                    await Wait(()=>Task.FromResult(probes.All(p=>p.Snapshot().FirstTones.Contains(880))),"Audio did not cross EOF while the journal writer was intentionally held.",25);
                    var heard=probes.Select(p=>p.Snapshot()).ToArray();observations.Add(new{phase="uncommitted EOF",heard});
                    foreach(var audio in heard)
                        Check(result,audio.Error is null&&audio.FirstTones.Take(2).SequenceEqual(new[]{660,880})&&audio.LongestSilentRun<=2,
                            audio.Mount+": decoded Beta-to-Gamma samples continue despite the held writer, with no silent run over40ms.");
                    Check(result,!Events().Any(e=>e.QueueEntryId==beta&&e.Type=="ended")&&!Events().Any(e=>e.QueueEntryId==gamma&&e.Type=="started"),
                        "The held transaction has not falsely published Beta completion or Gamma start.");
                    await Crash("uncommitted EOF");
                }
                finally{hold.CommandText="ROLLBACK";hold.ExecuteNonQuery();}
            }
            await StopProbes();await Launch();await Opening(660,"restarted uncommitted Beta");
            Check(result,Linked(Events(),beta,betaBeforeStall+1),"Uncommitted EOF recovers Beta from its beginning as exactly one linked new attempt.");
            Check(result,Cadence(database).MusicSinceJingle==0&&Cadence(database).MusicSinceSweeper==0,"Uncommitted EOF and skipped Alpha do not manufacture completed-song cadence.");
            await StopProbes();
            await Wait(async()=>{var s=await Status();return s.CurrentQueueEntryId==gamma&&s.CurrentItemSamples>=AudioFormat.SampleRate;},"Committed Beta did not advance to Gamma.");
            var committed=Events();
            Check(result,committed.Count(e=>e.QueueEntryId==beta&&e.Type=="ended")==1,"The recovered Beta completes exactly once in the journal.");
            Check(result,(await Read<StationQueueView>("queue-status","queue")).Items.Select(x=>x.Id).SequenceEqual(new[]{gamma}),"Committed Beta is removed from the active queue atomically with its terminal.");
            await Crash("after committed Beta");await Launch();await Opening(880,"restarted Gamma");
            Check(result,Linked(Events(),beta,betaBeforeStall+1)&&Linked(Events(),gamma,2),"A consumed Beta stays consumed; only unfinished Gamma gets a linked restart.");
            var beforeCadence=Cadence(database);
            Check(result,beforeCadence.MusicSinceJingle==1&&beforeCadence.MusicSinceSweeper==1,"The one committed music completion survives another host crash on both cadence counters.");
            await Wait(()=>Task.FromResult(Events().Any(e=>e.Kind==ContentKind.Jingle&&e.Type=="ended")),"The preserved two-song jingle cadence did not run after Gamma.");
            await Wait(()=>Task.FromResult(probes.All(p=>p.Snapshot().FirstTones.Contains(1320))),"The post-recovery jingle did not reach all three decoded outputs.",12);
            observations.Add(new{phase="durable cadence",heard=probes.Select(p=>p.Snapshot()).ToArray()});
            await Accepted(Command("stop-station","explicit-stop"),"Explicit Stop intent is committed after the recovered programme.");await host!.WaitForStationAsync(Station,"Stopped");
            await StopProbes();
            var cadence=Cadence(database);result.Details["cadence"]=cadence;
            Check(result,cadence.MusicSinceJingle==0&&cadence.MusicSinceSweeper==2&&cadence.CompletedJingles==1&&cadence.CompletedSweepers==0,
                "Exactly two committed queued songs produce one jingle; the independent three-song sweeper counter remains at two.");
            var final=Events();result.Details["queuedAttempts"]=final.Where(e=>e.QueueEntryId is not null).ToArray();
            Check(result,final.Count(e=>e.QueueEntryId==gamma&&e.Type=="ended")==1&&final.Count(e=>e.QueueEntryId==beta&&e.Type=="ended")==1,
                "Each completed queue entry has one terminal despite the injected crash/commit boundaries.");
            await Crash("explicitly stopped host");
            host=new(sdk,assembly,configuration,pipe,Path.Combine(root,"host-"+ ++generation));await host.WaitForStationAsync(Station,"Stopped");
            var stopped=await Status();var stoppedQueue=await Read<StationQueueView>("queue-status","queue");
            result.Details["stoppedAfterRestart"]=stopped;result.Details["queueAfterStoppedRestart"]=stoppedQueue;
            Check(result,!stopped.RequestedRunning&&stopped.State=="Stopped"&&stopped.Outputs.Count==outputs.Length&&
                stopped.Outputs.Select(o=>o.Id).SequenceEqual(outputs.Select(o=>o.Id))&&
                stopped.Outputs.All(o=>o.State=="Stopped"&&o.EncoderPid is null&&o.EncoderStarts==0&&o.Connections==0&&o.FramesWritten==0&&o.QueuedBlocks==0)&&
                stoppedQueue.Items.Count==0,
                "Explicit Stop and the consumed queue survive host restart without opening any codec/source branch.");
            var mountChecks=new List<object>();result.Details["stoppedMountChecks"]=mountChecks;
            foreach(var output in outputs)
            {
                // Isolate each observation from stale keep-alive/proxy state.
                // A transport failure remains a failure, never proof of absence.
                using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)};
                using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(3)};
                using var request=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{origin.Port}{output.Mount}")
                    {Version=System.Net.HttpVersion.Version10,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
                request.Headers.ConnectionClose=true;
                try
                {
                    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
                    mountChecks.Add(new{output.Mount,Status=(int)response.StatusCode,Version=response.Version.ToString()});
                    Check(result,response.StatusCode==System.Net.HttpStatusCode.NotFound,output.Mount+": the isolated listener endpoint confirms there is no automatically restarted source.");
                }
                catch(HttpRequestException error)
                {mountChecks.Add(new{output.Mount,Error=error.GetType().Name,InnerError=error.InnerException?.GetType().Name});throw;}
            }
            Check(result,Events().Count(e=>e.Type=="started")==final.Count(e=>e.Type=="started"),"Stopped recovery manufactures no additional audio attempt.");
            Check(result,EventJournal.Verify(database),"All crash/restart/Skip/cadence attempts retain a valid immutable journal chain.");
            result.Details["hostGenerations"]=generation;
        }
        finally
        {
            result.Details["decodedPhases"]=observations;
            await StopProbes();if(host is not null)await host.DisposeAsync();
        }
    }
}
