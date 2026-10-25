using OnAirNext.ConsoleProtocol;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ConsoleStartupDiagnosticsTests
{
    private static ConsoleStartupDiagnostics Store() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "startup-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void StartupFailureShowsItsPhaseAndRootCodeWithoutLoggingSecrets()
    {
        var store = Store(); var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var path = store.RecordFailure(1234, "test-build", "Windows operator console session",
            new InvalidOperationException("password=secret-outer", new UnauthorizedAccessException("token=secret-inner")));
        Assert.NotNull(path);
        var raw = File.ReadAllText(path); Assert.DoesNotContain("secret", raw); Assert.DoesNotContain("password", raw); Assert.DoesNotContain("token", raw);
        var failure = store.ReadFailure(1234, "test-build", before);
        Assert.NotNull(failure); Assert.Equal("UnauthorizedAccessException", failure.RootErrorType);
        var message = store.FailureMessage(1234, 1, "test-build", before);
        Assert.Contains("Windows operator console session", message); Assert.Contains(failure.RootErrorCode, message);
        Assert.Contains("Existing broadcasts continue", message);
    }

    [Fact]
    public void AnotherBuildOrStaleAttemptCannotSupplyTheCurrentFailure()
    {
        var store = Store(); var path = store.RecordFailure(1234, "first-build", "web host creation", new IOException("private data"));
        Assert.NotNull(path);
        Assert.Null(store.ReadFailure(1234, "other-build", DateTimeOffset.MinValue));
        Assert.Null(store.ReadFailure(1234, "first-build", DateTimeOffset.UtcNow.AddMinutes(2)));
        Assert.Null(store.ReadFailure(1235, "first-build", DateTimeOffset.MinValue));
        var raw = File.ReadAllText(path);
        Assert.Null(store.RecordFailure(1234, "first-build", "web host build", new IOException("replacement")));
        Assert.Equal(raw, File.ReadAllText(path));
    }

    [Fact]
    public void DamagedOrOversizedDiagnosticsFallBackToTheActualExitCode()
    {
        var store = Store(); var path = store.RecordFailure(1234, "build", "web host creation", new IOException("private data"));
        Assert.NotNull(path);
        foreach (var raw in new[] { "{broken", "{\"ProcessId\":1234,\"Version\":\"build\",\"Stage\":null}", new string('x', 4097) })
        {
            File.WriteAllText(path, raw);
            Assert.Null(store.ReadFailure(1234, "build", DateTimeOffset.MinValue));
            Assert.Contains("0xC0000135", store.FailureMessage(1234, unchecked((int)0xC0000135), "build", DateTimeOffset.MinValue));
        }
    }

    [Fact]
    public void DiagnosticWriterFailureCannotReplaceTheOriginalStartupError()
    {
        var store = new ConsoleStartupDiagnostics("not-an-absolute-root");
        Assert.Null(store.RecordFailure(1234, "build", "web host creation", new IOException("private data")));
        Assert.Null(Store().RecordFailure(1234, "build", "untrusted\nstage", new IOException("private data")));
    }
}
