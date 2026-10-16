using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ConsoleProtocol;
using OnAirNext.ServiceControl;

if(!OperatingSystem.IsWindows())return 2;
try
{
    if(args.Length==1&&args[0]=="--prepare-update")
    {
        WindowsOwnedJob.AttachCurrentProcess();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var root=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        var review=await AppUpdatePreparation.PrepareAsync(root,deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(review));return 0;
    }
    if(args.Length==3&&args[0]=="--prepare")
    {
        WindowsOwnedJob.AttachCurrentProcess();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var request=await SetupPaths.ReadVerifiedAsync<SetupRequest>(args[1],args[2],deadline.Token);
        var root=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        var plan=await SetupPreparation.PrepareAsync(root,request,deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(plan));return 0;
    }
    if(args.Length==3&&args[0]=="--install")
    {
        WindowsOwnedJob.AttachCurrentProcess();return await WindowsInstaller.InstallAsync(args[1],args[2]);
    }
    if(args.Length!=0&&!(args.Length==1&&args[0]=="--open-console"))throw new ArgumentException("Open the installation package normally, or use System in the local console.");
    var package=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
    var web=Path.Combine(package,"console","OnAirNext.Web.exe");SetupPaths.RequireNoLinks(web);
    using var http=new HttpClient{Timeout=TimeSpan.FromMilliseconds(800)};
    var consoleProcessId=0;
    async Task<bool> ReadyAsync()
    {
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            using var result=await http.GetAsync("http://127.0.0.1:18121/api/health",HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!result.IsSuccessStatusCode)throw new InvalidOperationException("Another application occupies the console port.");
            await using var body=await result.Content.ReadAsStreamAsync(deadline.Token);var bytes=new byte[4097];var count=0;
            while(count<bytes.Length){var read=await body.ReadAsync(bytes.AsMemory(count),deadline.Token);if(read==0)break;count+=read;}
            if(count>4096)throw new InvalidOperationException("Unexpected console response.");
            using var json=JsonDocument.Parse(bytes.AsMemory(0,count));
            if(!json.RootElement.TryGetProperty("product",out var product)||product.GetString()!="RadioTEDU-OnAirNext")throw new InvalidOperationException("Another application occupies the console port.");
            var own=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if(!json.RootElement.TryGetProperty("version",out var version)||version.GetString()!=own)throw new InvalidOperationException("A different console version is open. Use Close console in its System screen, then open this package again. Broadcasts continue in the service.");if(!json.RootElement.TryGetProperty("processId",out var processId)||!processId.TryGetInt32(out consoleProcessId))throw new InvalidOperationException("The console process identity is unavailable.");return true;
        }
        catch(HttpRequestException){return false;}catch(OperationCanceledException){return false;}
    }
    if(!await ReadyAsync())
    {
        if(!File.Exists(web))throw new IOException("The browser console is missing from this installation package.");
        using var child=Process.Start(new ProcessStartInfo(web){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(web)!})??throw new IOException("The browser console did not start.");
        try
        {
            var timer=Stopwatch.StartNew();while(!await ReadyAsync()){if(child.HasExited||timer.Elapsed>TimeSpan.FromSeconds(15))throw new IOException("The local console could not open. Check whether its port is already in use.");await Task.Delay(200);}
        }
        catch
        {
            if(!child.HasExited){child.Kill(entireProcessTree:true);using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await child.WaitForExitAsync(cleanup.Token);}throw;
        }
    }
    ConsoleEndpoint endpoint;
    try{endpoint=await SetupPaths.ReadAsync<ConsoleEndpoint>(ConsoleEndpointLocation.Descriptor,4096);}
    catch(Exception error)when(error is IOException or UnauthorizedAccessException){throw new InvalidOperationException("Open the console from the Windows operator account that started it.",error);}
    var expectedVersion=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if(endpoint.ProcessId!=consoleProcessId||endpoint.Version!=expectedVersion||endpoint.Token.Length!=64||!endpoint.Token.All(Uri.IsHexDigit))throw new InvalidOperationException("The operator console session changed. Open the installed shortcut again.");
    Process.Start(new ProcessStartInfo("http://127.0.0.1:18121/#access="+endpoint.Token){UseShellExecute=true});return 0;
}
catch(Exception error)
{
    var message=error is ArgumentException or InvalidOperationException ? error.Message : "Setup could not complete. Check the reviewed package, runtime files, permissions and free disk space. Existing broadcasts were not taken over.";
    if(args.Length>0&&args[0] is "--prepare" or "--prepare-update")Console.WriteLine(JsonSerializer.Serialize(new{error=message}));
    else if(args.Length==0||args[0]=="--open-console")SetupMessage.Show(message);
    return 1;
}

internal static class SetupMessage
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Show(string message)=>MessageBoxW(0,message,"RadioTEDU OnAir Next",0x10);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int MessageBoxW(nint owner,string text,string caption,uint type);
}
