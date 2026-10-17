using System.ComponentModel;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.ServiceControl;

// Commissioning is deliberately distinct from the development loopback boundary.
// A stopped-but-automatic former service is not an exclusive ownership release.
internal static class LegacyServiceFence
{
    internal const string LegacyService = "RadioTEDU.OnAir.Supervisor";

    public static void RequireReleased()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Commissioning requires the installed Windows service.");
        RequireWindowsRelease();
    }

    // Per-output guard for the one-station-at-a-time handover: the whole former
    // service must be released, or (while it still serves the other channels)
    // this exact mount must have no connected source on the production origin.
    public static Action ForOutput(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return () => RequireOutputReleased(output);
    }

    public static void RequireOutputReleased(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Commissioning requires the installed Windows service.");
        if (TryWindowsRelease()) return;
        RequireOriginMountFree(output);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryWindowsRelease()
    {
        try { RequireWindowsRelease(); return true; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private static void RequireOriginMountFree(OutputSettings output)
    {
        var destination = $"http://{output.Host}:{output.Port}/status-json.xsl";
        string payload;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            payload = client.GetStringAsync(destination).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or TimeoutException or
            IOException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            throw new InvalidOperationException($"The production source list for {output.Mount} could not be read from {output.Host}:{output.Port}; the former program's release stays unverified.", error);
        }
        bool owned;
        try { owned = OriginSourceList.OwnsMount(payload, output.Mount); }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"The production source list for {output.Mount} could not be parsed; the former program's release stays unverified.", error);
        }
        if (owned)
            throw new InvalidOperationException($"The former program still owns {output.Mount} on {output.Host}:{output.Port}; this source waits for the mount release.");
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsRelease()
    {
        var manager = OpenSCManagerW(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, LegacyService, 5);
            if (service == 0) throw new InvalidOperationException("The former service ownership release cannot be verified.");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                QueryServiceConfigW(service, 0, 0, out var needed);
                if (needed is < 1 or > 65536) throw new IOException("The former service configuration is unavailable.");
                var memory = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!QueryServiceConfigW(service, memory, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config = Marshal.PtrToStructure<Configuration>(memory);
                    RequireReleasedState(status.State, status.ProcessId, config.StartType);
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static void RequireReleasedState(uint state, uint processId, uint startType)
    {
        if (state != 1 || processId != 0 || startType != 4)
            throw new InvalidOperationException("The former broadcast service must be stopped and its automatic startup disabled before any production source connects.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Status
    {
        public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration
    {
        public uint Type, StartType, ErrorControl;
        public nint BinaryPath, LoadOrderGroup;
        public uint Tag;
        public nint Dependencies, StartName, DisplayName;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(nint service, uint level, out Status status, int bytes, out int needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(nint service, nint config, uint bytes, out uint needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
}

// Pure parser for Icecast status-json.xsl source lists. Matches the exact mount
// path so that /energize and /energize-low are never confused.
internal static class OriginSourceList
{
    public static bool OwnsMount(string statusJson, string mount)
    {
        ArgumentNullException.ThrowIfNull(statusJson);
        ArgumentNullException.ThrowIfNull(mount);
        using var document = JsonDocument.Parse(statusJson);
        if (!document.RootElement.TryGetProperty("icestats", out var stats) || stats.ValueKind != JsonValueKind.Object) return false;
        if (!stats.TryGetProperty("source", out var source)) return false;
        if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in source.EnumerateArray())
                if (EntryOwns(entry, mount)) return true;
            return false;
        }
        return source.ValueKind == JsonValueKind.Object && EntryOwns(source, mount);
    }

    private static bool EntryOwns(JsonElement entry, string mount)
    {
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (entry.TryGetProperty("listenurl", out var listenUrl) && listenUrl.ValueKind == JsonValueKind.String)
        {
            var value = listenUrl.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                {
                    if (uri.AbsolutePath.Equals(mount, StringComparison.Ordinal)) return true;
                }
                else if (value.TrimEnd('/').Equals(mount, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        if (entry.TryGetProperty("mount", out var mountElement) && mountElement.ValueKind == JsonValueKind.String &&
            string.Equals(mountElement.GetString(), mount, StringComparison.Ordinal)) return true;
        return false;
    }
}
