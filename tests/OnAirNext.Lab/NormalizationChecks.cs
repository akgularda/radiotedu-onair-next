using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Globalization;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class NormalizationChecks
{
    private static void Check(CaseResult result,bool success,string text)
    {
        result.Checks.Add(new {Passed=success,Description=text});
        if(!success)throw new InvalidDataException(text);
    }
    public static async Task RunAsync(string ffmpeg,string icecastRoot,string root,CaseResult result)
    {
        Directory.CreateDirectory(root);
        await using var probe=new LibraryAudioProbe(ffmpeg);
        var details=new List<object>(); MediaItem? target=null;
        foreach(var name in new[]{"target","loud","quiet","transient","silence"})
        {
            var path=Path.Combine(root,name+".wav"); WriteFixture(path,name);
            var original=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var metadata=await probe.VerifyAsync(path,CancellationToken.None);
            var measured=metadata.Loudness ?? throw new InvalidDataException("Missing measured source.");
            Check(result,measured.ContentSha256==original,"Completed "+name+" scan binds the full source SHA-256.");
            var kind=name switch{"loud"=>ContentKind.Jingle,"quiet"=>ContentKind.Sweeper,"transient"=>ContentKind.Advertisement,"silence"=>ContentKind.Program,_=>ContentKind.Music};
            var item=new MediaItem(name,path,"Synthetic engineering signal",name,Kind:kind,DurationSeconds:metadata.Seconds,Loudness:measured);
            var raw=await DecodeAsync(ffmpeg,item with{Loudness=null});
            var processed=await DecodeAsync(ffmpeg,item);
            var before=Measure(raw);var after=Measure(processed);var gain=SourceNormalization.Decide(measured);
            Check(result,raw.Length==192000&&processed.Length==raw.Length,name+": no added latency, samples or finite-EOF padding.");
            if(before.IntegratedLufs is double sourceI)
                Check(result,Math.Abs(sourceI-measured.IntegratedLufs!.Value)<=0.15,name+": independent native instrument agrees with FFmpeg source I within 0.15 LU.");
            double maximumError=0;var different=0;
            for(var i=0;i<raw.Length;i++)maximumError=Math.Max(maximumError,Math.Abs(processed[i]-raw[i]*gain.Linear));
            for(var i=0;i<processed.Length;i+=2)if(processed[i]!=processed[i+1])different++;
            Check(result,maximumError<=0.0000001,name+": real decoder applies only the measured fixed stereo gain.");
            Check(result,after.TruePeakDbtp is null||after.TruePeakDbtp<=-1.0,name+": measured source true peak stays below the linear ceiling.");
            var expected=name switch{"target" or "loud"=>"Target gain","quiet"=>"Boost constrained","transient"=>"Peak constrained",_=>"No gated programme"};
            Check(result,gain.State==expected,name+": target/peak/boost/silence decision is explicit.");
            if(expected=="Target gain")Check(result,Math.Abs(after.IntegratedLufs!.Value+23)<=0.2,name+": independently measured processed programme is within -23 +/-0.2 LUFS.");
            if(name!="silence")Check(result,different>raw.Length/4,name+": distinct stereo channels survive processing.");
            Check(result,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))==original,name+": original media is unchanged.");
            details.Add(new{Name=name,Source=measured,Gain=gain,Before=before,After=after,MaximumPcmError=maximumError});
            if(name=="target")target=item;
        }
        result.Details["sources"]=details;
        await using(var journal=new EventJournal(Path.Combine(root,"playback.db")))
        {
            var sink=new MeasurementSink();
            await using var engine=new StationEngine(ffmpeg,new("normalized",[target!],[],Repeat:false),journal,[sink]);
            await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(12));
            var state=engine.Snapshot();
            Check(result,state.State=="Completed"&&state.Samples==96000&&state.CompletedItems==1,"Real finite station completes at the original exact sample boundary.");
            var reading=sink.Finish();Check(result,Math.Abs(reading.IntegratedLufs!.Value+23)<=0.2,"Actual programme publication carries the normalized source.");
            var events=EventJournal.ReadEvents(journal.DatabasePath);
            Check(result,events.Count(x=>x.Type=="started")==1&&events.Count(x=>x.Type=="ended")==1&&events.All(x=>x.SourceGain?.State=="Target gain"),"Actual attempt records preserve the gain used at start and completion.");
            Check(result,EventJournal.Verify(journal.DatabasePath),"Attempt hash chain still verifies after source-gain evidence is added.");
            result.Details["engine"]=new{State=state,Reading=reading,Events=events};
        }
        await EncodedChecksAsync(ffmpeg,icecastRoot,root,target!,result);
        var bad=target! with{Loudness=target!.Loudness! with{ModifiedTicks=target.Loudness!.ModifiedTicks-1}};
        var rejected=false;try{await using var decoder=new MediaDecoder(ffmpeg,bad);}catch(InvalidDataException){rejected=true;}
        Check(result,rejected,"A changed source revision is rejected before a decoder starts.");
        Check(result,ChildProcess.ActiveProcesses.Count==0,"All real validation/decoder workers are confirmed closed.");
        result.Details["scope"]="Synthetic full-file source measurement, fixed-gain PCM, finite playout and history only. Two loopback AAC-LC/FLAC branches use a low-crest synthetic programme only; no final-mix limiter, worst-case codec headroom, HE-AAC, all-18-output, production media or commissioning acceptance.";
    }

    private static async Task EncodedChecksAsync(string ffmpeg,string icecastRoot,string root,MediaItem target,CaseResult result)
    {
        await using var origin=new Sandbox(icecastRoot,Path.Combine(root,"encoded"));await origin.StartAsync();
        await using var journal=new EventJournal(Path.Combine(root,"encoded-playback.db"));
        var outputs=new[]{origin.Output("normalized-aac",CodecProfile.AacLc192),origin.Output("normalized-flac",CodecProfile.OggFlac)};
        await using var engine=new StationEngine(ffmpeg,new("encoded-normalized",[target],outputs),journal);
        await engine.StartAsync();
        using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(engine.Snapshot().Outputs.Any(x=>x.State!="SourceConnected"))await Task.Delay(50,ready.Token);
        var readings=new List<object>();result.Details["encodedListeners"]=readings;
        foreach(var output in outputs)
        {
            var process=ChildProcess.Start(ffmpeg,["-hide_banner","-v","info","-nostats","-nostdin","-rw_timeout","5000000",
                "-analyzeduration","200000","-probesize","32768","-i",$"http://127.0.0.1:{origin.Port}{output.Mount}",
                "-t","4","-vn","-sn","-dn","-filter_threads","1","-af",SourceNormalization.DecodeFormat+",asetpts=N/SR/TB,atrim=end_sample=192000,ebur128=peak=true:framelog=verbose,astats=measure_perchannel=none:measure_overall=Number_of_samples","-f","null","-"],role:"normalized-listener-meter");
            try
            {
                var errors=ChildProcess.DrainErrorAsync(process);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));var diagnostic=await errors;
                if(process.ExitCode!=0)throw new InvalidDataException("Normalized listener decode failed: "+output.Id);
                var at=diagnostic.LastIndexOf("Summary:",StringComparison.Ordinal);if(at<0)throw new InvalidDataException("Listener meter summary missing.");
                var summary=diagnostic[at..];
                double Metric(string key,string unit)
                {
                    var match=Regex.Match(summary,@"(?m)^\s*"+key+@":\s*(-?\d+(?:\.\d+)?)\s+"+unit+@"\s*$",RegexOptions.CultureInvariant);
                    if(!match.Success)throw new InvalidDataException("Listener meter value missing: "+key);
                    return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
                }
                var integrated=Metric("I","LUFS");var peak=Metric("Peak","dBFS");
                var samples=Regex.Matches(diagnostic,@"Number of samples:\s*(\d+(?:\.\d+)?)",RegexOptions.CultureInvariant);
                var frames=samples.Count==0?0:double.Parse(samples[^1].Groups[1].Value,CultureInfo.InvariantCulture);
                readings.Add(new{Profile=output.Profile.ToString(),Mount=output.Mount,RequestedSeconds=4,DecodedFrames=frames,SampleClock="Decoded PCM count rebased to zero; no transport timestamp trimming",IntegratedLufs=integrated,TruePeakDbtp=peak,DiagnosticTail=diagnostic[^Math.Min(1800,diagnostic.Length)..],Scope="Low-crest synthetic programme; not worst-case codec-headroom qualification"});
                Check(result,frames==192000,output.Profile+": listener measures exactly 192000 decoded stereo frames (observed "+frames.ToString(CultureInfo.InvariantCulture)+").");
                Check(result,Math.Abs(integrated+23)<=0.2,output.Profile+": independently decoded synthetic output measures -23 +/-0.2 LUFS (observed "+integrated.ToString(CultureInfo.InvariantCulture)+").");
                Check(result,peak<=-1,output.Profile+": independently decoded synthetic output stays below -1 dBTP (observed "+peak.ToString(CultureInfo.InvariantCulture)+").");
            }
            finally{await ChildProcess.StopAsync(process);}
        }
        Check(result,engine.Snapshot().Outputs.All(x=>x.EncoderStarts==1&&x.DroppedBlocks==0),"Both normalized codec branches remain connected without encoder restarts or dropped blocks during sequential listener measurements.");
        result.Details["encodedListeners"]=readings;
    }

    private static async Task<float[]> DecodeAsync(string ffmpeg,MediaItem item)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var decoder=new MediaDecoder(ffmpeg,item);await decoder.Ready.WaitAsync(deadline.Token);
        var values=new List<float>(192000);
        while(await decoder.ReadAsync(deadline.Token) is {} block)
        {
            using(block){values.AddRange(MemoryMarshal.Cast<byte,float>(block.Memory.Span).ToArray());}
            if(values.Count>192000)throw new InvalidDataException("Synthetic decoder exceeded its bounded fixture.");
        }
        return values.ToArray();
    }
    private static LoudnessReading Measure(float[] values)
    {
        var meter=new StereoLoudnessMeter48k(TimeSpan.FromSeconds(3));meter.Feed(values);meter.Finish();return meter.Read();
    }
    private sealed class MeasurementSink:IProgramOutput
    {
        private readonly StereoLoudnessMeter48k _meter=new(TimeSpan.FromSeconds(3));
        public void Start(){} public bool Publish(PcmBlock block){_meter.Feed(MemoryMarshal.Cast<byte,float>(block.Memory.Span));return true;}
        public OutputSnapshot Snapshot()=>new("synthetic-sink","Ready",0,0,0,0,0,null,null);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public LoudnessReading Finish(){_meter.Finish();return _meter.Read();}
    }
    private static void WriteFixture(string path,string name)
    {
        using var writer=new BinaryWriter(File.Create(path));const int frames=96000;
        writer.Write("RIFF"u8);writer.Write(36+frames*8);writer.Write("WAVEfmt "u8);writer.Write(16);
        writer.Write((short)3);writer.Write((short)2);writer.Write(48000);writer.Write(384000);writer.Write((short)8);writer.Write((short)32);
        writer.Write("data"u8);writer.Write(frames*8);
        var amplitude=name switch{"loud"=>0.8,"quiet"=>0.002,"transient"=>0.001,"silence"=>0,_=>0.06};
        for(var i=0;i<frames;i++)
        {
            var left=(float)(amplitude*Math.Sin(2*Math.PI*440*i/48000));var right=(float)(amplitude*.75*Math.Sin(2*Math.PI*880*i/48000));
            if(name=="transient"&&i==48000){left=0.95f;right=-0.95f;}
            writer.Write(left);writer.Write(right);
        }
    }
}
