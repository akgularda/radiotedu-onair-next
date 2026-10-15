using System.Security.Cryptography;
using System.Text.Json;

namespace OnAirNext.Installation;

public sealed record SetupFile(string Path,long Bytes,string Sha256);
public sealed record SetupBundle(string Source,SetupFile[] Files);
public sealed record SetupPlan(string Id,string OperatorSid,string BundleRoot,string BundleSha256,string RuntimeRoot,SetupFile[] RuntimeFiles,bool StartService);
public sealed record SetupReceipt(string PlanId,string Phase,string Detail,string? Source=null,bool ServiceStarted=false);
public sealed record SetupRequest(string RuntimeFolder,bool StartService=true);

public static class SetupPaths
{
    public const long MaximumBundleBytes=1536L*1024*1024;
    public static readonly string[] StationIds=["1","2","4","5","8","9","10","11"];
    public static string CanonicalRoot(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new ArgumentException("Choose a local installation folder.");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),StringComparison.OrdinalIgnoreCase)||full.StartsWith(@"\\",StringComparison.Ordinal)||full.StartsWith("//",StringComparison.Ordinal))
            throw new ArgumentException("Choose a local subfolder, rather than a drive or network share.");
        RequireNoLinks(full);return full;
    }
    public static void RequireNoLinks(string path)
    {
        for(var item=Path.GetFullPath(path);!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
        {
            try{if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new IOException("Installation paths cannot traverse links.");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
    }
    public static string Resolve(string root,string relative)
    {
        if(string.IsNullOrEmpty(relative)||relative.Length>240||relative.Contains('\\')||relative.Contains(':')||relative.StartsWith('/')||relative.Split('/').Any(p=>p.Length==0||p is "." or ".."||p.TrimEnd(' ','.')!=p||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Reserved(p)||p.Any(char.IsControl)))
            throw new ArgumentException("Invalid package file path.");
        var full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,comparison))throw new IOException("Package path escaped its folder.");
        RequireNoLinks(full);return full;
    }
    private static bool Reserved(string segment)
    {
        var name=segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" || name.Length==4&&(name.StartsWith("COM",StringComparison.Ordinal)||name.StartsWith("LPT",StringComparison.Ordinal))&&name[3] is >= '1' and <= '9';
    }
    public static void ValidateFiles(SetupFile[] files,bool bundle)
    {
        if(files is null||files.Length is <1 or >2000||!bundle&&files.Length>64)throw new ArgumentException("The package file list is unavailable or too large.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var file in files)
        {
            if(file is null||file.Path is null||file.Sha256 is null||file.Bytes<=0||file.Bytes>MaximumBundleBytes||file.Sha256.Length!=64||!file.Sha256.All(Uri.IsHexDigit))throw new ArgumentException("Invalid package file identity.");
            if(!names.Add(file.Path))throw new ArgumentException("Duplicate package file path.");
            if(bundle&&!new[]{"bin/","console/","setup/"}.Any(prefix=>file.Path.StartsWith(prefix,StringComparison.Ordinal)))throw new ArgumentException("Unexpected package component.");
            if(!bundle&&(file.Path.Contains('/')||!(file.Path.Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase)||file.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||file.Path.StartsWith("LICENSE",StringComparison.OrdinalIgnoreCase)||file.Path.StartsWith("COPYING",StringComparison.OrdinalIgnoreCase))))throw new ArgumentException("Unexpected runtime file.");
            total+=file.Bytes;if(total>MaximumBundleBytes)throw new ArgumentException("Package exceeds the installation size limit.");
        }
        if(!bundle&&!files.Any(f=>f.Path.Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Choose a folder containing ffmpeg.exe.");
    }
    public static async Task<string> HashAsync(string path,CancellationToken token=default)
    {
        RequireNoLinks(path);await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input,token));
    }
    public static async Task<T> ReadAsync<T>(string path,int maximum=524288,CancellationToken token=default)
    {
        RequireNoLinks(path);await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        if(stream.Length>maximum)throw new ArgumentException("The setup document exceeds its size limit.");
        return await JsonSerializer.DeserializeAsync<T>(stream,cancellationToken:token)??throw new ArgumentException("The setup document is unreadable.");
    }
    public static async Task<T> ReadVerifiedAsync<T>(string path,string expected,CancellationToken token)
    {
        RequireNoLinks(path);await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        if(stream.Length is <1 or >524288)throw new ArgumentException("Setup document size is invalid.");
        var bytes=new byte[(int)stream.Length];await stream.ReadExactlyAsync(bytes,token);
        if(expected is null||!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("The reviewed setup document changed.");
        return JsonSerializer.Deserialize<T>(bytes)??throw new ArgumentException("The setup document is unreadable.");
    }
    public static async Task<SetupBundle> VerifyBundleAsync(string root,string expected,CancellationToken token)
    {
        root=CanonicalRoot(root);var manifest=Resolve(root,"bundle.json");
        if(!string.Equals(await HashAsync(manifest,token),expected,StringComparison.OrdinalIgnoreCase))throw new IOException("The package changed after review. Prepare setup again.");
        var bundle=await ReadVerifiedAsync<SetupBundle>(manifest,expected,token);ValidateFiles(bundle.Files,true);
        if(bundle.Source is null||bundle.Source.Length!=40||!bundle.Source.All(Uri.IsHexDigit))throw new ArgumentException("The package build version is unavailable.");
        foreach(var item in bundle.Files)
        {
            var source=Resolve(root,item.Path);
            if(new FileInfo(source).Length!=item.Bytes||!string.Equals(await HashAsync(source,token),item.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("A package file changed or is incomplete.");
        }
        foreach(var required in new[]{"bin/OnAirNext.Host.exe","console/OnAirNext.Web.exe","setup/OnAirNext.Setup.exe"})if(!bundle.Files.Any(f=>f.Path==required))throw new ArgumentException("The installation package is incomplete.");
        return bundle;
    }
}
