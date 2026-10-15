using OnAirNext.Host;
using OnAirNext.ServiceControl;

if (args.Length == 2 && args[0] == "--probe-media-source") return await SourceAccessProbe.RunAsync(args[1]);

var serviceMode = args.Length == 3 && args[0] == "--windows-service" && args[1] == "--config";
if (!serviceMode && (args.Length != 2 || args[0] != "--config"))
{
    Console.Error.WriteLine("Use the guided installation, or the isolated developer host configuration.");
    return 2;
}
var configPath=args[serviceMode?2:1];
if(serviceMode)
{
    if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service mode requires Windows.");
    return WindowsServiceRunner.Run((token,ready,stopping)=>EngineHost.RunAsync(configPath,true,token,ready,stopping));
}
using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,eventArgs)=>{eventArgs.Cancel=true;stop.Cancel();};
return await EngineHost.RunAsync(configPath,false,stop.Token,()=>{},()=>{});
