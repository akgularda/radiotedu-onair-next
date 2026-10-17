using System.Security.Cryptography;
using System.Text.Json;

namespace OnAirNext.Installation;

// The matching CI runtime reference belongs to the reviewed application bundle.
// No media, installed configuration or provider file is rewritten during inspection.
public static class CodecRuntimePackage
{
    public const string ReferencePath="setup/codec-runtime-reference.json";
    private static readonly string[] Required=["bin/ffmpeg.exe","bin/ffprobe.exe","bin/libfdk-aac-2.dll",
        "windows-codec.lock.json","Licenses/COPYING.LGPLv2.1","Licenses/FDK-AAC-NOTICE.txt","Licenses/LICENSE.md",
        "Sources/build-configurations.json","Sources/build-windows-codecs.py","Sources/fdk-v2.0.3.tar.gz",
        "Sources/ffmpeg-config.mak","Sources/ffmpeg-n9.0.2.tar.gz","Sources/toolchain.txt"];
    public static SetupFile[] ParseReference(byte[] bytes)
    {
        if(bytes.Length is <1 or >524288)throw new ArgumentException("The codec runtime reference exceeds its limit.");
        try
        {
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            if(root.GetProperty("schemaVersion").GetInt32()!=1)throw new ArgumentException("Unsupported codec runtime reference.");
            var entries=root.GetProperty("files");if(entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength() is <1 or >63)throw new ArgumentException("The codec runtime file list is invalid.");
            var files=entries.EnumerateArray().Select(x=>new SetupFile(x.GetProperty("path").GetString()!,x.GetProperty("bytes").GetInt64(),x.GetProperty("sha256").GetString()!)).ToArray();
            SetupPaths.ValidateFiles(files,false);_=SetupPaths.InstalledRuntimeFiles(files);
            if(files.Any(f=>f.Path=="runtime-manifest.json")||Required.Any(path=>!files.Any(f=>f.Path==path))||files.Any(f=>!f.Path.StartsWith("bin/",StringComparison.Ordinal)&&!f.Path.StartsWith("Licenses/",StringComparison.Ordinal)&&!f.Path.StartsWith("Sources/",StringComparison.Ordinal)&&f.Path!="windows-codec.lock.json"))
                throw new ArgumentException("Choose the complete matching codec package, including bin, Licenses and Sources.");
            foreach(var pair in new[]{("dependencyLockSha256","windows-codec.lock.json"),("buildScriptSha256","Sources/build-windows-codecs.py")})
                if(!string.Equals(root.GetProperty(pair.Item1).GetString(),files.Single(f=>f.Path==pair.Item2).Sha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("The codec source and build reference is inconsistent.");
            var result=files.Append(new SetupFile("runtime-manifest.json",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)))).ToArray();
            SetupPaths.ValidateFiles(result,false);return result;
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {throw new ArgumentException("The codec runtime reference is invalid or incomplete.",error);}
    }
    public static void RequireInventory(SetupFile[] reviewed,SetupFile[] inspected)
    {
        SetupPaths.ValidateFiles(reviewed,false);_=SetupPaths.InstalledRuntimeFiles(reviewed);
        if(reviewed.Length!=inspected.Length||reviewed.Any(file=>!inspected.Any(actual=>actual.Path==file.Path&&actual.Bytes==file.Bytes&&actual.Sha256.Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("The reviewed runtime does not match the complete codec package inventory.");
    }
    public static async Task<SetupFile[]> InspectAsync(string runtimeRoot,string reference,string expected,CancellationToken token)
    {
        runtimeRoot=SetupPaths.CanonicalRoot(runtimeRoot);SetupPaths.RequireNoLinks(reference);
        if(new FileInfo(reference).Length is <1 or >524288)throw new ArgumentException("The codec runtime reference is invalid.");
        var bytes=await File.ReadAllBytesAsync(reference,token);
        if(!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),expected,StringComparison.OrdinalIgnoreCase))throw new IOException("The packaged codec reference changed. Reopen the complete application package.");
        var files=ParseReference(bytes);var manifest=SetupPaths.Resolve(runtimeRoot,"runtime-manifest.json");
        if(!File.Exists(manifest))throw new ArgumentException("Select the complete matching codec runtime folder containing bin, Licenses and Sources; do not select bin alone.");
        foreach(var file in files)
        {
            token.ThrowIfCancellationRequested();var path=SetupPaths.Resolve(runtimeRoot,file.Path);
            if(!File.Exists(path)||new FileInfo(path).Length!=file.Bytes||!string.Equals(await SetupPaths.HashAsync(path,token),file.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The codec package is incomplete or differs from this build's reference. Keep its binaries, licenses and source archives together.");
        }
        return files;
    }
}
