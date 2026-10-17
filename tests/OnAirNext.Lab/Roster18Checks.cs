using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class Roster18Checks
{
    private static void Check(CaseResult result, bool passed, string description)
    {
        result.Checks.Add(new { Passed=passed, Description=description });
        if (!passed) throw new InvalidDataException(description);
    }

    public static async Task RunAsync(string ffmpeg, string icecastRoot, string root, CaseResult result)
    {
        Directory.CreateDirectory(root);
        var sources = new List<MediaItem>();
        await using (var analyzer = new LibraryAudioProbe(ffmpeg))
        {
            foreach (var frequency in new[] {440,880,1320,660})
            {
                var path=Path.Combine(root,"stereo-"+frequency+".wav"); WriteFixture(path,frequency);
                var metadata=await analyzer.VerifyAsync(path,CancellationToken.None);
                sources.Add(new("stereo-"+frequency,path,"Synthetic engineering signal","Stereo "+frequency,
                    DurationSeconds:metadata.Seconds,Loudness:metadata.Loudness));
                Check(result,metadata.Loudness is {IntegratedLufs: not null, TruePeakDbtp: not null},"The complete stereo "+frequency+" source is measured before selection.");
            }
        }
        await using var origin=new Sandbox(icecastRoot,Path.Combine(root,"origin")); await origin.StartAsync();
        var database=Path.Combine(root,"playback.db");
        var probes=new List<Probe>(); var probeStarts=new Dictionary<Probe,long>(); var engines=new List<StationEngine>(); var outputs=new List<OutputSettings>();
        var readings=new List<object>(); result.Details["encodedListeners"]=readings;
        result.Details["scope"]="Eight synthetic station programs: eight AAC-LC 192, eight libfdk HE-AACv2 64 and two 24-bit Ogg FLAC branches, all 48 kHz stereo. A concurrent annual export includes 10025 synthetic archived attempts seeded before audio. A concurrent metadata-only music assignment exercises the shared journal; its legacy source inspection and UI are separate checks. Low-crest sources only. This is not an imported production rate/name map, worst-case encoded headroom, actual-PC resource, 24/72h or UI acceptance.";
        await using (var journal=new EventJournal(database))
        {
            await SeedReportHistoryAsync(journal);
            await journal.InitializeLibraryAsync();
            await journal.InitializeDesiredStateAsync(new("migration-prep", [], [], AutoStart: false));
            await using var resources=new Resources();
            await using var reports=new ReportExports(journal,Enumerable.Range(1,8).Select(index=>"roster-"+index));
            await reports.InitializeAsync();
            var observations=new List<object>();result.Details["fleetObservations"]=observations;
            using var observeStop=new CancellationTokenSource();
            Task observeTask=Task.CompletedTask;
            string? reportId=null;
            Exception? runFailure=null;var cleanupFailures=new List<Exception>();
            try
            {
                for(var index=1; index<=8; index++)
                {
                    var group=new List<OutputSettings>{origin.Output("roster-"+index+"-aac",CodecProfile.AacLc192),origin.Output("roster-"+index+"-he",CodecProfile.HeAacV2_64)};
                    if(index>=7)group.Add(origin.Output("roster-"+index+"-flac",CodecProfile.OggFlac));
                    outputs.AddRange(group);
                    engines.Add(new StationEngine(ffmpeg,new("roster-"+index,sources,group),journal));
                }
                observeTask=ObserveFleetAsync(engines,journal,observations,observeStop.Token);
                foreach(var engine in engines)await engine.StartAsync();
                using(var ready=new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                    while(engines.Any(e=>e.Snapshot().Outputs.Any(o=>o.State!="SourceConnected")))await Task.Delay(100,ready.Token);
                Check(result,outputs.Count==18&&outputs.Count(o=>o.Profile==CodecProfile.HeAacV2_64)==8,"All 18 actual codec branches are concurrently source-connected.");
                foreach(var output in outputs)
                {
                    var started=Stopwatch.GetTimestamp();var listener=new Probe(ffmpeg,origin.Port,output.Mount);
                    probes.Add(listener);probeStarts.Add(listener,started);
                }
                reportId=Guid.NewGuid().ToString("N");
                await reports.StartAsync(reportId,"year",TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,PlayReports.Istanbul).Year.ToString("D4",CultureInfo.InvariantCulture));
                await Task.Delay(6000);
                var beforeMusic=engines.Select(engine=>engine.Snapshot()).ToArray();
                var musicProof=new LegacyMusicProof(Guid.NewGuid().ToString("N"),1,99,"Synthetic preparation only","migration-prep",
                    Path.Combine(root,"old-preparation-source"),Path.Combine(root,"prepared-music"),0,new string('a',64),
                    new("synthetic-recovery.db.dpapi",new string('b',64),50,"2026-10-16T08:00:00Z"),DateTimeOffset.UtcNow);
                var musicCommand=Guid.NewGuid().ToString("N");
                Check(result,await journal.SetLegacyMusicRootAsync("migration-prep",musicProof.SelectedMusicFolder,musicCommand,musicProof,()=>{}),
                    "An unarmed metadata-only music assignment commits while all eighteen codec branches are active.");
                var musicPage=journal.LibraryPage("migration-prep");var musicReceipt=journal.LegacyMusicAssignment(musicCommand)!;
                result.Details["concurrentMusicAssignment"]=new{Receipt=musicReceipt,Scope="Atomic journal assignment under full-roster audio; not legacy-database inspection, UI acceptance or a real station migration."};
                Check(result,musicPage.LegacyMusicAssignment==musicReceipt&&musicPage.Root?.Path==musicProof.SelectedMusicFolder,
                    "Concurrent music assignment reads back the same root and immutable receipt.");
                Check(result,!await journal.SetLegacyMusicRootAsync("migration-prep",musicProof.SelectedMusicFolder,musicCommand,musicReceipt.Proof,()=>throw new InvalidOperationException("A completed assignment must not reauthorize."))&&journal.LibraryPage("migration-prep").Root!.Revision==1,
                    "A repeated metadata assignment does not create another library revision under full-roster load.");
                var afterMusic=engines.Select(engine=>engine.Snapshot()).ToArray();
                Check(result,beforeMusic.Zip(afterMusic).All(pair=>pair.Second.Samples>=pair.First.Samples&&pair.First.Outputs.Zip(pair.Second.Outputs).All(output=>
                    output.First.EncoderPid==output.Second.EncoderPid&&output.First.EncoderStarts==output.Second.EncoderStarts&&output.First.Connections==output.Second.Connections)),
                    "Music preparation leaves every active encoder identity, source connection and programme timeline in place.");
                foreach(var output in outputs)await MeasureOutputAsync(ffmpeg,origin.Port,root,output,readings,result);
                var exported=journal.Report(reportId)!;result.Details["reportExport"]=exported;
                Check(result,exported.State=="Completed","A full annual export completes while all eighteen real codec branches remain active.");
                Check(result,exported.Records>=10025&&exported.IncompleteRecords>=10025,"The export retains over ten thousand archived attempts without a UI-page row limit or invented endings.");
                var download=reports.OpenDownload(reportId);
                await using(var exportedFile=download.File)
                {
                    var hash=Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(exportedFile));
                    Check(result,hash==exported.Sha256&&exportedFile.Length==exported.Bytes,"The annual export bytes independently match its confirmed SHA-256 receipt.");
                    exportedFile.Position=0;using var csv=new StreamReader(exportedFile,System.Text.Encoding.UTF8,true,65536,true);long rows=-1;
                    while(await csv.ReadLineAsync() is not null)rows++;
                    Check(result,rows==exported.Records,"The complete synthetic CSV row count matches its persisted export receipt.");
                }
                var snapshots=engines.Select(e=>e.Snapshot()).ToArray();
                result.Details["stations"]=snapshots; result.Details["listeners"]=probes.Select(p=>new{Audio=p.Snapshot(),ElapsedSeconds=Stopwatch.GetElapsedTime(probeStarts[p]).TotalSeconds}).ToArray();
                result.Details["resources"]=resources.Snapshot();
                Check(result,snapshots.All(s=>s.State=="Running"&&s.CompletedItems>=8),"Every station has crossed at least eight real source EOF boundaries while all branches remained active.");
                Check(result,snapshots.SelectMany(s=>s.Outputs).All(o=>o.EncoderStarts==1&&o.Connections==1&&o.DroppedBlocks==0&&o.State=="SourceConnected"),"All eighteen branches retain one encoder/session with zero dropped PCM blocks.");
                foreach(var probe in probes)
                {
                    var reading=probe.Snapshot();var elapsed=Stopwatch.GetElapsedTime(probeStarts[probe]).TotalSeconds;
                    Check(result,elapsed>=30&&reading.DecodedSeconds>=elapsed-3&&reading.DecodedSeconds<=elapsed+2,
                        reading.Mount+": continuous decoded duration follows elapsed wall time within startup/burst allowance (decoded "+reading.DecodedSeconds.ToString("F2",CultureInfo.InvariantCulture)+", elapsed "+elapsed.ToString("F2",CultureInfo.InvariantCulture)+" seconds).");
                    Check(result,reading.Error is null&&reading.DecodedSeconds>=30&&reading.LongestSilentRun<=2&&reading.ToneTransitions>=Math.Max(4,Math.Floor(reading.DecodedSeconds/3)-2)&&reading.ToneTransitions<=Math.Ceiling(reading.DecodedSeconds/3)+1&&reading.UnexpectedToneTransitions==0,
                        reading.Mount+": continuous native decoding observes successive expected items without unexplained silence or repeats.");
                }
                Check(result,snapshots.All(s=>s.Loudness?.Continuous==true&&s.Loudness.State=="Measuring"&&s.Loudness.HistoryFailures==0),"Every station retains continuous final-mix measurement and confirmed measurement history under the complete codec roster.");
                result.Details["journalBeforeStop"]=new{journal.CommittedTransactions,journal.CommittedEvents,journal.PendingEvents};
                Check(result,snapshots.All(s=>s.AuditRejectedEvents==0)&&journal.Error is null,"Concurrent eight-station attempt journals remain writable without rejected events.");
            }
            catch(Exception ex){runFailure=ex;}
            finally
            {
                await observeStop.CancelAsync();await observeTask;
                if(reportId is not null)result.Details["reportExportAtCleanup"]=journal.Report(reportId) ?? (object)new { State = "Missing" };
                result.Details["journalAtCleanup"]=new{journal.CommittedTransactions,journal.CommittedEvents,journal.PendingEvents};
                result.Details["stationsAtCleanup"]=engines.Select(e=>e.Snapshot()).ToArray();
                result.Details["resourcesAtCleanup"]=resources.Snapshot();
                foreach(var probe in probes)try{await probe.DisposeAsync();}catch(Exception ex){cleanupFailures.Add(ex);}
                foreach(var engine in engines)try{await engine.DisposeAsync();}catch(Exception ex){cleanupFailures.Add(ex);}
            }
            result.Details["statesAfterCleanup"]=engines.Select(e=>e.Snapshot()).ToArray();
            if(runFailure is not null)cleanupFailures.Insert(0,runFailure);
            if(cleanupFailures.Count>0)throw new AggregateException("Full roster validation or owned cleanup failed.",cleanupFailures);
            result.Details["journalAfterStop"]=new{journal.CommittedTransactions,journal.CommittedEvents,journal.PendingEvents,Stations=engines.Select(e=>e.Snapshot()).ToArray()};
            Check(result,engines.All(e=>e.Snapshot().PendingAuditCommits==0)&&journal.PendingEvents==0,"Stopping all eight stations acknowledges every queued audit commit within the unchanged bounded drain.");
        }
        Check(result,EventJournal.Verify(database),"The full 18-branch test preserves the immutable attempt hash chain.");
        result.Details["profileCounts"]=new{AacLc192=8,HeAacV2_64=8,OggFlac24=2};
    }

    private static async Task ObserveFleetAsync(List<StationEngine> engines,EventJournal journal,List<object> readings,CancellationToken token)
    {
        var clock=Stopwatch.StartNew();
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(readings.Count==180)readings.RemoveAt(0);
                readings.Add(new{AtUtc=DateTimeOffset.UtcNow,ElapsedSeconds=clock.Elapsed.TotalSeconds,
                    ThreadPool.ThreadCount,ThreadPool.PendingWorkItemCount,ThreadPool.CompletedWorkItemCount,
                    GcPauseMilliseconds=GC.GetTotalPauseDuration().TotalMilliseconds,
                    Gen2Collections=GC.CollectionCount(2),ManagedBytes=GC.GetTotalMemory(false),
                    JournalPending=journal.PendingEvents,
                    Stations=engines.Select(engine=>{var s=engine.Snapshot();return new{s.Id,s.LateBlocks,s.CompletedItems,
                        MeterDropped=s.Loudness?.DroppedBlocks,Outputs=s.Outputs.Select(o=>new{o.Id,o.State,o.EncoderStarts,o.Connections,o.DroppedBlocks,o.QueuedBlocks}).ToArray()};}).ToArray()});
                await Task.Delay(1000,token);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
    }

    private static async Task SeedReportHistoryAsync(EventJournal journal)
    {
        var year=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,PlayReports.Istanbul).Year.ToString("D4",CultureInfo.InvariantCulture);
        var start=PlayReports.PeriodBounds("year",year).Start.AddMinutes(1);
        for(var offset=0;offset<10025;offset+=128)
            await Task.WhenAll(Enumerable.Range(offset,Math.Min(128,10025-offset)).Select(index=>journal.AppendAsync(
                new PlaybackEvent("archive-start-"+index,"archive-attempt-"+index,"archived-report-fixture","started","archive",
                    "Synthetic archived artist","Synthetic historical attempt "+index,ContentKind.Music,start.AddMilliseconds(index),0))));
    }

    private static async Task MeasureOutputAsync(string ffmpeg,int port,string root,OutputSettings output,List<object> readings,CaseResult result)
    {
        var address=$"http://127.0.0.1:{port}{output.Mount}";
        var ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe");
        var (probeText,_)=await ToolAsync(ffprobe,["-v","error","-rw_timeout","5000000","-analyzeduration","2000000","-probesize","65536",
            "-read_intervals","%+#1","-show_entries","stream=codec_name,profile,sample_rate,channels,bits_per_raw_sample,bit_rate","-of","json",address],"roster-format-probe",true);
        using var document=JsonDocument.Parse(probeText);
        var streams=document.RootElement.GetProperty("streams");
        Check(result,streams.GetArrayLength()==1,output.Id+": actual encoded stream has one audio track.");
        var stream=streams[0];
        var profile=stream.TryGetProperty("profile",out var foundProfile)?foundProfile.GetString():null;
        var codec=stream.GetProperty("codec_name").GetString();
        Check(result,stream.GetProperty("sample_rate").GetString()=="48000"&&stream.GetProperty("channels").GetInt32()==2,output.Id+": independently probed output retains 48 kHz stereo.");
        Check(result,output.Profile switch{CodecProfile.AacLc192=>codec=="aac"&&profile=="LC",CodecProfile.HeAacV2_64=>codec=="aac"&&profile=="HE-AACv2",_=>codec=="flac"&&stream.GetProperty("bits_per_raw_sample").GetString()=="24"},output.Id+": actual codec profile/FLAC depth matches the branch.");
        if(output.Profile!=CodecProfile.OggFlac)
        {
            var bitrate=int.Parse(stream.GetProperty("bit_rate").GetString()!,CultureInfo.InvariantCulture);
            var requested=output.Profile==CodecProfile.AacLc192?192000:64000;
            Check(result,bitrate>=requested*0.80&&bitrate<=requested*1.20,output.Id+": observed ADTS bitrate is within 20% of the requested rate, including framing/VBR variance.");
        }
        var pcm=Path.Combine(root,output.Id+"-decoded.f32");
        var decode=new List<string>{"-nostdin","-hide_banner","-nostats","-y"};
        if(output.Profile!=CodecProfile.OggFlac)decode.AddRange(["-c:a","aac"]);
        decode.AddRange(["-rw_timeout","5000000","-analyzeduration","200000","-probesize","32768","-i",address,"-t","4","-vn","-sn","-dn","-filter_threads","1","-af",SourceNormalization.DecodeFormat+",asetpts=N/SR/TB,atrim=end_sample=192000,ebur128=peak=true:framelog=verbose","-c:a","pcm_f32le","-f","f32le",pcm]);
        var (_,diagnostic)=await ToolAsync(ffmpeg,decode,"roster-listener-meter");
        var at=diagnostic.LastIndexOf("Summary:",StringComparison.Ordinal);
        if(at<0)throw new InvalidDataException("Final decoded meter summary missing: "+output.Id);
        var summary=diagnostic[at..];
        double Metric(string key,string unit)
        {
            var match=Regex.Match(summary,@"(?m)^\s*"+key+@":\s*(-?\d+(?:\.\d+)?)\s+"+unit+@"\s*$",RegexOptions.CultureInvariant);
            if(!match.Success)throw new InvalidDataException("Missing decoded "+key+": "+output.Id);
            return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
        }
        var integrated=Metric("I","LUFS"); var peak=Metric("Peak","dBFS");
        var bytes=await File.ReadAllBytesAsync(pcm);
        var (frames,different,finite)=Analyze(bytes);
        var measured=new{output.Id,Profile=output.Profile.ToString(),ActualStream=stream.Clone(),DecodedFrames=frames,DistinctStereoFrames=different,IntegratedLufs=integrated,TruePeakDbtp=peak,LraAcceptance="Withheld: window shorter than one minute",Scope="Four-second low-crest native-decoded live window, sample clock rebased before exact trimming; no worst-case codec headroom proof"};
        readings.Add(measured);
        Check(result,frames==192000&&finite,output.Id+": exactly 192000 finite native-decoded stereo frames are measured.");
        Check(result,different>frames/2,output.Id+": decoded left/right signals remain distinct.");
        Check(result,Math.Abs(integrated+23)<=0.2,output.Id+": low-crest decoded I is -23 +/-0.2 LUFS (observed "+integrated.ToString(CultureInfo.InvariantCulture)+").");
        Check(result,peak<=-1.0,output.Id+": low-crest decoded true peak is <= -1 dBTP (observed "+peak.ToString(CultureInfo.InvariantCulture)+").");
    }
    private static (int Frames,int Different,bool Finite) Analyze(byte[] bytes)
    {
        if(bytes.Length%8!=0)return (0,0,false);
        var samples=MemoryMarshal.Cast<byte,float>(bytes.AsSpan()); var different=0; var finite=true;
        for(var i=0;i<samples.Length;i+=2){finite&=float.IsFinite(samples[i])&&float.IsFinite(samples[i+1]);if(samples[i]!=samples[i+1])different++;}
        return(samples.Length/2,different,finite);
    }
    private static async Task<(string Output,string Error)> ToolAsync(string executable,IEnumerable<string> arguments,string role,bool stdout=false)
    {
        var process=ChildProcess.Start(executable,arguments,output:stdout,role:role);
        try
        {
            var errors=ChildProcess.DrainErrorAsync(process);var output=stdout?process.StandardOutput.ReadToEndAsync():Task.FromResult("");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(18));
            var diagnostic=await errors;var text=await output;
            if(process.ExitCode!=0)throw new InvalidDataException(role+" failed: "+diagnostic);
            return(text,diagnostic);
        }
        finally{await ChildProcess.StopAsync(process);}
    }
    private static void WriteFixture(string path,int frequency)
    {
        const int frames=144000,bytes=frames*8;
        using var writer=new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);writer.Write(36+bytes);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)3);writer.Write((short)2);
        writer.Write(48000);writer.Write(48000*8);writer.Write((short)8);writer.Write((short)32);writer.Write("data"u8);writer.Write(bytes);
        for(var i=0;i<frames;i++){var value=Math.Sin(2*Math.PI*frequency*i/48000.0);writer.Write((float)(value*0.12));writer.Write((float)(value*0.075));}
    }
}
