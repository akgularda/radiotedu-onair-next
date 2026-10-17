using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class CodecRuntimePackageTests
{
    private static readonly string[] Paths=["bin/ffmpeg.exe","bin/ffprobe.exe","bin/libfdk-aac-2.dll","windows-codec.lock.json",
        "Licenses/COPYING.LGPLv2.1","Licenses/FDK-AAC-NOTICE.txt","Licenses/LICENSE.md","Sources/build-configurations.json",
        "Sources/build-windows-codecs.py","Sources/fdk-v2.0.3.tar.gz","Sources/ffmpeg-config.mak","Sources/ffmpeg-n9.0.2.tar.gz","Sources/toolchain.txt"];
    private static byte[] Payload(string path)=>Encoding.UTF8.GetBytes("Synthetic inventory fixture only: "+path);
    private static SetupFile Item(string path)=>new(path,Payload(path).Length,Convert.ToHexString(SHA256.HashData(Payload(path))));
    private static byte[] Reference(SetupFile[]? files=null,string? scriptHash=null)=>JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,
        dependencyLockSha256=Item("windows-codec.lock.json").Sha256,buildScriptSha256=scriptHash??Item("Sources/build-windows-codecs.py").Sha256,
        files=(files??Paths.Select(Item).ToArray()).Select(x=>new{path=x.Path,bytes=x.Bytes,sha256=x.Sha256})});
    private static string Root(){var p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
    [Fact] public void Complete_reference_preserves_probe_notices_archives_and_manifest()
    {
        var bytes=Reference();var files=CodecRuntimePackage.ParseReference(bytes);Assert.Equal(Paths.Length+1,files.Length);
        var installed=SetupPaths.InstalledRuntimeFiles(files);Assert.Contains(installed,f=>f.Path=="ffprobe.exe");Assert.Contains(installed,f=>f.Path=="Sources/ffmpeg-n9.0.2.tar.gz");
        Assert.Contains(installed,f=>f.Path=="runtime-manifest.json"&&f.Sha256==Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Contains(files,f=>f.Path=="bin/ffmpeg.exe");
    }
    [Theory] [InlineData("bin/ffprobe.exe")] [InlineData("Licenses/FDK-AAC-NOTICE.txt")] [InlineData("Sources/fdk-v2.0.3.tar.gz")] [InlineData("Sources/ffmpeg-n9.0.2.tar.gz")]
    public void Partial_provider_cannot_pass_as_a_complete_package(string missing)=>Assert.Throws<ArgumentException>(()=>CodecRuntimePackage.ParseReference(Reference(Paths.Where(x=>x!=missing).Select(Item).ToArray())));
    [Fact] public void Source_build_identity_cannot_disagree_with_its_declared_file()=>Assert.Throws<ArgumentException>(()=>CodecRuntimePackage.ParseReference(Reference(scriptHash:new('F',64))));
    [Fact] public void Flattening_rejects_two_sources_for_the_same_installed_binary()=>Assert.Throws<ArgumentException>(()=>SetupPaths.InstalledRuntimeFiles([Item("bin/ffmpeg.exe"),Item("ffmpeg.exe")]));
    [Theory] [InlineData("Sources/../private.txt")] [InlineData("bin/unknown.exe")] [InlineData("private/credentials.json")] [InlineData("bin/runtimes/win-x64/native/e_sqlite3.dll")]
    public void Provider_cannot_introduce_unlisted_component_paths(string path)=>Assert.Throws<ArgumentException>(()=>CodecRuntimePackage.ParseReference(Reference(Paths.Select(Item).Append(Item(path)).ToArray())));
    [Fact] public void Existing_flat_plan_bytes_remain_the_same_after_runtime_mapping()
    {
        var plan=new SetupPlan(Guid.NewGuid().ToString("N"),"fixture-operator","package",new('A',64),"flat-runtime",[Item("ffmpeg.exe"),Item("support.dll")],false);
        var before=JsonSerializer.SerializeToUtf8Bytes(plan);var installed=SetupPaths.InstalledRuntimeFiles(plan.RuntimeFiles);
        Assert.Equal(before,JsonSerializer.SerializeToUtf8Bytes(plan));Assert.Equal(plan.RuntimeFiles,installed);
    }
    [Fact] public void Elevated_inventory_validation_rejects_truncated_or_changed_review()
    {
        var files=CodecRuntimePackage.ParseReference(Reference());CodecRuntimePackage.RequireInventory(files,files.Reverse().ToArray());
        Assert.Throws<ArgumentException>(()=>CodecRuntimePackage.RequireInventory(files[..^1],files));
        var changed=files.ToArray();changed[0]=changed[0] with{Sha256=new('B',64)};
        Assert.Throws<ArgumentException>(()=>CodecRuntimePackage.RequireInventory(changed,files));
    }
    [Fact] public async Task Actual_files_must_match_the_reference_before_any_runtime_execution()
    {
        var root=Root();foreach(var path in Paths){var full=SetupPaths.Resolve(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllBytesAsync(full,Payload(path));}
        var bytes=Reference();var reference=Path.Combine(Root(),"reference.json");await File.WriteAllBytesAsync(reference,bytes);
        var manifest=Path.Combine(root,"runtime-manifest.json");await File.WriteAllBytesAsync(manifest,bytes);var hash=await SetupPaths.HashAsync(reference);
        Assert.Equal(Paths.Length+1,(await CodecRuntimePackage.InspectAsync(root,reference,hash,CancellationToken.None)).Length);
        await File.WriteAllTextAsync(SetupPaths.Resolve(root,"Licenses/FDK-AAC-NOTICE.txt"),"changed");
        await Assert.ThrowsAsync<IOException>(()=>CodecRuntimePackage.InspectAsync(root,reference,hash,CancellationToken.None));
    }
    [Fact] public async Task A_changed_packaged_reference_is_rejected_before_provider_reads()
    {
        var reference=Path.Combine(Root(),"reference.json");await File.WriteAllBytesAsync(reference,Reference());var hash=await SetupPaths.HashAsync(reference);
        await File.WriteAllTextAsync(reference,"changed");await Assert.ThrowsAsync<IOException>(()=>CodecRuntimePackage.InspectAsync(Root(),reference,hash,CancellationToken.None));
    }
}
