namespace OnAirNext.ServiceControl;

internal static class ServiceEndpoint
{
    public const string Name = "RadioTEDU.OnAirNext.Development";
    public const string Pipe = "onair-next-dev-service";
    public const string FilePipe = Pipe + "-files";
    public static string Files(string control) => IsIsolated(control) && !control.EndsWith("-files", StringComparison.Ordinal)
        ? control + "-files" : throw new ArgumentException("Choose an isolated control engine.");
    public static bool IsAllowedPipe(string name) => IsIsolated(name) || name.EndsWith("-files", StringComparison.Ordinal) && IsIsolated(name[..^6]);
    public static bool IsReserved(string name) => name is Pipe or FilePipe;
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RadioTEDU-OnAirNext-Development");
    public static string ConfigPath => Path.Combine(DataRoot, "host.json");
    public static string EngineData => Path.Combine(DataRoot, "data");
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RadioTEDU-OnAirNext-Development");
    public static string Ffmpeg => Path.Combine(InstallRoot, "tools", "ffmpeg.exe");
    public static bool IsIsolated(string? name) => name is not null && name.StartsWith("onair-next-dev-", StringComparison.Ordinal)
        && name.Length <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static void RequirePath(string actual, string expected)
    {
        if (!Path.IsPathFullyQualified(actual) || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The development service requires its separate installed paths.");
        for (var item = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(actual))!); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Service paths cannot traverse folder links.");
        if ((File.Exists(actual) || Directory.Exists(actual)) && (File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Service paths cannot be links.");
    }
}
