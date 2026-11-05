using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class SourceCueQualification
{
    public static async Task<int> RunAsync(string ffmpeg,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var wave=Path.Combine(output,"original.wav");WriteWave(wave);
        var encoded=Path.Combine(output,"unicode.flac");
        await ChildProcess.CaptureAsync(ffmpeg,"-hide_banner","-v","error","-nostdin","-y","-i",wave,"-c:a","flac","-metadata","artist=久石譲","-metadata","title=Şarkı – été",encoded);
        var original=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(encoded)));
        await using var probe=new LibraryAudioProbe(ffmpeg);
        var metadata=await probe.VerifyAsync(encoded,CancellationToken.None);
        var range=metadata.Loudness?.PlaybackRange??throw new InvalidDataException("Measured source bounds missing.");
        if(metadata.Artist!="久石譲"||metadata.Title!="Şarkı – été")throw new InvalidDataException("UTF-8 metadata did not survive validation.");
        if(range.TotalFrames!=12*48000||range.StartFrame!=95040||range.EndFrame!=292800)throw new InvalidDataException("Source cue bounds differ from the generated signal.");
        var checks=new List<object>();
        foreach(var kind in new[]{ContentKind.Music,ContentKind.Jingle,ContentKind.Sweeper,ContentKind.Program})
        {
            var item=new MediaItem("cue-"+kind,encoded,metadata.Artist,metadata.Title,kind,SourcePlaybackRange.Duration(kind,metadata.Seconds,metadata.Loudness),metadata.Loudness);
            var expected=kind==ContentKind.Program?range.TotalFrames:range.Frames;
            var decoded=await DecodeAsync(ffmpeg,item,0,false);
            if(decoded.Frames!=expected)throw new InvalidDataException("Cue decoder frame count mismatch: "+kind);
            if(decoded.LongestQuietFrames<2*48000)throw new InvalidDataException("Internal musical silence was changed: "+kind);
            checks.Add(new{kind,decoded.Frames,decoded.LongestQuietFrames,expected});
            if(kind==ContentKind.Music)
            {
                var seek=await DecodeAsync(ffmpeg,item,48000,true);
                if(seek.Frames!=range.Frames-48000)throw new InvalidDataException("Private seek was not relative to the cued programme.");
                checks.Add(new{kind="PrivateCueSeek",seek.Frames,expected=range.Frames-48000});
            }
        }
        var music=new MediaItem("first",encoded,metadata.Artist,metadata.Title,ContentKind.Music,range.Frames/48000d,metadata.Loudness);
        await using(var journal=new EventJournal(Path.Combine(output,"cue-playout.db")))
        {
            var sink=new BoundarySink(range.Frames);
            await using var engine=new StationEngine(ffmpeg,new("cue-test",[music,music with{Id="second"}],[],Repeat:false),journal,[sink]);
            await engine.StartAsync();await engine.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            var state=engine.Snapshot();
            if(state.State!="Completed"||state.Samples!=2*range.Frames||state.CompletedItems!=2)throw new InvalidDataException("Cued finite playout did not retain exact sample boundaries.");
            if(sink.LongestBoundaryQuiet>48000/5)throw new InvalidDataException("An unwanted silence gap remains between the cued songs.");
            var history=EventJournal.ReadEvents(journal.DatabasePath);
            if(history.Count(e=>e.Type=="started")!=2||history.Count(e=>e.Type=="ended")!=2||!EventJournal.Verify(journal.DatabasePath))throw new InvalidDataException("Cued attempts or journal hash chain are incorrect.");
            checks.Add(new{kind="FiniteProgrammeBoundary",state.Samples,sink.LongestBoundaryQuiet,expected=2*range.Frames});
        }
        if(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(encoded)))!=original)throw new InvalidDataException("Source file changed during cue measurement or playback.");
        if(ChildProcess.ActiveProcesses.Count!=0)throw new InvalidDataException("Qualification child cleanup remains pending.");
        var result=new{passed=true,sourceUnchanged=true,utf8Metadata=true,range,checks,scope="Real source validation and decoder output. Exterior silence removed; internal silence and program timing preserved. No live outputs were opened."};
        await File.WriteAllTextAsync(Path.Combine(output,"source-cue-evidence.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks=checks.Count,evidence=Path.Combine(output,"source-cue-evidence.json")}));return 0;
    }
    private sealed class BoundarySink(long boundary):IProgramOutput
    {
        private long _frames,_quiet;
        public long LongestBoundaryQuiet{get;private set;}
        public void Start(){}
        public bool Publish(PcmBlock block)
        {
            var samples=MemoryMarshal.Cast<byte,float>(block.Memory.Span);
            for(var i=0;i<samples.Length;i+=2)
            {
                if(Math.Abs(_frames-boundary)<=14400&&Math.Max(Math.Abs(samples[i]),Math.Abs(samples[i+1]))<.00001)
                {_quiet++;LongestBoundaryQuiet=Math.Max(LongestBoundaryQuiet,_quiet);}else _quiet=0;
                _frames++;
            }
            return true;
        }
        public OutputSnapshot Snapshot()=>new("cue-boundary-sink","Ready",0,0,_frames,0,0,null,null);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task<(long Frames,long LongestQuietFrames)> DecodeAsync(string ffmpeg,MediaItem item,long start,bool fast)
    {
        await using var decoder=new MediaDecoder(ffmpeg,item,start,fastSeek:fast);long frames=0,quiet=0,longest=0;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(await decoder.ReadAsync(deadline.Token) is{} block)
        {try{Measure(block.Memory.Span,ref frames,ref quiet,ref longest);}finally{block.Dispose();}}
        return(frames,longest);
    }
    private static void Measure(ReadOnlySpan<byte> bytes,ref long frames,ref long quiet,ref long longest)
    {
        var samples=MemoryMarshal.Cast<byte,float>(bytes);
        for(var i=0;i<samples.Length;i+=2)
        {frames++;if(Math.Max(Math.Abs(samples[i]),Math.Abs(samples[i+1]))<.00001){quiet++;longest=Math.Max(longest,quiet);}else quiet=0;}
    }
    private static void WriteWave(string path)
    {
        const int frames=12*48000;using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+frames*8);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);writer.Write((short)3);writer.Write((short)2);writer.Write(48000);writer.Write(48000*8);writer.Write((short)8);writer.Write((short)32);
        writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(frames*8);
        for(var i=0;i<frames;i++)
        {var t=i/48000d;var active=t>=2&&t<3||t>=5&&t<6;var value=active?(float)(.1*Math.Sin(2*Math.PI*440*t)):0;writer.Write(value);writer.Write(value);}
    }
}
