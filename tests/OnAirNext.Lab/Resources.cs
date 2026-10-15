using System.Diagnostics;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal sealed class Resources : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public long HostPeakBytes { get; private set; }
    public long CodecPeakBytes { get; private set; }
    public long ListenerPeakBytes { get; private set; }
    public double PeakCpuPercentOfMachine { get; private set; }
    public double AverageCpuPercentOfMachine { get; private set; }
    public long FirstSteadyWorkingSetBytes { get; private set; }
    public long LastWorkingSetBytes { get; private set; }
    public Resources() => _run = Task.Run(SampleAsync);
    private async Task SampleAsync()
    {
        var previous = new Dictionary<int, TimeSpan>();
        var clock = Stopwatch.StartNew(); var last = clock.Elapsed;
        double totalCpu = 0, measuredSeconds = 0;
        while (!_stop.IsCancellationRequested)
        {
            long codec = 0, listener = 0; double cpu = 0;
            using var host = Process.GetCurrentProcess(); host.Refresh();
            HostPeakBytes = Math.Max(HostPeakBytes, host.WorkingSet64);
            var processList = ChildProcess.ActiveProcesses.ToArray().Append(new KeyValuePair<int, string>(host.Id, "lab-host")).ToArray();
            var activeIds = processList.Select(x => x.Key).ToHashSet();
            foreach (var old in previous.Keys.Where(x => !activeIds.Contains(x)).ToArray()) previous.Remove(old);
            foreach (var (pid, role) in processList)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited) continue;
                    if (role == "codec") codec += process.WorkingSet64;
                    if (role == "listener-probe") listener += process.WorkingSet64;
                    if (role is "codec" or "lab-host")
                    {
                        var time = process.TotalProcessorTime;
                        if (previous.TryGetValue(pid, out var prior)) cpu += (time - prior).TotalSeconds;
                        previous[pid] = time;
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            CodecPeakBytes = Math.Max(CodecPeakBytes, codec); ListenerPeakBytes = Math.Max(ListenerPeakBytes, listener);
            LastWorkingSetBytes = host.WorkingSet64 + codec;
            if (clock.Elapsed.TotalSeconds > 5 && FirstSteadyWorkingSetBytes == 0) FirstSteadyWorkingSetBytes = LastWorkingSetBytes;
            var elapsed = clock.Elapsed - last; last = clock.Elapsed;
            if (elapsed.TotalSeconds > 0.5)
            {
                PeakCpuPercentOfMachine = Math.Max(PeakCpuPercentOfMachine, cpu / elapsed.TotalSeconds / Environment.ProcessorCount * 100);
                totalCpu += cpu; measuredSeconds += elapsed.TotalSeconds;
                AverageCpuPercentOfMachine = totalCpu / measuredSeconds / Environment.ProcessorCount * 100;
            }
            try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public object Snapshot() => new { HostPeakBytes, CodecPeakBytes, ListenerPeakBytes, PeakCpuPercentOfMachine, AverageCpuPercentOfMachine,
        FirstSteadyWorkingSetBytes, LastWorkingSetBytes,
        LogicalProcessors = Environment.ProcessorCount,
        Note = "Host includes lab instrumentation; listener processes and test Icecast are not counted as engine codecs. This is the development PC, not the HP Z220." };
    public async ValueTask DisposeAsync() { await _stop.CancelAsync(); await _run; _stop.Dispose(); }
}
