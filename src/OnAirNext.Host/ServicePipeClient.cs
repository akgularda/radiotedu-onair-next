using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OnAirNext.ServiceControl;

internal static class ServicePipeClient
{
    public static NamedPipeClientStream Create(string name)
    {
        if (!ServiceEndpoint.IsAllowedPipe(name)) throw new ArgumentException("Use an isolated development engine.");
        if (OperatingSystem.IsWindows())
        {
            if (ServiceEndpoint.IsReserved(name))
                return new NamedPipeClientStream(".", name, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
            return new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                TokenImpersonationLevel.Identification);
        }
        return new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    public static void AuthenticateServer(NamedPipeClientStream pipe, string name)
    {
        if (!ServiceEndpoint.IsReserved(name)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Windows service is available on Windows only.");
        VerifyWindowsServer(pipe);
    }
    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var manager = OpenSCManagerW(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, ServiceEndpoint.Name, 4);
            if (service == 0) throw new UnauthorizedAccessException("The isolated Windows service is not installed. A matching pipe name is not proof of its identity.");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.State != 4 || status.ProcessId == 0 || actual != status.ProcessId)
                    throw new UnauthorizedAccessException("The pipe does not belong to the running isolated Windows service.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint Type, State, Controls, Win32Exit, SpecificExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint processId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, uint level, out ServiceStatusProcess status, int size, out int needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
