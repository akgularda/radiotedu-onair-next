using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.Installation;

namespace OnAirNext.ConsoleProtocol;

internal sealed record ConsoleStartupFailure(int ProcessId, string Version, string Stage, string ErrorType,
    string ErrorCode, string RootErrorType, string RootErrorCode, DateTimeOffset AtUtc);

// Startup-only diagnostics contain exception identities/codes, never exception
// messages, arguments, environment, request bodies or operator session tokens.
internal sealed class ConsoleStartupDiagnostics(string directory)
{
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal) { "temporary upload directory", "web host creation",
        "Windows operator console session", "web host build", "local HTTP endpoint startup", "running local console" };
    public static ConsoleStartupDiagnostics ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev",
        "browser-console", "startup-errors"));
    private string Location(int processId)
    {
        if (processId <= 0) throw new ArgumentException("A console process identity is required.");
        var root = SetupPaths.CanonicalRoot(directory);
        return Path.Combine(root, "console-" + processId.ToString(CultureInfo.InvariantCulture) + ".json");
    }
    public string? RecordFailure(int processId, string version, string stage, Exception error)
    {
        try
        {
            if (!Stages.Contains(stage) ||
                string.IsNullOrWhiteSpace(version) || version.Length > 160 || version.Any(char.IsControl)) return null;
            var file = Location(processId); var folder = Path.GetDirectoryName(file)!;
            SetupPaths.RequireNoLinks(folder);
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var owner = identity.User ?? throw new UnauthorizedAccessException("The operator identity is unavailable.");
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(owner);
                foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                var target = new DirectoryInfo(folder);
                if (target.Exists) target.SetAccessControl(security); else target.Create(security);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            SetupPaths.RequireNoLinks(file);
            var rootError = error;
            for (var depth = 0; depth < 8 && rootError.InnerException is { } inner; depth++) rootError = inner;
            var value = new ConsoleStartupFailure(processId, version, stage, error.GetType().Name,
                "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture), rootError.GetType().Name,
                "0x" + rootError.HResult.ToString("X8", CultureInfo.InvariantCulture), DateTimeOffset.UtcNow);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            // Each launcher attempt owns its process. Do not replace an existing
            // diagnostic after PID reuse; the reader rejects stale timestamps.
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(bytes); stream.Flush(true); return file;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException or PlatformNotSupportedException) { return null; }
    }
    public ConsoleStartupFailure? ReadFailure(int processId, string version, DateTimeOffset notBefore)
    {
        try
        {
            var file = Location(processId); SetupPaths.RequireNoLinks(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 2 or > 4096) return null;
            var value = JsonSerializer.Deserialize<ConsoleStartupFailure>(stream);
            return value is not null && value.ProcessId == processId && value.Version == version && value.AtUtc >= notBefore &&
                value.AtUtc <= DateTimeOffset.UtcNow.AddMinutes(1) && Stages.Contains(value.Stage) && SafeType(value.ErrorType) &&
                SafeType(value.RootErrorType) && SafeCode(value.ErrorCode) && SafeCode(value.RootErrorCode) ? value : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or JsonException or
            System.Security.SecurityException) { return null; }
    }
    private static bool SafeType(string? value) => value is { Length: > 0 and <= 120 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '`');
    private static bool SafeCode(string? value) => value is { Length: 10 } && value.StartsWith("0x", StringComparison.Ordinal) &&
        value[2..].All(Uri.IsHexDigit);
    public string FailureMessage(int processId, int exitCode, string version, DateTimeOffset notBefore)
    {
        var failure = ReadFailure(processId, version, notBefore);
        var code = "0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture);
        return failure is null
            ? "The browser console closed before opening (exit " + code + "). Open a complete reviewed package. Existing broadcasts continue."
            : "The browser console could not open at " + failure.Stage + ". " + failure.RootErrorType + " (" + failure.RootErrorCode +
                "). Startup code details: " + Location(processId) + ". Existing broadcasts continue.";
    }
}
