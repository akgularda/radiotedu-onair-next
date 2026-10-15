namespace OnAirNext.ConsoleProtocol;

public sealed record ConsoleEndpoint(int ProcessId,string Version,string Token);
public static class ConsoleEndpointLocation
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","browser-console","access");
    public static string Descriptor => Path.Combine(Directory,"console.json");
}
