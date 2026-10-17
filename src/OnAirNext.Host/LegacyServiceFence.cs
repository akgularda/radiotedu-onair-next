using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
