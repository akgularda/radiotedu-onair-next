using System.Runtime.InteropServices;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static partial class ProcessingChecks
{
    private static async Task PrivateCueChecksAsync(string ffmpeg,string root,CaseResult result)
    {
        var folder=Path.Combine(root,"private-cue-library");Directory.CreateDirectory(folder);
        var privateItem=Fixture(folder,"cue-only",96000,.4f);var programme=Fixture(root,"cue-programme-only",480000,.03f);
        await using var journal=new EventJournal(Path.Combine(root,"private-cue.db"));
        await journal.InitializeDesiredStateAsync(new("cue-library",[],[],AutoStart:false));
        await using var library=new StationLibrary(journal,ffmpeg,root,["cue-library"],(path,token)=>Task.FromResult(new AudioMetadata("Synthetic private cue","Private-only source",2)));
        await library.InitializeAsync();await library.SetRootAsync("cue-library",folder,"cue-source");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while(library.ReadyInventory("cue-library").ReadyCount!=1){library.Wake();await Task.Delay(100,timeout.Token);}
        var page=library.Page("cue-library","",0);var asset=page.Items.Single();var cue=new PrivateCuePlayback(library,ffmpeg);
        PrivateCueRequest Request(long offset=0)=>new(Guid.NewGuid().ToString("N"),"cue-library",page.Root!.Revision,asset.Id,asset.EditVersion,asset.SourceStamp,offset);
        var sink=new Capture(journal.DatabasePath);await using var engine=new StationEngine(ffmpeg,new("cue-programme",[programme],[],Repeat:false),journal,[sink]);
        await engine.StartAsync();var maximumChildren=0;
        using var full=new MemoryStream();
        var completed=await cue.PlayAsync(Request(),(header,token)=>{Check(result,header.Accepted&&header.StartSample==0&&header.TotalSamples==96000,"Private cue confirms its reviewed source and 48 kHz stereo extent.");return Task.CompletedTask;},async(bytes,token)=>
        {maximumChildren=Math.Max(maximumChildren,ChildProcess.ActiveProcesses.Count);await full.WriteAsync(bytes,token);},timeout.Token);
        Check(result,completed.Completed&&completed.Frames==96000&&full.Length==96000*8,"Cue EOF and limiter tail preserve all 96,000 real stereo frames without padding.");
        Check(result,Peak(full.ToArray())>.2&&Peak(full.ToArray())<.5,"The independent private bus contains the actual non-silent source PCM.");
        using var seek=new MemoryStream();
        var sought=await cue.PlayAsync(Request(48013),(header,token)=>Task.CompletedTask,async(bytes,token)=>await seek.WriteAsync(bytes,token),timeout.Token);
        Check(result,sought.Completed&&sought.Frames==47987&&seek.Length==47987*8,"Cue seek combines whole-second input seeking with exact frame trim, preserving the 47,987 remaining source frames.");
        var cancelled=Request();long received=0;var cancellationObserved=false;
        try{await cue.PlayAsync(cancelled,(header,token)=>Task.CompletedTask,(bytes,token)=>{received+=bytes.Length/8;if(received>=1920)cue.RequestStop(cancelled.SessionId);return Task.CompletedTask;},timeout.Token);}
        catch(OperationCanceledException){cancellationObserved=true;}
        Check(result,cancellationObserved&&received>0&&cue.Snapshot().State=="Idle"&&!ChildProcess.ActiveProcesses.Values.Any(x=>x.StartsWith("private-cue",StringComparison.Ordinal)),"Cue Stop confirms private decoder cleanup before releasing its lease.");
        Check(result,engine.Snapshot().RequestedRunning&&maximumChildren<=2,"The programme keeps running while one bounded background cue decoder is active.");
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(25));
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        Check(result,sink.Frames==480000&&sink.Samples.All(x=>Math.Abs(x)<.031)&&engine.Snapshot().CompletedItems==1,
            "Private cue, seek and cancellation cannot mix their louder PCM into the programme or consume its frames.");
        Check(result,events.Where(x=>x.Type=="started").Count()==1&&events.Where(x=>x.Type=="started").All(x=>x.ItemId==programme.Id)&&
            events.All(x=>x.ItemId!=asset.Id)&&EventJournal.Verify(journal.DatabasePath),"Only the actual programme attempt enters immutable play history; cue never becomes a programme occurrence.");
        var progress=await journal.BrandingProgressAsync("cue-programme");
        Check(result,progress.CompletedMusic==1,"Private listening does not advance programme music/jingle/sweeper counters.");
        result.Details["private-cue"]=new{FullFrames=completed.Frames,SeekFrames=sought.Frames,StoppedFrames=received,MaximumChildren=maximumChildren,
            ProgrammeFrames=sink.Frames,Events=events,Progress=progress,Scope="Synthetic software cue only; no physical browser output or microphone was opened."};
    }
    private static float Peak(byte[] data)
    {var peak=0f;foreach(var sample in MemoryMarshal.Cast<byte,float>(data))peak=Math.Max(peak,Math.Abs(sample));return peak;}
}
