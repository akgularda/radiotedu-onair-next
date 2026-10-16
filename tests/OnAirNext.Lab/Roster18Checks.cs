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
        await using var resources=new Resources();
        var database=Path.Combine(root,"playback.db");
        var probes=new List<Probe>(); var probeStarts=new Dictionary<Probe,long>(); var engines=new List<StationEngine>(); var outputs=new List<OutputSettings>();
        var readings=new List<object>(); result.Details["encodedListeners"]=readings;
        result.Details["scope"]="Eight synthetic station programs: eight AAC-LC 192, eight libfdk HE-AACv2 64 and two 24-bit Ogg FLAC branches, all 48 kHz stereo. Low-crest sources only. This is not an imported production rate/name map, worst-case encoded headroom, actual-PC resource, 24/72h or UI acceptance.";
        await using (var journal=new EventJournal(database))
        {
            try
            {
                for(var index=1; index<=8; index++)
                {
                    var group=new List<OutputSettings>{origin.Output("roster-"+index+"-aac",CodecProfile.AacLc192),origin.Output("roster-"+index+"-he",CodecProfile.HeAacV2_64)};
                    if(index>=7)group.Add(origin.Output("roster-"+index+"-flac",CodecProfile.OggFlac));
                    outputs.AddRange(group);
                    engines.Add(new StationEngine(ffmpeg,new("roster-"+index,sources,group),journal));
                }
                foreach(var engine in engines)await engine.StartAsync();
                using(var ready=new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                    while(engines.Any(e=>e.Snapshot().Outputs.Any(o=>o.State!="SourceConnected")))await Task.Delay(100,ready.Token);
                Check(result,outputs.Count==18&&outputs.Count(o=>o.Profile==CodecProfile.HeAacV2_64)==8,"All 18 actual codec branches are concurrently source-connected.");
                foreach(var output in outputs)
                {
                    var started=Stopwatch.GetTimestamp();var listener=new Probe(ffmpeg,origin.Port,output.Mount);
                    probes.Add(listener);probeStarts.Add(listener,started);
                }
                await Task.Delay(6000);
                foreach(var output in outputs)await MeasureOutputAsync(ffmpeg,origin.Port,root,output,readings,result);
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
                Check(result,snapshots.All(s=>s.AuditRejectedEvents==0)&&journal.Error is null,"Concurrent eight-station attempt journals remain writable without rejected events.");
            }
            finally
            {
                foreach(var probe in probes)await probe.DisposeAsync();
                foreach(var engine in engines)await engine.DisposeAsync();
            }
        }
        Check(result,EventJournal.Verify(database),"The full 18-branch test preserves the immutable attempt hash chain.");
        result.Details["profileCounts"]=new{AacLc192=8,HeAacV2_64=8,OggFlac24=2};
    }

    private static async Task MeasureOutputAsync(string ffmpeg,int port,string root,OutputSettings output,List<object> readings,CaseResult result)
    {
        var encoded=Path.Combine(root,output.Id+(output.Profile==CodecProfile.OggFlac?".ogg":".aac"));
        await ToolAsync(ffmpeg,["-nostdin","-hide_banner","-v","error","-y","-rw_timeout","5000000","-analyzeduration","2000000","-probesize","65536",
            "-i",$"http://127.0.0.1:{port}{output.Mount}","-t","4.1","-vn","-sn","-dn","-c:a","copy","-f",output.Profile==CodecProfile.OggFlac?"ogg":"adts",encoded],"roster-capture");
        var ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe");
        var (probeText,_)=await ToolAsync(ffprobe,["-v","error","-show_entries","stream=codec_name,profile,sample_rate,channels,bits_per_raw_sample,bit_rate","-of","json",encoded],"roster-format-probe",true);
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
        decode.AddRange(["-i",encoded,"-vn","-sn","-dn","-filter_threads","1","-af",SourceNormalization.DecodeFormat+",asetpts=N/SR/TB,atrim=end_sample=192000,ebur128=peak=true:framelog=verbose","-c:a","pcm_f32le","-f","f32le",pcm]);
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
        var measured=new{output.Id,Profile=output.Profile.ToString(),ActualStream=stream.Clone(),DecodedFrames=frames,DistinctStereoFrames=different,IntegratedLufs=integrated,TruePeakDbtp=peak,LraAcceptance="Withheld: window shorter than one minute",Scope="Four-second low-crest decoded window only; no worst-case codec headroom proof"};
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
