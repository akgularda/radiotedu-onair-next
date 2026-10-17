namespace OnAirNext.Installation;

public sealed record UpdateReviewRequest(string Id,string OperatorSid,string PackageSha256);
public sealed record UpdateReviewResult(string Id,AppUpdateReview? Review,string? Error);

// Metadata only, beneath the original operator's private console-access directory.
public static class UpdateReviewHandoff
{
    public static string RequestPath(string directory,string id)=>Resolve(directory,id,".request.json");
    public static string ResultPath(string directory,string id)=>Resolve(directory,id,".result.json");
    private static string Resolve(string directory,string id,string suffix)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("The review request identity is invalid.");
        return Path.Combine(Path.GetFullPath(directory),id+suffix);
    }
    public static string IdFromRequestPath(string path,string directory)
    {
        var name=Path.GetFileName(path);const string suffix=".request.json";
        if(name.Length!=32+suffix.Length||!name.EndsWith(suffix,StringComparison.Ordinal))throw new ArgumentException("Use a retained private review request.");
        var id=name[..32];
        if(!Path.GetFullPath(path).Equals(RequestPath(directory,id),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Use the original operator's private review request.");
        return id;
    }
    public static void Validate(UpdateReviewRequest request,string path,string directory,string operatorSid,string packageSha256)
    {
        if(request is null||!Guid.TryParseExact(request.Id,"N",out _)||string.IsNullOrWhiteSpace(request.OperatorSid)||request.OperatorSid.Length>184||
            request.PackageSha256 is null||request.PackageSha256.Length!=64||!request.PackageSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The update review request is invalid.");
        if(IdFromRequestPath(path,directory)!=request.Id)throw new ArgumentException("Use the original operator's private review request.");
        if(request.OperatorSid!=operatorSid)throw new InvalidOperationException("Approve this review with the original studio Windows account.");
        if(!request.PackageSha256.Equals(packageSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The reviewed package changed before Windows approval.");
    }
    public static AppUpdateReview ReadResult(UpdateReviewRequest request,UpdateReviewResult result)
    {
        if(result.Id!=request.Id)throw new IOException("The administrator review result has another request identity.");
        if(result.Error is not null)throw new InvalidOperationException(result.Error);
        var review=result.Review??throw new IOException("The administrator review result is missing.");
        if(review.OperatorSid!=request.OperatorSid||!review.Files.Replacement.BundleSha256.Equals(request.PackageSha256,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The administrator review does not match this operator and package.");
        return review;
    }
}
