using System.Security.Cryptography;
using System.Text.Json;

namespace OnAirNext.Installation;

public sealed record RuntimeSupplementRequest(string RuntimeFolder);
public sealed record RuntimeSupplementPlan(int SchemaVersion,string Id,string OperatorSid,string PackageRoot,string PackageSha256,
    string InstalledBundleSha256,string RuntimeRoot,string ReferencePath,string ReferenceSha256,string InitialPlanSha256,
    string OwnershipSha256,string ConfigurationSha256,SetupFile[] RuntimeFiles,DateTimeOffset ReviewedAtUtc);
public sealed record RuntimeSupplementReceipt(string PlanSha256,RuntimeSupplementPlan Plan,string Phase,DateTimeOffset AtUtc,string? Detail=null);

// An additional receipt never changes the original seven-field installation claim.
public static class RuntimeSupplementInventory
{
    public const string ReceiptName="audio-tools-supplement.json";
    public const string LegacyReferencePath="setup/RuntimeReferences/b39ffcb9.json";
    public const string LegacyReferenceSha256="2FF9772ED74AF0AB3A2C6B7CCED4FCAA23F09BB2D28205B5DB863AA919A629F2";
    public static bool Same(SetupFile[] left,SetupFile[] right)=>left.Length==right.Length&&left.All(f=>right.Any(n=>Identity(f,n)));
    private static bool Identity(SetupFile left,SetupFile right)=>left.Path.Equals(right.Path,StringComparison.OrdinalIgnoreCase)&&left.Bytes==right.Bytes&&left.Sha256.Equals(right.Sha256,StringComparison.OrdinalIgnoreCase);
    public static SetupFile[] Additions(SetupFile[] original,SetupFile[] provider)
    {
        var before=SetupPaths.InstalledRuntimeFiles(original);var after=SetupPaths.InstalledRuntimeFiles(provider);
        if(before.Any(f=>!after.Any(n=>Identity(f,n))))throw new ArgumentException("This package does not match every original audio tool. Choose the original matching provider; codec replacement is not permitted.");
        return provider.Where(f=>!before.Any(n=>n.Path.Equals(SetupPaths.RuntimeDestination(f.Path),StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    public static string HashPlan(RuntimeSupplementPlan plan)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
    public static void ValidatePlan(RuntimeSupplementPlan plan)
    {
        if(plan.SchemaVersion!=1||!Guid.TryParseExact(plan.Id,"N",out _)||string.IsNullOrWhiteSpace(plan.OperatorSid)||
            plan.ReferencePath is not (CodecRuntimePackage.ReferencePath or LegacyReferencePath)||plan.ReviewedAtUtc==default||plan.ReviewedAtUtc.Offset!=TimeSpan.Zero)
            throw new ArgumentException("The audio tools review is invalid.");
        foreach(var hash in new[]{plan.PackageSha256,plan.InstalledBundleSha256,plan.ReferenceSha256,plan.InitialPlanSha256,plan.OwnershipSha256,plan.ConfigurationSha256})
            if(hash is null||hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new ArgumentException("The audio tools review has an invalid file identity.");
        foreach(var path in new[]{plan.PackageRoot,plan.RuntimeRoot})
        {
            if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||path.StartsWith("//",StringComparison.Ordinal))throw new ArgumentException("The reviewed tools folder is invalid.");
            var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if(full==Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!))throw new ArgumentException("The reviewed tools folder cannot be a drive root.");
        }
        _=SetupPaths.InstalledRuntimeFiles(plan.RuntimeFiles);
        if(plan.ReferencePath==LegacyReferencePath&&!plan.ReferenceSha256.Equals(LegacyReferenceSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("The legacy audio tools reference is not the qualified original build.");
        if(!plan.RuntimeFiles.Any(f=>f.Path=="runtime-manifest.json"&&f.Sha256.Equals(plan.ReferenceSha256,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("The reviewed provider manifest is missing.");
    }
    public static async Task<RuntimeSupplementReceipt?> ReadReceiptAsync(string path,Action<string> assertProtected,CancellationToken token=default)
    {
        SetupPaths.RequireNoLinks(path);if(!File.Exists(path))return null;assertProtected(path);
        var saved=await SetupPaths.ReadAsync<RuntimeSupplementReceipt>(path,524288,token);
        if(saved.Plan is null||saved.PlanSha256 is null)throw new IOException("The retained audio tools receipt is incomplete.");ValidatePlan(saved.Plan);
        if(!saved.PlanSha256.Equals(HashPlan(saved.Plan),StringComparison.OrdinalIgnoreCase)||saved.Phase is not ("Pending" or "Complete")||saved.AtUtc==default||saved.AtUtc.Offset!=TimeSpan.Zero)
            throw new IOException("The retained audio tools receipt is invalid.");
        return saved;
    }
    public static async Task<SetupFile[]> EffectiveAsync(SetupPlan initial,string initialHash,string receiptPath,string installRoot,Action<string> assertProtected,CancellationToken token=default)
    {
        var original=SetupPaths.InstalledRuntimeFiles(initial.RuntimeFiles);var receipt=await ReadReceiptAsync(receiptPath,assertProtected,token);
        if(receipt is null)return original;
        if(receipt.Phase!="Complete")throw new InvalidOperationException("Complete the saved audio tools operation before reviewing an application update.");
        var plan=receipt.Plan;
        if(plan.OperatorSid!=initial.OperatorSid||!plan.InitialPlanSha256.Equals(initialHash,StringComparison.OrdinalIgnoreCase))throw new IOException("The audio tools receipt belongs to a different initial installation.");
        _=Additions(initial.RuntimeFiles,plan.RuntimeFiles);
        var manifest=SetupPaths.Resolve(installRoot,"tools/runtime-manifest.json");assertProtected(manifest);
        var raw=await File.ReadAllBytesAsync(RequireBoundedManifest(manifest),token);
        if(!Convert.ToHexString(SHA256.HashData(raw)).Equals(plan.ReferenceSha256,StringComparison.OrdinalIgnoreCase)||!Same(CodecRuntimePackage.ParseReference(raw),plan.RuntimeFiles))throw new IOException("The retained audio tools manifest no longer matches its approved receipt.");
        return SetupPaths.InstalledRuntimeFiles(plan.RuntimeFiles);
    }
    private static string RequireBoundedManifest(string path)
    {SetupPaths.RequireNoLinks(path);if(new FileInfo(path).Length is <1 or >524288)throw new IOException("The retained audio tools manifest exceeds its bound.");return path;}
}

public static class RuntimeSupplementTransaction
{
    public static async Task CompleteAsync(RuntimeSupplementPlan plan,SetupFile[] original,
        Func<RuntimeSupplementReceipt,Task> persist,Func<SetupFile,Task> copyAddition,Func<Task> verify,CancellationToken token=default)
    {
        RuntimeSupplementInventory.ValidatePlan(plan);var additions=RuntimeSupplementInventory.Additions(original,plan.RuntimeFiles);
        if(additions.Length==0)throw new InvalidOperationException("The matching audio tools inventory is already complete.");
        token.ThrowIfCancellationRequested();var hash=RuntimeSupplementInventory.HashPlan(plan);
        await persist(new(hash,plan,"Pending",DateTimeOffset.UtcNow,"Adding only reviewed missing files. Original codec binaries are retained."));
        foreach(var file in additions){token.ThrowIfCancellationRequested();await copyAddition(file);}
        token.ThrowIfCancellationRequested();await verify();
        await persist(new(hash,plan,"Complete",DateTimeOffset.UtcNow,"All reviewed audio tools were read back. Original codec files, initial ownership, service process and stopped channel configuration are unchanged."));
    }
}
