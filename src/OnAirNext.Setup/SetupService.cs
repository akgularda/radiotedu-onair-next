using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

public sealed record SetupServiceState(uint State,uint ProcessId,string Binary,string Account,uint StartType,bool Delayed,bool FailureFlag,int RecoveryActions);

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
                    var actions=ReadInfo(handle,2,p=>{var failure=Marshal.PtrToStructure<Failure>(p);if(failure.Reset!=86400||failure.Count!=3||failure.Actions==0||!string.IsNullOrEmpty(Marshal.PtrToStringUni(failure.Command)))return 0;for(var i=0;i<3;i++)if(Marshal.ReadInt32(failure.Actions,i*8)!=1||Marshal.ReadInt32(failure.Actions,i*8+4)<5000)return 0;return 3;});
                    return new(status.State,status.ProcessId,Marshal.PtrToStringUni(config.Binary)??"",Marshal.PtrToStringUni(config.Account)??"",config.StartType,delayed,flag,actions);
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
    private static async Task ScAsync(string[] args,CancellationToken token)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Windows service setup did not start.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(deadline.Token);if(process.ExitCode!=0)throw new Win32Exception(process.ExitCode);}
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(nint handle);
}
