using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OnAirNext.Engine;

internal sealed record LimiterReferenceResult(string Name,bool Passed,long InputFrames,long OutputFrames,
    double? NativeTruePeakDbtp,double? IndependentTruePeakDbtp,double? IndependentFrames,LimiterSnapshot? Processing,string? Error=null);
internal static class LimiterReferenceChecks
{
    public const int CaseCount=6;
    public static async Task<IReadOnlyList<LimiterReferenceResult>> RunAsync(string root,Func<string[],Task<string>> run)
    {
        var results=new List<LimiterReferenceResult>();
        foreach(var name in new[]{"limiter-phase12k","limiter-phase20k","limiter-near-nyquist","limiter-impulse","limiter-noise","limiter-alternating"})
        {
            var file=Path.Combine(root,name+".generated-reference-f32le");
            try
            {
                var input=Signal(name);var limiter=new TruePeakLimiter48k();var output=new List<float>(input.Length);var block=new float[1920];
                for(var offset=0;offset<input.Length;offset+=1920)
                {var n=limiter.Process(input.AsSpan(offset,Math.Min(1920,input.Length-offset)),block);output.AddRange(block.AsSpan(0,n*2).ToArray());}
                limiter.Finish();int count;while((count=limiter.Flush(block))>0)output.AddRange(block.AsSpan(0,count*2).ToArray());
                var samples=output.ToArray();var meter=new StereoLoudnessMeter48k(TimeSpan.FromSeconds(5));meter.Feed(samples);meter.Finish();var reading=meter.Read();
                // Generated reference PCM only, never programme/listener/private-microphone recording. Deleted in finally.
                await File.WriteAllBytesAsync(file,MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
                var text=await run(["-hide_banner","-nostdin","-nostats","-threads","1","-filter_threads","1","-f","f32le","-ar","48000","-ac","2","-i",file,
                    "-af","ebur128=peak=true:framelog=verbose,astats=measure_perchannel=none:measure_overall=Number_of_samples","-f","null","-"]);
                var at=text.LastIndexOf("Summary:",StringComparison.Ordinal);if(at<0)throw new InvalidDataException("Independent limiter summary missing.");
                var peak=Regex.Match(text[at..],@"\bPeak:\s*([-+\d.]+)\s*dBFS",RegexOptions.CultureInvariant);
                var frames=Regex.Matches(text,@"Number of samples:\s*([\d.]+)",RegexOptions.CultureInvariant);
                if(!peak.Success||frames.Count==0)throw new InvalidDataException("Independent limiter peak/sample count missing.");
                var independent=double.Parse(peak.Groups[1].Value,CultureInfo.InvariantCulture);var independentFrames=double.Parse(frames[^1].Groups[1].Value,CultureInfo.InvariantCulture);
                var ok=samples.Length==input.Length&&independentFrames==input.Length/2&&reading.TruePeakDbtp<=-1.499&&independent<=-1&&limiter.Snapshot().LimitedFrames>0;
                results.Add(new(name,ok,input.Length/2,samples.Length/2,reading.TruePeakDbtp,independent,independentFrames,limiter.Snapshot()));
                Console.WriteLine(name+": "+(ok?"passed":"FAILED")+"; independent peak "+independent.ToString("F2",CultureInfo.InvariantCulture)+" dBTP");
            }
            catch(Exception ex){results.Add(new(name,false,0,0,null,null,null,null,ex.Message));Console.WriteLine(name+": FAILED");}
            finally{if(File.Exists(file))File.Delete(file);}
        }
        return results;
    }
    private static float[] Signal(string name)
    {
        var samples=new float[192000*2];var random=new Random(17705);
        for(var i=0;i<samples.Length/2;i++)
        {
            var frequency=name=="limiter-phase20k"?20000:name=="limiter-near-nyquist"?23500:12000;
            var value=2*Math.Sin(2*Math.PI*frequency*i/48000+Math.PI/4);
            if(name=="limiter-noise")value=(random.NextDouble()*2-1)*4;
            if(name=="limiter-impulse")value=i==48000?64:0.002*Math.Sin(2*Math.PI*440*i/48000);
            if(name=="limiter-alternating")value=i%17==0?(i%2==0?16:-16):0.03;
            samples[i*2]=(float)value;samples[i*2+1]=(float)(value*(name=="limiter-alternating"?-0.75:0.5));
        }
        return samples;
    }
}
