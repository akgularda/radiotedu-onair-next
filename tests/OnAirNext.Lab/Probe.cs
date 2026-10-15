using System.Diagnostics;
using System.Runtime.InteropServices;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal sealed record ProbeResult(string Mount, double DecodedSeconds, int SilentWindows, int LongestSilentRun,
    long ToneTransitions, long UnexpectedToneTransitions, long LiveToneWindows, IReadOnlyList<int> FirstTones, string? Error);

internal sealed class Probe : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<string> _stderr;
    private readonly Task _reader;
    private readonly string _mount;
    private readonly bool _analyzeLive;
    private long _liveToneWindows;
    private readonly CodecPipe _pcm = new(System.IO.Pipes.PipeDirection.In);
    private long _windows, _transitions, _unexpected;
    private int _silence, _longestSilence, _currentSilence, _tone = -1, _candidate = -1, _candidateWindows;
    private readonly List<int> _tones = new();
    private string? _error;
    private static readonly int[] Frequencies = [440, 880, 1320, 660];
    public Probe(string ffmpeg, int port, string mount, bool analyzeLive = false)
    {
        _mount = mount; _analyzeLive = analyzeLive;
        _process = ChildProcess.Start(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-rw_timeout", "5000000",
            "-analyzeduration", "200000", "-probesize", "32768", "-i", $"http://127.0.0.1:{port}{mount}", "-vn", "-ac", "1", "-ar", "48000",
            "-f", "s16le", "-c:a", "pcm_s16le", _pcm.Path], role: "listener-probe");
        _stderr = ChildProcess.DrainErrorAsync(_process);
        _reader = ReadAsync();
    }

    private async Task ReadAsync()
    {
        var bytes = new byte[AudioFormat.SamplesPerBlock * 2];
        try
        {
            var connected = _pcm.ConnectAsync(_stop.Token);
            var exit = _process.WaitForExitAsync(_stop.Token);
            if (await Task.WhenAny(connected, exit) == exit && !connected.IsCompleted)
                throw new IOException("Listener exited before opening its PCM pipe: " + await _stderr);
            await connected;
            while (!_stop.IsCancellationRequested)
            {
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = await _pcm.Stream.ReadAsync(bytes.AsMemory(length), _stop.Token);
                    if (read == 0) break;
                    length += read;
                }
                if (length != bytes.Length) break;
                Analyze(bytes);
            }
            if (!_stop.IsCancellationRequested)
            {
                await _process.WaitForExitAsync();
                var error = await _stderr;
                _error = _process.ExitCode == 0 ? null : "Listener decode ended: " + error[..Math.Min(error.Length, 400)];
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) _error = ex.GetType().Name; }
    }

    private void Analyze(byte[] bytes)
    {
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        var windows = Interlocked.Increment(ref _windows);
        if (windows <= 10) return; // Decoder priming is measured separately from the continuing stream.
        double squares = 0;
        foreach (var sample in samples) squares += (double)sample * sample;
        if (Math.Sqrt(squares / samples.Length) < 200)
        {
            _silence++; _currentSilence++; _longestSilence = Math.Max(_longestSilence, _currentSilence);
        }
        else _currentSilence = 0;
        var best = -1; double maximum = 0;
        for (var i = 0; i < Frequencies.Length; i++)
        {
            var coefficient = 2 * Math.Cos(2 * Math.PI * Frequencies[i] / AudioFormat.SampleRate);
            double a = 0, b = 0;
            foreach (var sample in samples) { var next = sample + coefficient * a - b; b = a; a = next; }
            var power = a * a + b * b - coefficient * a * b;
            if (power > maximum) { maximum = power; best = i; }
        }
        if (_analyzeLive)
        {
            var coefficient = 2 * Math.Cos(2 * Math.PI * 1760 / AudioFormat.SampleRate);
            double a = 0, b = 0;
            foreach (var sample in samples) { var next = sample + coefficient * a - b; b = a; a = next; }
            if (a * a + b * b - coefficient * a * b > maximum) Interlocked.Increment(ref _liveToneWindows);
        }
        if (best != _candidate) { _candidate = best; _candidateWindows = 1; }
        else _candidateWindows++;
        if (_candidateWindows < 3 || best < 0 || best == _tone) return;
        if (_tone >= 0)
        {
            _transitions++;
            if (best != (_tone + 1) % Frequencies.Length) _unexpected++;
        }
        _tone = best;
        lock (_tones) if (_tones.Count < 64) _tones.Add(Frequencies[best]);
    }

    public ProbeResult Snapshot()
    {
        lock (_tones) return new(_mount, Interlocked.Read(ref _windows) * 0.02, _silence, _longestSilence,
            _transitions, _unexpected, Interlocked.Read(ref _liveToneWindows), _tones.ToArray(), _error);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await ChildProcess.StopAsync(_process);
        await _reader;
        _pcm.Dispose();
        _stop.Dispose();
    }
}
