using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnAirNext.Engine;
using OnAirNext.MeasurementFixtures;

// A disposable-CI-only instrument comparison, not a codec/output/fleet or production acceptance test.
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || args.Length != 1)
    throw new InvalidOperationException("Run this synthetic instrument qualification only on a disposable CI runner.");
var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var results = new List<object>(); var passed = true;
var referenceVersion = await RunAsync(["-version"]);
var cases = new (string Name, ToneSegment[] Segments)[]
{
    ("stereo-calibration", [new(20, -23)]),
    ("absolute-and-relative-gates", [new(10, -72), new(10, -36), new(60, -23), new(10, -36), new(10, -72)]),
    ("energy-gated-programme", [new(20, -26), new(20.1, -20), new(20, -26)]),
    ("loudness-range", [new(20, -50), new(20, -35), new(20, -20), new(20, -35), new(20, -50)]),
    ("opposite-phase-stereo", [new(20, -23, InvertRight: true)]),
    ("true-peak-phase-45", [new(3, 20 * Math.Log10(0.5), 12000, 45, true)]),
    ("true-peak-phase-60", [new(3, 20 * Math.Log10(0.5), 8000, 60, true)]),
    ("true-peak-phase-67.5", [new(3, 20 * Math.Log10(0.5), 6000, 67.5, true)]),
    ("above-full-scale-float", [new(3, 20 * Math.Log10(1.41), 12000, 45, true)])
};
foreach (var test in cases)
{
    var file = Path.Combine(root, test.Name + ".synthetic-f32le");
    try
    {
        var meter = new StereoLoudnessMeter48k(TimeSpan.FromMinutes(3));
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            StereoReferenceSignals.Render(block =>
            {
                meter.Feed(block.Span);
                stream.Write(MemoryMarshal.AsBytes(block.Span));
            }, test.Segments);
        meter.Finish(); var reading = meter.Read();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        var output = await RunAsync(["-hide_banner", "-nostdin", "-nostats", "-threads", "1", "-filter_threads", "1",
            "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", file, "-af", "ebur128=peak=true", "-f", "null", "-"]);
        var summaryAt = output.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (summaryAt < 0) throw new InvalidDataException("The independent meter did not produce a final summary.");
        var summary = output[summaryAt..];
        var integrated = Parse(summary, @"\bI:\s*([-+\d.]+)\s*LUFS");
        var range = Parse(summary, @"\bLRA:\s*([-+\d.]+)\s*LU");
        var truePeak = Parse(summary, @"\bPeak:\s*([-+\d.]+)\s*dBFS");
        var integratedDelta = Math.Abs((reading.IntegratedLufs ?? throw new InvalidDataException("No integrated measurement.")) - integrated);
        double? rangeDelta = reading.RangeSettled
            ? Math.Abs((reading.LoudnessRangeLu ?? throw new InvalidDataException("No range measurement.")) - range)
            : null;
        var peakDelta = Math.Abs((reading.TruePeakDbtp ?? throw new InvalidDataException("No true peak measurement.")) - truePeak);
        var ok = integratedDelta <= 0.2 && (rangeDelta is null || rangeDelta <= 1) && peakDelta <= 0.4;
        passed &= ok;
        results.Add(new { test.Name, Passed = ok, SampleSha256 = hash, Reading = reading,
            Reference = new { IntegratedLufs = integrated, LoudnessRangeLu = range, TruePeakDbtp = truePeak },
            RangeCompared = reading.RangeSettled,
            RangeComparisonNote = reading.RangeSettled ? "At least 60 seconds measured." : "Not assessed: LRA is not an acceptance criterion for programmes shorter than one minute.",
            Difference = new { IntegratedLu = integratedDelta, RangeLu = rangeDelta, PeakDb = peakDelta } });
        Console.WriteLine($"{test.Name}: {(ok ? "passed" : "FAILED")}; I difference {integratedDelta:F3} LU, peak difference {peakDelta:F3} dB");
    }
    catch (Exception ex)
    {
        passed = false; results.Add(new { test.Name, Passed = false, Error = ex.Message });
        Console.WriteLine(test.Name + ": FAILED");
    }
    finally { if (File.Exists(file)) File.Delete(file); }
}
var limiterResults = await LimiterReferenceChecks.RunAsync(root, RunAsync);
results.AddRange(limiterResults); passed &= limiterResults.All(x=>x.Passed);
await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new
{
    Source = Environment.GetEnvironmentVariable("GITHUB_SHA"), AtUtc = DateTimeOffset.UtcNow,
    Completed = results.Count == cases.Length + LimiterReferenceChecks.CaseCount, Passed = passed, Cases = results,
    ReferenceVersion = referenceVersion.Split('\n')[0].Trim(), SampleRate = 48000, Channels = 2,
    Scope = "Synthetic stereo meter and six oversampled final-limiter signal comparisons only. No encoded-output limiter, broadcasting PC, UI, full programme loudness workflow or full EBU test-set acceptance."
}, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

static double Parse(string text, string pattern)
{
    var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    if (!match.Success) throw new InvalidDataException("Missing independent measurement field.");
    return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
}
static async Task<string> RunAsync(string[] arguments)
{
    var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch the independent meter.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
    var output = await stdout + await stderr;
    if (process.ExitCode != 0 || output.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Independent meter failed or exceeded its output budget.");
    return output;
}
