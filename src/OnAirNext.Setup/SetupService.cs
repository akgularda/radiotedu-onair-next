using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

public sealed record SetupServiceState(uint State,uint ProcessId,string Binary,string Account,uint StartType,bool Delayed,bool FailureFlag,int RecoveryActions,uint FailureActionsCount=0,bool HasRecoveryCommand=false);

[SupportedOSPlatform("windows")]
public static class SetupService
{
    public static string ExpectedBinary => "\""+Path.Combine(ServiceEndpoint.InstallRoot,"bin","OnAirNext.Host.exe")+"\" --windows-service --config \""+ServiceEndpoint.ConfigPath+"\"";
    public static SetupServiceState? Read()
    {
        var manager=OpenSCManagerW(null,null,1);if(manager==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var handle=OpenServiceW(manager,ServiceEndpoint.Name,5);
            if(handle==0){var error=Marshal.GetLastWin32Error();if(error==1060)return null;throw new Win32Exception(error);}
            try
            {
                if(!QueryServiceStatusEx(handle,0,out var status,Marshal.SizeOf<Status>(),out _))throw new Win32Exception(Marshal.GetLastWin32Error());
                QueryServiceConfigW(handle,0,0,out var needed);if(needed is <1 or >65536)throw new IOException("Service configuration size is invalid.");
                var memory=Marshal.AllocHGlobal((int)needed);
                try
                {
                    if(!QueryServiceConfigW(handle,memory,needed,out _))throw new Win32Exception(Marshal.GetLastWin32Error());var config=Marshal.PtrToStructure<Configuration>(memory);
                    var delayed=ReadInfo(handle,3,p=>Marshal.ReadInt32(p)!=0);var flag=ReadInfo(handle,4,p=>Marshal.ReadInt32(p)!=0);
                    var actions=ReadInfo(handle,2,p=>
                    {
                        var failure=Marshal.PtrToStructure<Failure>(p);var command=!string.IsNullOrEmpty(Marshal.PtrToStringUni(failure.Command));var valid=0;
                        if(failure.Reset==86400&&failure.Count==3&&failure.Actions!=0&&!command)
                        {
                            int[] delays=[5000,15000,60000];valid=3;
                            for(var i=0;i<3;i++)if(Marshal.ReadInt32(failure.Actions,i*8)!=1||Marshal.ReadInt32(failure.Actions,i*8+4)!=delays[i]){valid=0;break;}
                        }
                        return(Valid:valid,Count:failure.Count,Command:command);
                    });
                    return new(status.State,status.ProcessId,Marshal.PtrToStringUni(config.Binary)??"",Marshal.PtrToStringUni(config.Account)??"",config.StartType,delayed,flag,actions.Valid,actions.Count,actions.Command);
                }
                finally{Marshal.FreeHGlobal(memory);}
            }
            finally{CloseServiceHandle(handle);}
        }
        finally{CloseServiceHandle(manager);}
    }
    private static T ReadInfo<T>(nint handle,uint level,Func<nint,T> read)
    {
        QueryServiceConfig2W(handle,level,0,0,out var bytes);if(bytes is <1 or >65536)throw new IOException("Service recovery configuration size is invalid.");
        var memory=Marshal.AllocHGlobal((int)bytes);try{if(!QueryServiceConfig2W(handle,level,memory,bytes,out _))throw new Win32Exception(Marshal.GetLastWin32Error());return read(memory);}finally{Marshal.FreeHGlobal(memory);}
    }
    public static void RequireOwned(SetupServiceState service)
    {
        if(!service.Binary.Equals(ExpectedBinary,StringComparison.OrdinalIgnoreCase)||!service.Account.Equals(@"NT AUTHORITY\LocalService",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The isolated service name is already used by a different installation. No service was changed.");
    }
    public static async Task InstallAsync(bool start,CancellationToken token)
    {
        var service=Read();if(service is null)await ScAsync(["create",ServiceEndpoint.Name,"binPath=",ExpectedBinary,"start=","auto","obj=",@"NT AUTHORITY\LocalService"],token);else RequireOwned(service);
        await ScAsync(["config",ServiceEndpoint.Name,"start=","delayed-auto"],token);
        await ScAsync(["failure",ServiceEndpoint.Name,"reset=","86400","actions=","restart/5000/restart/15000/restart/60000"],token);
        await ScAsync(["failureflag",ServiceEndpoint.Name,"1"],token);
        service=Read()??throw new IOException("The new service could not be read back.");RequireOwned(service);
        if(service.StartType!=2||!service.Delayed||!service.FailureFlag||service.RecoveryActions!=3)throw new IOException("The new service startup or recovery settings could not be verified.");
        if(start&&service.State==1)await ScAsync(["start",ServiceEndpoint.Name],token);
    }
    // Called only by the elevated reviewed updater, while its global setup lock is held.
    // This never addresses the former broadcasting service and never kills a process.
    public static async Task SuspendForMaintenanceAsync(uint? expectedProcessId,CancellationToken token)
    {
        var before=Read()??throw new IOException("The installed service is missing.");RequireOwned(before);
        if(before.State is not (1 or 3 or 4))throw new InvalidOperationException("Wait for the isolated service to reach a stable state before maintenance.");
        if(expectedProcessId is { } expected&&(before.State!=4||before.ProcessId!=expected))throw new InvalidOperationException("The reviewed service process changed. Prepare a fresh update review.");
        var originalPolicy=before.StartType==2&&before.Delayed&&before.FailureFlag&&before.RecoveryActions==3;
        var resumableFence=expectedProcessId is null&&before.StartType==4&&(before.FailureActionsCount==0||before.RecoveryActions==3);
        if(before.HasRecoveryCommand||!(originalPolicy||resumableFence))
            throw new InvalidOperationException("The isolated service has unreviewed startup or recovery settings.");
        using var process=before.ProcessId==0?null:Process.GetProcessById(checked((int)before.ProcessId));
        if(process is not null)
        {
            _=process.SafeHandle; // Retain this process identity across a graceful stop/PID reuse.
            if(!string.Equals(process.MainModule?.FileName,Path.Combine(ServiceEndpoint.InstallRoot,"bin","OnAirNext.Host.exe"),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The service process image does not match the owned installation.");
        }
        // A disabled start type also fences already queued SCM restart attempts. Clear
        // the actions before stopping so a crash cannot race the application rename.
        await ScAsync(["config",ServiceEndpoint.Name,"start=","disabled"],token);
        ClearFailureActions();await ScAsync(["failureflag",ServiceEndpoint.Name,"0"],token);
        var fenced=Read()??throw new IOException("Maintenance service readback failed.");RequireOwned(fenced);
        if(fenced.StartType!=4||fenced.FailureActionsCount!=0||fenced.HasRecoveryCommand||fenced.FailureFlag)throw new IOException("The service maintenance fence could not be verified.");
        if(fenced.State==4)await ScAsync(["stop",ServiceEndpoint.Name],token);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(35));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();var current=Read()??throw new IOException("The maintenance service disappeared.");RequireOwned(current);
            if(current.State==1&&current.ProcessId==0)break;
            await Task.Delay(200,deadline.Token);
        }
        if(process is not null)await process.WaitForExitAsync(deadline.Token);
        RequireMaintenanceStopped();
    }
    public static void RequireMaintenanceStopped()
    {
        var service=Read()??throw new IOException("The maintenance service is missing.");RequireOwned(service);
        if(service.State!=1||service.ProcessId!=0||service.StartType!=4||service.FailureActionsCount!=0||service.HasRecoveryCommand||service.FailureFlag)
            throw new InvalidOperationException("Keep the isolated service stopped and fenced throughout application maintenance.");
    }
    private static void ClearFailureActions()
    {
        var manager=OpenSCManagerW(null,null,1);if(manager==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service=OpenServiceW(manager,ServiceEndpoint.Name,2);if(service==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var emptyActions=Marshal.AllocHGlobal(8);var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Failure>());
                try
                {
                    // Microsoft requires a NON-null action pointer with Count=0 to delete
                    // the action list. A null pointer would leave the restart actions intact.
                    Marshal.WriteInt64(emptyActions,0);var value=new Failure{Actions=emptyActions};
                    Marshal.StructureToPtr(value,memory,false);if(!ChangeServiceConfig2W(service,2,memory))throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally{Marshal.FreeHGlobal(memory);Marshal.FreeHGlobal(emptyActions);}
            }
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }
    private static async Task ScAsync(string[] args,CancellationToken token)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Windows service setup did not start.");
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(deadline.Token);await output;await errors;if(process.ExitCode!=0)throw new Win32Exception(process.ExitCode);}
        finally{if(!process.HasExited){process.Kill();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);}}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Configuration{public uint Type,StartType,Error;public nint Binary,Group;public uint Tag;public nint Dependencies,Account,DisplayName;}
    [StructLayout(LayoutKind.Sequential)]private struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait,ProcessId,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct Failure{public uint Reset;public nint Reboot,Command;public uint Count;public nint Actions;}
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern nint OpenSCManagerW(string? machine,string? database,uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern nint OpenServiceW(nint manager,string name,uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceStatusEx(nint handle,uint level,out Status status,int bytes,out int needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceConfigW(nint handle,nint config,uint bytes,out uint needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceConfig2W(nint handle,uint level,nint config,uint bytes,out uint needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ChangeServiceConfig2W(nint handle,uint level,nint config);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(nint handle);
}
