using System.Diagnostics;
using System.Collections.Concurrent;

namespace OnAirNext.Engine;

public static class ChildProcess
{
    private static readonly ConcurrentDictionary<int, string> Owned = new();
    public static IReadOnlyDictionary<int, string> ActiveProcesses => Owned;
    public static Task<string> DrainErrorAsync(Process process) => Task.Factory.StartNew(() =>
    {
        // Keep synchronous stderr reads off the audio scheduler's pool and bound diagnostic retention.
        var tail = new System.Text.StringBuilder(4096);
        var buffer = new char[512];
        try
        {
            int read;
            while ((read = process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        return tail.ToString();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    public static Process Start(string executable, IEnumerable<string> arguments, bool input = false, bool output = false, string role = "codec")
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input, RedirectStandardOutput = output, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Could not start an owned codec worker.");
        Owned[process.Id] = role;
        return process;
    }

    public static async Task StopAsync(Process process)
    {
        var pid = process.Id;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        finally { Owned.TryRemove(pid, out _); process.Dispose(); }
    }

    public static async Task<string> CaptureAsync(string executable, params string[] arguments)
    {
        var process = Start(executable, arguments, output: true, role: "capability-check");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = DrainErrorAsync(process);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Codec capability check failed ({process.ExitCode}): {error[..Math.Min(error.Length, 500)]}");
            return await stdout;
        }
        finally { await StopAsync(process); }
    }
}

public static class EncoderCapabilities
{
    public static async Task ValidateAsync(string ffmpeg, IEnumerable<OutputSettings> outputs)
    {
        var encoders = await ChildProcess.CaptureAsync(ffmpeg, "-hide_banner", "-encoders");
        foreach (var profile in outputs.Select(x => x.Profile).Distinct())
        {
            var required = profile switch { CodecProfile.HeAacV2_64 => "libfdk_aac", CodecProfile.OggFlac => "flac", _ => "aac" };
            if (!encoders.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() == required))
                throw new NotSupportedException($"Configured {profile} needs encoder {required}; quality substitution is prohibited.");
        }
    }
}
