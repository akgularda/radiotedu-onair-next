using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

if(!OperatingSystem.IsWindows())return 2;
try
{
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
    async Task<bool> ReadyAsync()
    {
        try{using var result=await http.GetAsync("http://127.0.0.1:18121/api/health");if(!result.IsSuccessStatusCode)throw new InvalidOperationException("Another application occupies the console port.");var body=await result.Content.ReadAsStringAsync();if(body.Length>4096)throw new InvalidOperationException("Unexpected console response.");using var json=JsonDocument.Parse(body);if(!json.RootElement.TryGetProperty("product",out var product)||product.GetString()!="RadioTEDU-OnAirNext")throw new InvalidOperationException("Another application occupies the console port.");return true;}
        catch(HttpRequestException){return false;}catch(TaskCanceledException){return false;}
    }
    if(!await ReadyAsync())
    {
        if(!File.Exists(web))throw new IOException("The browser console is missing from this installation package.");
        using var child=Process.Start(new ProcessStartInfo(web){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(web)!})??throw new IOException("The browser console did not start.");
        var deadline=DateTimeOffset.UtcNow.AddSeconds(15);while(!await ReadyAsync()){if(child.HasExited||DateTimeOffset.UtcNow>deadline)throw new IOException("The local console could not open. Check whether its port is already in use.");await Task.Delay(200);}
    }
    Process.Start(new ProcessStartInfo("http://127.0.0.1:18121/"){UseShellExecute=true});return 0;
}
catch(Exception error)
{
    var message=error is ArgumentException or InvalidOperationException ? error.Message : "Setup could not complete. Check the reviewed package, runtime files, permissions and free disk space. Existing broadcasts were not taken over.";
    if(args.Length>0&&args[0]=="--prepare")Console.WriteLine(JsonSerializer.Serialize(new{error=message}));
    else if(args.Length==0||args[0]=="--open-console")SetupMessage.Show(message);
    return 1;
}

internal static class SetupMessage
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Show(string message)=>MessageBoxW(0,message,"RadioTEDU OnAir Next",0x10);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)][DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int MessageBoxW(nint owner,string text,string caption,uint type);
}
