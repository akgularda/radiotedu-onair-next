using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class UpdateMaintenanceLockTests
{
    [Fact]
    public async Task ReadOnlyProbeDetectsAnOwnedLeaseAndCancellationDoesNotReplaceTheLockFile()
    {
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"setup.lock");var original=new byte[]{11,22,33};await File.WriteAllBytesAsync(path,original);
        Assert.False(UpdateMaintenanceLock.IsHeld(path));
        await using(var held=await UpdateMaintenanceLock.AcquireAsync(path))
        {
            Assert.True(UpdateMaintenanceLock.IsHeld(path));
            using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>UpdateMaintenanceLock.AcquireAsync(path,cancelled.Token));
        }
        Assert.False(UpdateMaintenanceLock.IsHeld(path));var actual=await File.ReadAllBytesAsync(path);Assert.Equal(original,actual);
    }
    [Fact]
    public void StatusProbeNeverCreatesAMissingInstallationLock()
    {
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"missing.lock");Assert.Throws<FileNotFoundException>(()=>UpdateMaintenanceLock.IsHeld(path));Assert.False(File.Exists(path));
    }
}
