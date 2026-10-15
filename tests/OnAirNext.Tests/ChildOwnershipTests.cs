using System.Diagnostics;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ChildOwnershipTests
{
    [Fact]
    public async Task CleanupRejectsAnUnownedProcessObjectBeforeAttemptingToStopIt()
    {
        using var process = new Process();
        await Assert.ThrowsAsync<ArgumentException>(() => ChildProcess.StopAsync(process));
    }
}
