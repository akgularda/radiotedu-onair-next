using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace OnAirNext.Installation;

[SupportedOSPlatform("windows")]
internal static class SetupPreparation
{
    public static async Task<SetupPlan> PrepareAsync(string bundleRoot,SetupRequest request,CancellationToken token)
    {
        bundleRoot=SetupPaths.CanonicalRoot(bundleRoot);var manifest=SetupPaths.Resolve(bundleRoot,"bundle.json");
        var digest=await SetupPaths.HashAsync(manifest,token);var bundle=await SetupPaths.VerifyBundleAsync(bundleRoot,digest,token);
        var runtime=SetupPaths.CanonicalRoot(request.RuntimeFolder);
        var reference=bundle.Files.SingleOrDefault(f=>f.Path==CodecRuntimePackage.ReferencePath)??throw new ArgumentException("This setup package has no matching codec runtime reference.");
        var files=await CodecRuntimePackage.InspectAsync(runtime,SetupPaths.Resolve(bundleRoot,reference.Path),reference.Sha256,token);
        var encoders=await CaptureAsync(SetupPaths.Resolve(runtime,"bin/ffmpeg.exe"),["-hide_banner","-encoders"],token);
        if(!encoders.Split('\n').Any(line=>line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)=="libfdk_aac")||
            !encoders.Split('\n').Any(line=>line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)=="flac"))
            throw new InvalidOperationException("The selected runtime must list libfdk_aac and FLAC encoders. No codec substitution is permitted.");
        // Capability listing is deliberately not a codec, decoded-output or loudness qualification.
        foreach(var file in files)if(!string.Equals(await SetupPaths.HashAsync(SetupPaths.Resolve(runtime,file.Path),token),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("The runtime changed during inspection.");
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new InvalidOperationException("The operator account is unavailable.");
        return new(Guid.NewGuid().ToString("N"),sid,bundleRoot,digest,runtime,files,request.StartService);
    }
    private static async Task<string> CaptureAsync(string executable,string[] args,CancellationToken token)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=start};if(!process.Start())throw new IOException("The selected runtime did not start.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        async Task<string> DrainAsync(StreamReader reader){var result=new StringBuilder();var buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer,deadline.Token))>0){if(result.Length+count>262144)throw new IOException("Runtime inspection output exceeded its limit.");result.Append(buffer,0,count);}return result.ToString();}
        var output=DrainAsync(process.StandardOutput);var error=DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);var both=await Task.WhenAll(output,error);if(process.ExitCode!=0)throw new IOException("The selected runtime failed its encoder inspection.");return string.Join("\n",both);
        }
        finally
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);
            try{await Task.WhenAll(output,error);}catch(Exception)when(deadline.IsCancellationRequested||output.IsFaulted||error.IsFaulted){}
        }
    }
}
