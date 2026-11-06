using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class SourceCueQualification
{
    public static async Task<int> RunAsync(string ffmpeg,string output,string? icecastRoot=null)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","lab","source-cues",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var wave=Path.Combine(fixture,"original.wav");WriteWave(wave);
        var encoded=Path.Combine(fixture,"unicode.flac");
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
        if(icecastRoot is not null)await EncodedAsync(ffmpeg,icecastRoot,fixture,music,checks);
        await using(var journal=new EventJournal(Path.Combine(fixture,"cue-playout.db")))
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
        var result=new{passed=true,sourceUnchanged=true,utf8Metadata=true,range,checks,encodedLoopback=icecastRoot is not null,scope="Real source validation and decoder output. Exterior silence removed; internal silence and program timing preserved. Optional three-codec checks use literal loopback only. No production outputs were opened."};
        await File.WriteAllTextAsync(Path.Combine(output,"source-cue-evidence.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks=checks.Count,evidence=Path.Combine(output,"source-cue-evidence.json")}));return 0;
    }
    private static async Task EncodedAsync(string ffmpeg,string icecastRoot,string root,MediaItem music,List<object> checks)
    {
        await using var origin=new Sandbox(icecastRoot,Path.Combine(root,"encoded-origin"),utf8Metadata:true);await origin.StartAsync();
        var outputs=new[]{origin.Output("cue-aac",CodecProfile.AacLc192),origin.Output("cue-low",CodecProfile.HeAacV2_64),origin.Output("cue-flac",CodecProfile.OggFlac)};
        await using var journal=new EventJournal(Path.Combine(root,"encoded-cues.db"));
        await using var engine=new StationEngine(ffmpeg,new("encoded-cues",[music,music with{Id="second"}],outputs),journal);
        await engine.StartAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while(engine.Snapshot().Outputs.Any(o=>o.State!="SourceConnected"))await Task.Delay(50,deadline.Token);
        var results=await Task.WhenAll(outputs.Select(async output=>
        {
            using var pipe=new CodecPipe(System.IO.Pipes.PipeDirection.In);
            var input=$"http://127.0.0.1:{origin.Port}{output.Mount}";
            var process=ChildProcess.Start(ffmpeg,["-hide_banner","-v","error","-nostdin","-y","-rw_timeout","5000000","-f",output.Profile==CodecProfile.OggFlac?"ogg":"aac","-i",input,"-t","10","-map","0:a:0","-vn","-threads","1","-filter_threads","1","-af",SourceNormalization.DecodeFormat+",asetpts=N/SR/TB,atrim=end_sample=480000","-ac","2","-ar","48000","-c:a","pcm_f32le","-f","f32le",pipe.Path],role:"source-cue-listener");
            var error=ChildProcess.DrainErrorAsync(process);
            try
            {
                var connected=pipe.ConnectAsync(deadline.Token);var exited=process.WaitForExitAsync(deadline.Token);
                if(await Task.WhenAny(connected,exited)==exited&&!connected.IsCompleted)throw new InvalidDataException("Cue listener exited before opening PCM: "+await error);
                await connected;var buffer=new byte[65536];int carried=0,count;long frames=0,quiet=0,longest=0;
                while((count=await pipe.Stream.ReadAsync(buffer.AsMemory(carried),deadline.Token))>0)
                {
                    var total=carried+count;var aligned=total-total%8;
                    Measure(buffer.AsSpan(0,aligned),ref frames,ref quiet,ref longest,.0004);
                    carried=total-aligned;if(carried>0)Buffer.BlockCopy(buffer,aligned,buffer,0,carried);
                }
                await exited;
                if(process.ExitCode!=0||carried!=0||frames!=480000||longest<86400||longest>110400)throw new InvalidDataException("Encoded cue timing or preserved internal pause failed: "+output.Profile+" "+await error);
                return (object)new{kind="EncodedCue",output.Profile,decodedFrames=frames,longestQuietFrames=longest};
            }
            finally {await ChildProcess.StopAsync(process);}
        }));
        checks.AddRange(results);
        var expectedSong=music.Artist+" - "+music.Title;
        string[] observedMetadata;
        IcyRead[] observedIcy;
        string[] originAcknowledgements=[];
        using(var statusClient=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(2)})
        {
            // Anonymous status JSON can omit title. Verify the actual ICY block a listener receives.
            observedIcy=await Task.WhenAll(outputs.Where(o=>o.Profile!=CodecProfile.OggFlac)
                .Select(output=>ReadIcyTitleAsync(statusClient,$"http://127.0.0.1:{origin.Port}{output.Mount}",deadline.Token)));
            observedMetadata=observedIcy.Select(read=>read.Title).ToArray();
            // On failure only: distinguish a misleading acknowledgement from a listener parser failure.
            // This origin and its credentials are synthetic, literal-loopback fixtures only.
            if(observedMetadata.Any(song=>song!=expectedSong))
                originAcknowledgements=await Task.WhenAll(outputs.Where(o=>o.Profile!=CodecProfile.OggFlac).Select(async output=>
            {
                using var update=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{origin.Port}/admin/metadata?mode=updinfo&charset=UTF-8&mount={Uri.EscapeDataString(output.Mount)}&song={Uri.EscapeDataString(expectedSong)}");
                update.Headers.Authorization=new("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes("source:"+origin.Password)));
                using var response=await statusClient.SendAsync(update,deadline.Token);
                var body=await response.Content.ReadAsStringAsync(deadline.Token);
                return ((int)response.StatusCode)+":"+body[..Math.Min(1024,body.Length)];
            }));
        }
        var metadataReady=observedMetadata.Length==2&&observedMetadata.All(song=>song==expectedSong)&&
            engine.Snapshot().Outputs.Where(o=>o.Id!=outputs.Single(x=>x.Profile==CodecProfile.OggFlac).Id)
                .All(o=>o.Metadata is{Updates:>0,LastError:null}&&o.Metadata.PublishedTitle==expectedSong);
        if(!metadataReady)throw new InvalidDataException("Live AAC metadata did not reach the independent origin with exact Unicode artist/title. "+
            JsonSerializer.Serialize(new{expectedSong,observedMetadata,observedIcy,originAcknowledgements,outputs=engine.Snapshot().Outputs}));
        checks.Add(new{kind="LiveMetadata",dynamicOutputs=2,utf8=true,originCharset="UTF-8",expectedSong,scope="Real loopback Icecast URL updates and exact listener ICY blocks, with the fixture explicitly configured to represent Unicode. Ogg in-band comments are not ICY metadata."});
        if(engine.Snapshot().Outputs.Any(o=>o.State!="SourceConnected"||o.Connections!=1||o.EncoderStarts!=1||o.DroppedBlocks!=0))throw new InvalidDataException("Cued programme restarted or dropped an encoded output.");
    }
    private sealed record IcyRead(string Title,string[] Blocks);
    private static async Task<IcyRead> ReadIcyTitleAsync(HttpClient client,string address,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,address);
        request.Headers.Add("Icy-MetaData","1");
        request.Headers.UserAgent.ParseAdd("OnAirNext-Metadata-Qualification/"+Guid.NewGuid().ToString("N"));
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        if(!response.Headers.TryGetValues("icy-metaint",out var values)||!int.TryParse(values.Single(),out var interval)||interval is <1 or >1048576)
            throw new InvalidDataException("The loopback AAC listener did not advertise bounded ICY metadata.");
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        var audio=new byte[interval];var length=new byte[1];var blocks=new List<string>();
        for(var attempt=0;attempt<4;attempt++)
        {
            await stream.ReadExactlyAsync(audio,token);await stream.ReadExactlyAsync(length,token);
            var metadata=new byte[length[0]*16];await stream.ReadExactlyAsync(metadata,token);
            var text=Encoding.UTF8.GetString(metadata).TrimEnd('\0');
            blocks.Add(text[..Math.Min(256,text.Length)]);
            var match=System.Text.RegularExpressions.Regex.Match(text,@"StreamTitle='(.*?)';",System.Text.RegularExpressions.RegexOptions.Singleline);
            if(match.Success&&match.Groups[1].Value.Length>0)return new(match.Groups[1].Value,blocks.ToArray());
        }
        return new("",blocks.ToArray());
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
    private static void Measure(ReadOnlySpan<byte> bytes,ref long frames,ref long quiet,ref long longest,double threshold=.00001)
    {
        var samples=MemoryMarshal.Cast<byte,float>(bytes);
        for(var i=0;i<samples.Length;i+=2)
        {frames++;if(Math.Max(Math.Abs(samples[i]),Math.Abs(samples[i+1]))<threshold){quiet++;longest=Math.Max(longest,quiet);}else quiet=0;}
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
