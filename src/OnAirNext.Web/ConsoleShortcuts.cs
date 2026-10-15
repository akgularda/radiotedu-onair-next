using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Runtime.InteropServices.ComTypes;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal static class ConsoleShortcuts
{
    public static void Create()
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("Desktop shortcuts are available in the Windows package.");
        try { CreateWindows(); } catch(COMException error) { throw new IOException("Windows could not create the shortcuts. The installed launcher is available in the setup folder.",error); }
    }
    [SupportedOSPlatform("windows")]
    private static void CreateWindows()
    {
        var service=SetupService.Read()??throw new InvalidOperationException("Install the separate service before creating its shortcuts.");SetupService.RequireOwned(service);
        var target=Path.Combine(ServiceEndpoint.InstallRoot,"setup","OnAirNext.Setup.exe");SetupPaths.RequireNoLinks(target);
        if(!File.Exists(target))throw new IOException("The installed launcher is missing. Keep the original package and inspect the installation receipt.");
        foreach(var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.Programs)})
        {
            if(string.IsNullOrEmpty(folder))throw new IOException("The Windows shortcut folder is unavailable.");
            SetupPaths.RequireNoLinks(folder);Directory.CreateDirectory(folder);
            var destination=Path.Combine(folder,"RadioTEDU OnAir Next.lnk");SetupPaths.RequireNoLinks(destination);
            // Existing user shortcuts are never replaced.
            var temporary=Path.Combine(folder,".onair-next-"+Guid.NewGuid().ToString("N")+".lnk");
            IShellLink link=(IShellLink)new ShellLink();
            try
            {
                if(File.Exists(destination))
                {
                    ((IPersistFile)link).Load(destination,0);var oldPath=new StringBuilder(32768);var oldArguments=new StringBuilder(1024);
                    link.GetPath(oldPath,oldPath.Capacity,0,4);link.GetArguments(oldArguments,oldArguments.Capacity);
                    if(oldPath.ToString().Equals(target,StringComparison.OrdinalIgnoreCase)&&oldArguments.ToString()=="--open-console")continue;
                    throw new InvalidOperationException("A different desktop or Start shortcut already uses this name. Rename it before creating the OnAir Next shortcut.");
                }
                link.SetPath(target);link.SetArguments("--open-console");link.SetWorkingDirectory(ServiceEndpoint.InstallRoot);
                link.SetDescription("Open the RadioTEDU OnAir Next browser console");link.SetShowCmd(1);
                ((IPersistFile)link).Save(temporary,true);File.Move(temporary,destination,false);
            }
            finally{Marshal.FinalReleaseComObject(link);SetupPaths.RequireNoLinks(temporary);if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    [ComImport,Guid("00021401-0000-0000-C000-000000000046")]private class ShellLink { }
    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder file,int size,nint findData,uint flags);void GetIDList(out nint list);void SetIDList(nint list);
        void GetDescription(nint text,int size);void SetDescription([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetWorkingDirectory(nint text,int size);void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)]string path);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,int size);void SetArguments([MarshalAs(UnmanagedType.LPWStr)]string arguments);
        void GetHotkey(out short key);void SetHotkey(short key);void GetShowCmd(out int command);void SetShowCmd(int command);
        void GetIconLocation(nint icon,int size,out int index);void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)]string icon,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)]string path,uint reserved);void Resolve(nint window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)]string path);
    }
}
