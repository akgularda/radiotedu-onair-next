using System.Diagnostics;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OnAirNext.Engine;

public static class ChildProcess
{
    private sealed class OwnedChild(Process process, int id, string role)
    {
        public Process Process { get; } = process;
        public int Id { get; } = id;
        public string Role { get; } = role;
        public SemaphoreSlim StopGate { get; } = new(1, 1);
        public bool Released { get; set; }
        public string? CleanupError { get; set; }
    }
    private static readonly ConcurrentDictionary<int, OwnedChild> Owned = new();
    private static readonly ConditionalWeakTable<Process, OwnedChild> Objects = new();
    public static IReadOnlyDictionary<int, string> ActiveProcesses => Owned.ToDictionary(x => x.Key,
        x => x.Value.Role + (x.Value.CleanupError is null ? "" : " (cleanup pending: " + x.Value.CleanupError + ")"));
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
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start an owned codec worker.");
        }
        catch { process.Dispose(); throw; }
        var child = new OwnedChild(process, process.Id, role);
        Objects.Add(process, child);
        Owned[child.Id] = child;
        return process;
    }

    public static async Task StopAsync(Process process)
    {
        if (!Objects.TryGetValue(process, out var child))
            throw new ArgumentException("Only a process created and tracked by this engine may be stopped.", nameof(process));
        await child.StopGate.WaitAsync();
        try
        {
            if (child.Released) return;
            try
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // HasExited/Kill can race with a natural exit; a live failed kill is not success.
                    if (!process.HasExited) throw;
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (!process.HasExited) throw new IOException("Owned process exit was not confirmed.");
                child.Released = true;
                // A reused PID must not let an older session remove the newer child's record.
                ((ICollection<KeyValuePair<int, OwnedChild>>)Owned).Remove(new(child.Id, child));
                process.Dispose();
            }
            catch (Exception ex)
            {
                child.CleanupError = ex.GetType().Name;
                // Keep the handle and ownership record available for a later cleanup attempt.
                throw;
            }
        }
        finally { child.StopGate.Release(); }
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
        ValidateListing(encoders, outputs);
    }

    public static void ValidateListing(string encoders, IEnumerable<OutputSettings> outputs)
    {
        foreach (var profile in outputs.Select(x => x.Profile).Distinct())
        {
            var required = profile switch { CodecProfile.AacLc192 or CodecProfile.HeAacV2_64 => "libfdk_aac", CodecProfile.OggFlac => "flac", _ => throw new NotSupportedException("Unsupported configured codec profile.") };
            if (!encoders.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() == required))
                throw new NotSupportedException($"Configured {profile} needs encoder {required}; quality substitution is prohibited.");
        }
    }
}
