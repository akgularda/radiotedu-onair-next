using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class SetupPackageTests
{
    private static string Root(){var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    [Theory]
    [InlineData("../host.exe")][InlineData("bin/../../host.exe")][InlineData("/bin/host.exe")][InlineData("bin//host.exe")][InlineData("bin/host.exe ")][InlineData("bin/host.exe:stream")][InlineData("bin/CON.dll")][InlineData("bin/LPT1.dll")]
    public void PackagePathsCannotEscapeOrUseAmbiguousNames(string path)=>Assert.Throws<ArgumentException>(()=>SetupPaths.Resolve(Root(),path));
    [Fact]
    public void FileAdmissionRejectsDuplicateComponentsAndMissingRuntime()
    {
        var hash=new string('A',64);var item=new SetupFile("bin/app.dll",5,hash);
        Assert.Throws<ArgumentException>(()=>SetupPaths.ValidateFiles([item,item],true));
        Assert.Throws<ArgumentException>(()=>SetupPaths.ValidateFiles([item with{Path="outside/app.dll"}],true));
        Assert.Throws<ArgumentException>(()=>SetupPaths.ValidateFiles([new("support.dll",5,hash)],false));
        Assert.Throws<ArgumentException>(()=>SetupPaths.ValidateFiles([new("ffmpeg.exe",SetupPaths.MaximumBundleBytes+1,hash)],false));
        SetupPaths.ValidateFiles([new("ffmpeg.exe",5,hash),new("avcodec.dll",5,hash)],false);
    }
    [Fact]
    public async Task PinnedJsonIsDeserializedFromTheSameVerifiedBytes()
    {
        var path=Path.Combine(Root(),"review.json");await File.WriteAllTextAsync(path,"{\"RuntimeFolder\":\"fixture\",\"StartService\":false}");
        var hash=await SetupPaths.HashAsync(path);var request=await SetupPaths.ReadVerifiedAsync<SetupRequest>(path,hash,CancellationToken.None);Assert.False(request.StartService);
        await File.WriteAllTextAsync(path,"{\"RuntimeFolder\":\"changed\",\"StartService\":true}");
        await Assert.ThrowsAsync<IOException>(()=>SetupPaths.ReadVerifiedAsync<SetupRequest>(path,hash,CancellationToken.None));
    }
    [Fact]
    public async Task CompleteBundleRequiresEveryRequiredComponentAndExactFileBytes()
    {
        var root=Root();var files=new List<SetupFile>();
        foreach(var path in new[]{"bin/OnAirNext.Host.exe","console/OnAirNext.Web.exe","setup/OnAirNext.Setup.exe"})
        {
            var full=SetupPaths.Resolve(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllTextAsync(full,"fixture "+path);
            files.Add(new(path,new FileInfo(full).Length,await SetupPaths.HashAsync(full)));
        }
        var manifest=Path.Combine(root,"bundle.json");await File.WriteAllTextAsync(manifest,JsonSerializer.Serialize(new SetupBundle(new string('a',40),files.ToArray())));
        var hash=await SetupPaths.HashAsync(manifest);Assert.Equal(3,(await SetupPaths.VerifyBundleAsync(root,hash,CancellationToken.None)).Files.Length);
        await File.WriteAllTextAsync(SetupPaths.Resolve(root,files[0].Path),"tampered");
        await Assert.ThrowsAsync<IOException>(()=>SetupPaths.VerifyBundleAsync(root,hash,CancellationToken.None));
    }
}
