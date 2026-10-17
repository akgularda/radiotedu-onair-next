using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Host;

internal static class SourceAccessProbe
{
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
    public static async Task<int> RunAsync(string requested)
    {
        var path = LocalMediaPath.Normalize(requested);
        var identity = Environment.UserName; string? sid = null;
        if (OperatingSystem.IsWindows()) { using var token = WindowsIdentity.GetCurrent(); identity = token.Name; sid = token.User?.Value; }
        var volume = new DriveInfo(Path.GetPathRoot(path)!); var kind = volume.DriveType.ToString(); string? volumeId = null;
        SourceAccessResult Result(bool accessible, int entries, bool limited, bool? sample, string detail)
            => new(accessible, identity, sid, kind, volumeId, entries, limited, sample, detail);
        SourceAccessResult result;
        try
        {
            if (volume.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                throw new IOException("Choose an available local drive.");
            for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose a folder without links.");
            if (OperatingSystem.IsWindows())
            {
                var buffer = new System.Text.StringBuilder(512);
                if (GetVolumeNameForVolumeMountPointW(volume.RootDirectory.FullName, buffer, buffer.Capacity)) volumeId = buffer.ToString();
            }
            var count = 0; var limited = false; bool? readable = null; var attempts = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (count == 32) { limited = true; break; } count++;
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 || !Audio.Contains(Path.GetExtension(entry)) || attempts >= 4) continue;
                attempts++;
                try
                {
                    await using var file = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var read = await file.ReadAsync(new byte[1]);
                    if (read == 1) readable = true;
                    else if (readable is null) readable = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { if (readable is null) readable = false; }
            }
            result = Result(true, count, limited, readable, readable == false
                ? "Folder enumeration succeeded, but sampled audio was unreadable or empty. Full validation is still required."
                : "Folder enumeration succeeded. This limited check does not decode audio or prove access before logon.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            result = Result(false, 0, false, null, "The engine account cannot enumerate this local folder. Check the drive and folder permissions.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, System.Text.StringBuilder name, int length);
}
