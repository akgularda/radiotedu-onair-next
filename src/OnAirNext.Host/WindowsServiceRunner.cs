using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OnAirNext.ServiceControl;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceRunner
{
    private readonly Func<CancellationToken,Action,Action,Task<int>> _work;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly MainCallback _main; private readonly ControlCallback _control;
    private nint _statusHandle; private Status _status; private Task _cancellation = Task.CompletedTask;
    private bool _stopping, _stopped, _requestedStop; private int _exitCode, _reportFailure;
    private WindowsServiceRunner(Func<CancellationToken,Action,Action,Task<int>> work)
    { _work=work; _main=ServiceMain; _control=Control; }
    public static int Run(Func<CancellationToken,Action,Action,Task<int>> work)
    {
        var runner=new WindowsServiceRunner(work);
        var table=new[] { new TableEntry { Name=ServiceEndpoint.Name, Main=runner._main }, new TableEntry() };
        if (!StartServiceCtrlDispatcherW(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(runner); return Volatile.Read(ref runner._exitCode);
    }
    private void ServiceMain(uint count,nint arguments)
    {
        uint failure=0;
        try
        {
            _statusHandle=RegisterServiceCtrlHandlerExW(ServiceEndpoint.Name,_control,0);
            if (_statusHandle==0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Release SCM's startup serialization promptly; engine readiness is a separate IPC state.
            lock (_gate) Report(4,0);
            WindowsOwnedJob.AttachCurrentProcess();
            var code=_work(_stop.Token,Ready,Stopping).GetAwaiter().GetResult();
            lock (_gate) failure=code!=0 ? (uint)code : _requestedStop ? 0u : 1u;
        }
        catch(Exception error) { failure=unchecked((uint)error.HResult); if(failure==0)failure=1; }
        finally
        {
            Task cancellation;
            lock (_gate) { _stopped=true; cancellation=_cancellation; }
            try { cancellation.GetAwaiter().GetResult(); } catch { failure=failure==0?1u:failure; }
            lock (_gate)
            {
                if(failure==0&&_reportFailure!=0)failure=(uint)_reportFailure;
                _stopped=true; _exitCode=failure==0?0:1;
                if(_statusHandle!=0) Report(1,0,failure);
            }
            _stop.Dispose();
        }
    }
    private void Ready() { lock(_gate) if(!_stopping&&!_stopped) Report(4,1|4); }
    private void Stopping() { lock(_gate) if(!_stopping&&!_stopped) { _stopping=true; Report(3,0); } }
    private uint Control(uint control,uint eventType,nint data,nint context)
    {
        try
        {
            lock(_gate)
            {
                if(_stopped)return 0;
                if(control==4) { SetServiceStatus(_statusHandle,ref _status); return 0; }
                if(control is not (1 or 5))return 120;
                if(!_requestedStop) { _requestedStop=true; _stopping=true; Report(3,0); _cancellation=_stop.CancelAsync(); }
            }
            return 0;
        }
        catch { return 1; } // Exceptions must never cross the unmanaged control callback.
    }
    private void Report(uint state,uint controls,uint failure=0)
    {
        _status=new Status { Type=16, State=state, Controls=controls, Win32Exit=failure==0?0u:1066u,
            SpecificExit=failure, Checkpoint=state==3?1u:0u, WaitHint=state==3?30000u:0u };
        if(!SetServiceStatus(_statusHandle,ref _status)) { _reportFailure=Marshal.GetLastWin32Error(); _exitCode=1; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint count,nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlCallback(uint control,uint eventType,nint data,nint context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TableEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; [MarshalAs(UnmanagedType.FunctionPtr)] public MainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type,State,Controls,Win32Exit,SpecificExit,Checkpoint,WaitHint; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] TableEntry[] services);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern nint RegisterServiceCtrlHandlerExW(string name,ControlCallback callback,nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint handle,ref Status status);
}
