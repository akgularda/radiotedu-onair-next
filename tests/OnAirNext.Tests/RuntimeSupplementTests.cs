using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class RuntimeSupplementTests
{
    private static readonly string[] Paths=["bin/ffmpeg.exe","bin/ffprobe.exe","bin/libfdk-aac-2.dll","windows-codec.lock.json",
        "Licenses/COPYING.LGPLv2.1","Licenses/FDK-AAC-NOTICE.txt","Licenses/LICENSE.md","Sources/build-configurations.json",
        "Sources/build-windows-codecs.py","Sources/fdk-v2.0.3.tar.gz","Sources/ffmpeg-config.mak","Sources/ffmpeg-n9.0.2.tar.gz","Sources/toolchain.txt"];
    private sealed record Fixture(string Root,SetupPlan Initial,byte[] Manifest,RuntimeSupplementPlan Plan)
    {public string InitialHash=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Initial)));public string Receipt=>Path.Combine(Root,"receipt.json");public string Installed=>Path.Combine(Root,"installed");}
    private static SetupFile File(string path)
    {var bytes=Encoding.UTF8.GetBytes("Synthetic inventory only: "+path);return new(path,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)));}
    private static Fixture Create()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var entries=Paths.Select(File).ToArray();var raw=JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,dependencyLockSha256=File("windows-codec.lock.json").Sha256,buildScriptSha256=File("Sources/build-windows-codecs.py").Sha256,files=entries.Select(x=>new{path=x.Path,bytes=x.Bytes,sha256=x.Sha256})});
        var complete=CodecRuntimePackage.ParseReference(raw);var original=complete.Where(f=>f.Path is "bin/ffmpeg.exe" or "bin/libfdk-aac-2.dll").Select(f=>f with{Path=SetupPaths.RuntimeDestination(f.Path)}).ToArray();
        var initial=new SetupPlan(Guid.NewGuid().ToString("N"),"fixture-operator",Path.Combine(root,"package"),new('A',64),Path.Combine(root,"provider","bin"),original,false);
        var initialHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(initial)));
        var plan=new RuntimeSupplementPlan(1,Guid.NewGuid().ToString("N"),initial.OperatorSid,initial.BundleRoot,new('B',64),initial.BundleSha256,Path.Combine(root,"provider"),CodecRuntimePackage.ReferencePath,Convert.ToHexString(SHA256.HashData(raw)),initialHash,new('C',64),new('D',64),complete,DateTimeOffset.UtcNow);
        return new(root,initial,raw,plan);
    }
    private static async Task ReceiptAsync(Fixture f,string phase,RuntimeSupplementPlan? plan=null)
    {plan??=f.Plan;await System.IO.File.WriteAllBytesAsync(f.Receipt,JsonSerializer.SerializeToUtf8Bytes(new RuntimeSupplementReceipt(RuntimeSupplementInventory.HashPlan(plan),plan,phase,DateTimeOffset.UtcNow)));}
    private static async Task ManifestAsync(Fixture f,byte[]? bytes=null)
    {var path=Path.Combine(f.Installed,"tools","runtime-manifest.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);await System.IO.File.WriteAllBytesAsync(path,bytes??f.Manifest);}
    [Fact] public void Previously_qualified_legacy_reference_has_exact_source_bytes()
    {
        var bytes=System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"RuntimeReferences","b39ffcb9.json"));
        Assert.Equal(RuntimeSupplementInventory.LegacyReferenceSha256,Convert.ToHexString(SHA256.HashData(bytes)));Assert.Equal(20,CodecRuntimePackage.ParseReference(bytes).Length);
    }
    [Fact] public void Only_missing_tools_are_additions_and_original_plan_bytes_stay_unchanged()
    {
        var f=Create();var before=JsonSerializer.SerializeToUtf8Bytes(f.Initial);var additions=RuntimeSupplementInventory.Additions(f.Initial.RuntimeFiles,f.Plan.RuntimeFiles);
        Assert.Equal(12,additions.Length);Assert.Contains(additions,x=>x.Path=="bin/ffprobe.exe");Assert.Contains(additions,x=>x.Path=="Sources/ffmpeg-n9.0.2.tar.gz");
        Assert.DoesNotContain(additions,x=>x.Path=="bin/ffmpeg.exe"||x.Path=="bin/libfdk-aac-2.dll");Assert.Equal(before,JsonSerializer.SerializeToUtf8Bytes(f.Initial));
    }
    [Fact] public void A_changed_original_binary_cannot_be_silently_replaced()
    {
        var f=Create();var changed=f.Initial.RuntimeFiles.ToArray();changed[0]=changed[0] with{Sha256=new('F',64)};
        Assert.Throws<ArgumentException>(()=>RuntimeSupplementInventory.Additions(changed,f.Plan.RuntimeFiles));
    }
    [Fact] public async Task A_complete_inventory_does_not_create_an_empty_operation()
    {
        var f=Create();var writes=0;Assert.Empty(RuntimeSupplementInventory.Additions(f.Plan.RuntimeFiles,f.Plan.RuntimeFiles));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>RuntimeSupplementTransaction.CompleteAsync(f.Plan,f.Plan.RuntimeFiles,_=>{writes++;return Task.CompletedTask;},_=>Task.CompletedTask,()=>Task.CompletedTask));Assert.Equal(0,writes);
    }
    [Fact] public async Task Interrupted_copy_is_pending_and_retry_never_writes_an_original_tool()
    {
        var f=Create();var original=SetupPaths.InstalledRuntimeFiles(f.Initial.RuntimeFiles);var installed=original.ToDictionary(x=>x.Path,x=>x,StringComparer.OrdinalIgnoreCase);
        var receipts=new List<RuntimeSupplementReceipt>();var added=0;var fail=true;
        Task Persist(RuntimeSupplementReceipt value){receipts.Add(value);return Task.CompletedTask;}
        Task Copy(SetupFile file)
        {
            Assert.DoesNotContain(original,x=>x.Path==SetupPaths.RuntimeDestination(file.Path));Assert.Equal("Pending",receipts[^1].Phase);
            if(fail&&added==2){fail=false;throw new IOException("Injected copy interruption.");}
            var mapped=file with{Path=SetupPaths.RuntimeDestination(file.Path)};
            if(installed.TryGetValue(mapped.Path,out var existing))Assert.Equal(existing,mapped);else{installed.Add(mapped.Path,mapped);added++;}return Task.CompletedTask;
        }
        Task Verify(){Assert.Equal(f.Plan.RuntimeFiles.Length,installed.Count);return Task.CompletedTask;}
        await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementTransaction.CompleteAsync(f.Plan,f.Initial.RuntimeFiles,Persist,Copy,Verify));
        Assert.Equal("Pending",receipts[^1].Phase);Assert.Equal(original.Length+2,installed.Count);
        await RuntimeSupplementTransaction.CompleteAsync(f.Plan,f.Initial.RuntimeFiles,Persist,Copy,Verify);
        Assert.Equal("Complete",receipts[^1].Phase);Assert.All(original,x=>Assert.Same(x,installed[x.Path]));Assert.Equal(12,added);
    }
    [Fact] public async Task Failed_readback_never_commits_completion()
    {
        var f=Create();var receipts=new List<RuntimeSupplementReceipt>();
        await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementTransaction.CompleteAsync(f.Plan,f.Initial.RuntimeFiles,x=>{receipts.Add(x);return Task.CompletedTask;},_=>Task.CompletedTask,()=>throw new IOException("Readback failed.")));
        Assert.Single(receipts);Assert.Equal("Pending",receipts[0].Phase);
    }
    [Fact] public async Task Cancellation_after_one_addition_keeps_pending_intent()
    {
        var f=Create();using var stop=new CancellationTokenSource();var receipts=new List<RuntimeSupplementReceipt>();var copied=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>RuntimeSupplementTransaction.CompleteAsync(f.Plan,f.Initial.RuntimeFiles,x=>{receipts.Add(x);return Task.CompletedTask;},_=>{copied++;stop.Cancel();return Task.CompletedTask;},()=>Task.CompletedTask,stop.Token));
        Assert.Equal(1,copied);Assert.Single(receipts);Assert.Equal("Pending",receipts[0].Phase);
    }
    [Fact] public async Task Legacy_flat_inventory_remains_usable_without_a_supplement()
    {
        var f=Create();var files=await RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,f.Installed,_=>throw new Exception("No retained file should be opened."));
        Assert.Equal(f.Initial.RuntimeFiles,files);
    }
    [Fact] public async Task Pending_additions_block_application_image_maintenance()
    {
        var f=Create();await ReceiptAsync(f,"Pending");await Assert.ThrowsAsync<InvalidOperationException>(()=>RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,f.Installed,_=>{}));
    }
    [Fact] public async Task Completed_receipt_requires_its_exact_installed_manifest()
    {
        var f=Create();await ReceiptAsync(f,"Complete");await ManifestAsync(f,Encoding.UTF8.GetBytes("changed"));
        await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,f.Installed,_=>{}));
    }
    [Fact] public async Task Completed_receipt_cannot_be_reused_for_another_initial_claim()
    {
        var f=Create();await ReceiptAsync(f,"Complete");await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementInventory.EffectiveAsync(f.Initial,new('F',64),f.Receipt,f.Installed,_=>{}));
    }
    [Fact] public async Task Foreign_operator_receipt_cannot_extend_the_initial_inventory()
    {
        var f=Create();await ReceiptAsync(f,"Complete",f.Plan with{OperatorSid="other-operator"});await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,f.Installed,_=>{}));
    }
    [Fact] public async Task Tampered_receipt_plan_is_rejected_before_use()
    {
        var f=Create();var receipt=new RuntimeSupplementReceipt(RuntimeSupplementInventory.HashPlan(f.Plan),f.Plan with{Id=Guid.NewGuid().ToString("N")},"Complete",DateTimeOffset.UtcNow);
        await System.IO.File.WriteAllBytesAsync(f.Receipt,JsonSerializer.SerializeToUtf8Bytes(receipt));await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementInventory.ReadReceiptAsync(f.Receipt,_=>{}));
    }
    [Theory] [InlineData("Failed")] [InlineData("Unknown")]
    public async Task Unknown_receipt_states_fail_closed(string phase)
    {var f=Create();await ReceiptAsync(f,phase);await Assert.ThrowsAsync<IOException>(()=>RuntimeSupplementInventory.ReadReceiptAsync(f.Receipt,_=>{}));}
    [Fact] public async Task Completed_inventory_supports_both_update_images_without_rewriting_initial_plan()
    {
        var f=Create();var before=JsonSerializer.SerializeToUtf8Bytes(f.Initial);await ReceiptAsync(f,"Complete");await ManifestAsync(f);var protectedReads=new List<string>();
        var effective=await RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,f.Installed,protectedReads.Add);
        Assert.Equal(f.Plan.RuntimeFiles.Length,effective.Length);Assert.Contains(effective,x=>x.Path=="ffprobe.exe");Assert.DoesNotContain(effective,x=>x.Path.StartsWith("bin/",StringComparison.Ordinal));
        _=new UpdateSwap(f.Installed,Guid.NewGuid().ToString("N"),new(new('A',64),effective),new(new('B',64),effective.ToArray()));
        Assert.Equal(before,JsonSerializer.SerializeToUtf8Bytes(f.Initial));Assert.Contains(f.Receipt,protectedReads);Assert.Contains(Path.Combine(f.Installed,"tools","runtime-manifest.json"),protectedReads);
    }
    [Fact] public async Task Completed_inventory_remains_readable_during_the_release_rename_gap()
    {
        var f=Create();await ReceiptAsync(f,"Complete");await ManifestAsync(f);
        var runtime=SetupPaths.InstalledRuntimeFiles(f.Plan.RuntimeFiles);var swap=new UpdateSwap(f.Installed,Guid.NewGuid().ToString("N"),new(new('A',64),runtime),new(new('B',64),runtime));
        Assert.Equal(f.Installed,swap.RuntimeInventoryRoot());
        // Both reviewed paths share the freshly created private fixture parent.
        Assert.Equal(f.Root,Path.GetDirectoryName(swap.PreviousRoot));Assert.Equal(f.Root,Path.GetDirectoryName(swap.InstallRoot));
        Directory.CreateDirectory(swap.CandidateRoot);Directory.Move(swap.InstallRoot,swap.PreviousRoot);
        Assert.Equal(swap.PreviousRoot,swap.RuntimeInventoryRoot());
        var effective=await RuntimeSupplementInventory.EffectiveAsync(f.Initial,f.InitialHash,f.Receipt,swap.RuntimeInventoryRoot(),_=>{});
        Assert.Equal(runtime,effective);Assert.False(Directory.Exists(swap.InstallRoot));
    }
    [Fact] public async Task A_current_installation_file_cannot_be_hidden_by_a_retained_image()
    {
        var f=Create();var runtime=SetupPaths.InstalledRuntimeFiles(f.Plan.RuntimeFiles);var swap=new UpdateSwap(f.Installed,Guid.NewGuid().ToString("N"),new(new('A',64),runtime),new(new('B',64),runtime));
        Directory.CreateDirectory(swap.PreviousRoot);Directory.CreateDirectory(swap.CandidateRoot);await System.IO.File.WriteAllTextAsync(swap.InstallRoot,"unrelated");
        Assert.Throws<IOException>(()=>swap.RuntimeInventoryRoot());
    }
}
