using System.Security.Cryptography;
using System.Text.Json;

namespace OnAirNext.Installation;

public enum UpdateDirection { Apply, Rollback }
public sealed record UpdateSwapReview(string Id,UpdateImage Original,UpdateImage Replacement,DateTimeOffset CreatedAtUtc);
public sealed record UpdateIntent(string ReviewSha256,UpdateDirection Direction,string Phase,long Revision,DateTimeOffset UpdatedAtUtc);

// The elevated installer supplies an existing protected operation folder. This journal
// is separate from engine data and must be held together with the global setup lock.
public sealed class UpdateOperationStore
{
    private readonly string _install,_folder;
    public UpdateOperationStore(string trustedInstallRoot,string protectedOperationFolder)
    {
        _install=SetupPaths.CanonicalRoot(trustedInstallRoot);_folder=SetupPaths.CanonicalRoot(protectedOperationFolder);
        if(!Directory.Exists(_folder))throw new DirectoryNotFoundException("Create the protected update record folder first.");
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(_folder.Equals(_install,comparison)||_folder.StartsWith(_install+Path.DirectorySeparatorChar,comparison))throw new ArgumentException("Update records must be outside the application being replaced.");
    }
    private string FilePath(string id,string suffix)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Choose a saved update operation.");
        return SetupPaths.Resolve(_folder,id+suffix);
    }
    public async Task<string> SaveReviewAsync(UpdateSwapReview review,CancellationToken token=default)
    {
        if(review is null||review.CreatedAtUtc==default||review.CreatedAtUtc.Offset!=TimeSpan.Zero)throw new ArgumentException("The reviewed update timestamp is invalid.");
        _=new UpdateSwap(_install,review.Id,review.Original,review.Replacement);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(review);var path=FilePath(review.Id,".review.json");
        if(bytes.Length>524288)throw new ArgumentException("The update review is too large.");
        await using var held=Lock(review.Id);
        if(File.Exists(path))
        {
            var existing=await SetupPaths.ReadVerifiedAsync<UpdateSwapReview>(path,Convert.ToHexString(SHA256.HashData(bytes)),token);
            if(existing.Id!=review.Id)throw new IOException("The saved review has a different operation identity.");
        }
        else await WriteAtomicAsync(path,bytes,false,token);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private FileStream Lock(string id)
    {
        var path=FilePath(id,".lock");SetupPaths.RequireNoLinks(path);
        return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public async Task<UpdateIntent?> ReadIntentAsync(string id,string expectedReview,CancellationToken token=default)
    {
        var path=FilePath(id,".intent.json");if(!File.Exists(path))return null;
        var intent=await SetupPaths.ReadAsync<UpdateIntent>(path,32768,token);
        if(intent.ReviewSha256 is null||!intent.ReviewSha256.Equals(expectedReview,StringComparison.OrdinalIgnoreCase)||!Enum.IsDefined(intent.Direction)||intent.Revision<1||intent.UpdatedAtUtc==default||intent.UpdatedAtUtc.Offset!=TimeSpan.Zero||string.IsNullOrWhiteSpace(intent.Phase)||intent.Phase.Length>128)
            throw new IOException("The saved update direction does not match the reviewed operation.");
        return intent;
    }
    public async Task<UpdateIntent> RunAsync(string id,string expectedReview,UpdateDirection requested,
        Func<Task> requireStopped,Func<string,Task>? progress=null,CancellationToken token=default)
    {
        if(!Enum.IsDefined(requested))throw new ArgumentException("Choose update or rollback.");
        await using var held=Lock(id);
        var review=await SetupPaths.ReadVerifiedAsync<UpdateSwapReview>(FilePath(id,".review.json"),expectedReview,token);
        if(review.Id!=id)throw new IOException("The reviewed operation identity changed.");
        var swap=new UpdateSwap(_install,id,review.Original,review.Replacement);
        var prior=await ReadIntentAsync(id,expectedReview,token);
        // A retained rollback direction wins even when interrupted before its first move.
        // Reapplying after rollback needs a new review/operation, never a stale Apply retry.
        if(prior?.Direction==UpdateDirection.Rollback&&requested==UpdateDirection.Apply)throw new InvalidOperationException("This operation is rolling back or already rolled back. Prepare a new reviewed update.");
        var revision=prior?.Revision??0;UpdateIntent? latest=null;
        async Task Persist(string phase)
        {
            latest=new(expectedReview,requested,phase,checked(++revision),DateTimeOffset.UtcNow);
            await WriteAtomicAsync(FilePath(id,".intent.json"),JsonSerializer.SerializeToUtf8Bytes(latest),true,token);
            if(progress is not null)await progress(phase);
        }
        // This write is flushed before any filesystem transition, including retries.
        await Persist(requested==UpdateDirection.Apply?"Update requested":"Rollback requested");
        if(requested==UpdateDirection.Apply)await swap.ApplyAsync(requireStopped,Persist,token);
        else await swap.RollbackAsync(requireStopped,Persist,token);
        await Persist(requested==UpdateDirection.Apply?"Application files updated":"Original application files restored");
        return latest!;
    }
    private static async Task WriteAtomicAsync(string path,byte[] bytes,bool replace,CancellationToken token)
    {
        SetupPaths.RequireNoLinks(path);
        var temporary=path+".partial-"+Guid.NewGuid().ToString("N");SetupPaths.RequireNoLinks(temporary);
        await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.WriteThrough))
        {await stream.WriteAsync(bytes,token);await stream.FlushAsync(token);stream.Flush(true);}
        // Incomplete records are retained for diagnosis; an orphan partial never becomes
        // authoritative simply because its timestamp is newer than the committed record.
        SetupPaths.RequireNoLinks(path);File.Move(temporary,path,replace);
    }
}
