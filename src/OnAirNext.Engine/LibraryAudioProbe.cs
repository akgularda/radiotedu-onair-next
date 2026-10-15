using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed record AudioMetadata(string Artist, string Title, double Seconds);
public sealed class LibraryValidationUnavailableException(string message, Exception inner) : IOException(message, inner);

// Import work is serialized by StationLibrary, outside the programme clock.
public sealed class LibraryAudioProbe(string ffmpeg) : IAsyncDisposable
{
    private Process? _cleanupPending;
    private bool _disposed;
    public async Task<AudioMetadata> VerifyAsync(string path, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var text = await CaptureAsync(ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries",
            "format=duration:format_tags=artist,title:stream=codec_type,duration", "-of", "json", path], TimeSpan.FromSeconds(20), token);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidDataException("The selected file has no audio stream.");
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var duration = Read(format, "duration");
        if (duration.Length == 0) duration = Read(streams[0], "duration");
        if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds > 604800 || !double.IsFinite(seconds))
            throw new InvalidDataException("The selected audio has no valid duration.");
        // Probe metadata alone is not a decode validation. Decode the complete audio stream.
        await CaptureAsync(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode",
            "-threads", "1", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-threads", "1", "-f", "null", "-"],
            TimeSpan.FromSeconds(Math.Clamp(seconds / 2, 60, 600)), token);
        var tags = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("tags", out var value) ? value : default;
        var fallback = Path.GetFileNameWithoutExtension(path);
        var suffix = fallback.LastIndexOf("--", StringComparison.Ordinal);
        if (suffix >= 0 && fallback.Length - suffix == 66 && fallback[(suffix + 2)..].All(char.IsAsciiHexDigit)) fallback = fallback[..suffix];
        return new(Clean(Read(tags, "artist"), 128), Clean(Read(tags, "title"), 256) is { Length: > 0 } title
            ? title : Clean(fallback, 256), seconds);
    }

    private static string Read(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static string Clean(string value, int limit) => new(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());

    private async Task<string> CaptureAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        if (_cleanupPending is { } pending)
        {
            try { await ChildProcess.StopAsync(pending); _cleanupPending = null; }
            catch (Exception ex) { throw new LibraryValidationUnavailableException("Previous validation worker cleanup is pending.", ex); }
        }
        Process process;
        try { process = ChildProcess.Start(executable, arguments, output: true, role: "library-validation"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new LibraryValidationUnavailableException("The audio validation tool is unavailable.", ex); }
        try
        {
            try { process.PriorityClass = ProcessPriorityClass.Idle; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ChildProcess.DrainErrorAsync(process);
            // A bounded-reader failure must also stop a worker that is blocked writing stdout.
            var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(output, exited) == output) await output;
            await exited;
            var diagnostic = await error;
            if (process.ExitCode != 0) throw new InvalidDataException("Audio validation failed: " + diagnostic[..Math.Min(500, diagnostic.Length)]);
            return await output;
        }
        finally
        {
            try { await ChildProcess.StopAsync(process); }
            catch (Exception ex)
            {
                _cleanupPending = process;
                throw new LibraryValidationUnavailableException("Audio validation worker cleanup is pending; another worker will not be started.", ex);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[2048]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + read > 65536) throw new InvalidDataException("Audio metadata exceeds the import limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_cleanupPending is not { } pending) return;
        await ChildProcess.StopAsync(pending); _cleanupPending = null;
    }
}
