using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.ConsoleProtocol;

namespace OnAirNext.Installation;

// UAC grants this bounded helper read access to the exact LocalService process.
// It never stops a service, changes its ACL/policy, or writes installation/engine data.
[SupportedOSPlatform("windows")]
internal static class WindowsReviewElevation
{
    private static string Folder=>Path.Combine(ConsoleEndpointLocation.Directory,"update-reviews");
    internal static bool NeedsApproval(Exception error)
    {
        if(error is not UnauthorizedAccessException && error is not Win32Exception{NativeErrorCode:5})return false;
        using var identity=WindowsIdentity.GetCurrent();return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    internal static async Task<AppUpdateReview> PrepareAsync(string package,CancellationToken token)
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new UnauthorizedAccessException("The Windows operator is unavailable.");
        SetupPaths.RequireNoLinks(Folder);Directory.CreateDirectory(Folder);
        var request=new UpdateReviewRequest(Guid.NewGuid().ToString("N"),sid,await SetupPaths.HashAsync(Path.Combine(package,"bundle.json"),token));
        var path=UpdateReviewHandoff.RequestPath(Folder,request.Id);await WriteNewAsync(path,request,token);
        var hash=await SetupPaths.HashAsync(path,token);
        var helper=Environment.ProcessPath??throw new IOException("The setup helper path is unavailable.");SetupPaths.RequireNoLinks(helper);
        var start=new ProcessStartInfo(helper){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=package};
        start.ArgumentList.Add("--prepare-update-approved");start.ArgumentList.Add(path);start.ArgumentList.Add(hash);
        using var child=Process.Start(start)??throw new IOException("The administrator review did not start.");
        // An approved read-only helper has its own three-minute bound. Closing the
        // console does not turn a timeout into a guessed completion or kill another process.
        await child.WaitForExitAsync(token);
        var resultPath=UpdateReviewHandoff.ResultPath(Folder,request.Id);SetupPaths.RequireNoLinks(resultPath);
        if(!File.Exists(resultPath))throw new InvalidOperationException("The review could not return to the original studio Windows account. Approve it with that same account; no service was changed.");
        var result=await SetupPaths.ReadAsync<UpdateReviewResult>(resultPath,524288,token);
        var review=UpdateReviewHandoff.ReadResult(request,result);
        if(child.ExitCode!=0)throw new InvalidOperationException("The administrator review did not complete. No service was changed.");
        return review;
    }
    internal static async Task<int> RunAsync(string path,string hash,CancellationToken token)
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new UnauthorizedAccessException("The Windows operator is unavailable.");
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Approve Windows administrator access for this read-only review.");
        SetupPaths.RequireNoLinks(Folder);SetupPaths.RequireNoLinks(path);
        _=UpdateReviewHandoff.IdFromRequestPath(path,Folder);
        if(new FileInfo(path).Length>4096)throw new IOException("The review request exceeds its bound.");
        var request=await SetupPaths.ReadVerifiedAsync<UpdateReviewRequest>(path,hash,token);
        var package=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        UpdateReviewHandoff.Validate(request,path,Folder,sid,await SetupPaths.HashAsync(Path.Combine(package,"bundle.json"),token));
        var output=UpdateReviewHandoff.ResultPath(Folder,request.Id);SetupPaths.RequireNoLinks(output);
        if(File.Exists(output))throw new InvalidOperationException("This review request already has a retained result. Read it before preparing another review.");
        UpdateReviewResult result;
        try{result=new(request.Id,await AppUpdatePreparation.PrepareAsync(package,token),null);}
        catch(Exception error){result=new(request.Id,null,FailureMessage(error));}
        await WriteNewAsync(output,result,CancellationToken.None);return result.Error is null?0:1;
    }
    internal static string FailureMessage(Exception error)=>error switch
    {
        Win32Exception{NativeErrorCode:1223}=>"Windows administrator approval was cancelled. No service was changed.",
        Win32Exception{NativeErrorCode:5} or UnauthorizedAccessException=>"Windows denied access while verifying the service or package. Use the original studio Windows account and its administrator approval. No service was changed.",
        OperationCanceledException=>"The read-only review did not finish within its deadline. No service was changed. Read the retained result before retrying.",
        ArgumentException or InvalidOperationException or IOException=>error.Message,
        _=>"The read-only update review failed ("+error.GetType().Name+", "+error.HResult+"). No service was changed."
    };
    private static async Task WriteNewAsync<T>(string path,T value,CancellationToken token)
    {
        SetupPaths.RequireNoLinks(path);var temporary=path+".partial-"+Guid.NewGuid().ToString("N");SetupPaths.RequireNoLinks(temporary);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>524288)throw new IOException("The review metadata exceeds its bound.");
        await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
        {await file.WriteAsync(bytes,token);await file.FlushAsync(token);file.Flush(true);}File.Move(temporary,path,false);
    }
}
