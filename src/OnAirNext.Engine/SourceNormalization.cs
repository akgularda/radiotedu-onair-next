using System.Globalization;
using System.Text.RegularExpressions;

namespace OnAirNext.Engine;

// A completed full-file scan, tied to the source bytes. Never a final-mix/output compliance verdict.
public sealed record SourceLoudness(string ContentSha256, long Bytes, long ModifiedTicks, double Seconds,
    double? IntegratedLufs, double? TruePeakDbtp, double? LoudnessRangeLu, DateTimeOffset MeasuredAtUtc,
    string Algorithm = "ffmpeg-ebur128-stereo48k-v1")
{
    public void Validate()
    {
        if (Algorithm != "ffmpeg-ebur128-stereo48k-v1" || string.IsNullOrEmpty(ContentSha256) || ContentSha256.Length != 64 || !ContentSha256.All(char.IsAsciiHexDigit) ||
            Bytes <= 0 || ModifiedTicks <= 0 || !double.IsFinite(Seconds) || Seconds <= 0 || Seconds > 604800 ||
            !Valid(IntegratedLufs, -70, 80) || !Valid(TruePeakDbtp, -200, 80) || !Valid(LoudnessRangeLu, 0, 100) ||
            IntegratedLufs is not null && TruePeakDbtp is null || MeasuredAtUtc == default)
            throw new ArgumentException("Invalid full-file loudness measurement.");
    }
    private static bool Valid(double? value, double min, double max) => value is null || double.IsFinite(value.Value) && value >= min && value <= max;

    public static SourceLoudness FromSummary(string diagnostic, string sha256, long bytes, long modifiedTicks, double seconds)
    {
        var start = diagnostic.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Full-file loudness summary is missing; retry audio validation.");
        var summary = diagnostic[start..];
        static double? Metric(string summary, string label, string unit, bool allowInfinity = false)
        {
            var match = Regex.Match(summary, @"(?m)^\s*" + Regex.Escape(label) + @":\s*(-?\d+(?:\.\d+)?|-inf)\s+" + Regex.Escape(unit) + @"\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("Incomplete full-file loudness summary: " + label);
            if (allowInfinity && match.Groups[1].Value == "-inf") return null;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidDataException("Invalid full-file loudness value: " + label);
            return value;
        }
        var integrated = Metric(summary, "I", "LUFS");
        // FFmpeg reports its -70 LUFS floor for ungated silence and sub-400 ms files.
        if (integrated <= -70) integrated = null;
        var peak = Metric(summary, "Peak", "dBFS", allowInfinity: true);
        var range = Metric(summary, "LRA", "LU");
        var result = new SourceLoudness(sha256, bytes, modifiedTicks, seconds, integrated, peak,
            seconds >= 60 && integrated is not null ? range : null, DateTimeOffset.UtcNow);
        result.Validate(); return result;
    }
}

public sealed record SourceGain(double TargetLufs, double SourceCeilingDbtp, double RequestedDb, double AppliedDb,
    string State, double? PredictedLufs, double? PredictedTruePeakDbtp)
{
    public double Linear => Math.Pow(10, AppliedDb / 20);
}

public static class SourceNormalization
{
    public const double TargetLufs = -23.0;
    // 0.4 dB reserve for finite oversampled measurement/rounding. Encoded outputs still require independent checks.
    public const double SourceCeilingDbtp = -1.4;
    public const double MaximumBoostDb = 12;
    public const string DecodeFormat = "aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo";

    public static SourceGain Decide(SourceLoudness? measurement)
    {
        if (measurement is null) return new(TargetLufs, SourceCeilingDbtp, 0, 0, "Unmeasured", null, null);
        measurement.Validate();
        if (measurement.IntegratedLufs is not double loudness)
            return new(TargetLufs, SourceCeilingDbtp, 0, Math.Min(0, SourceCeilingDbtp - (measurement.TruePeakDbtp ?? SourceCeilingDbtp)),
                "No gated programme", null, measurement.TruePeakDbtp is double p ? Math.Min(p, SourceCeilingDbtp) : null);
        var requested = TargetLufs - loudness;
        var peakLimit = SourceCeilingDbtp - measurement.TruePeakDbtp!.Value;
        var applied = Math.Min(requested, Math.Min(MaximumBoostDb, peakLimit));
        var state = applied < requested - 0.000001 ? (peakLimit < Math.Min(requested, MaximumBoostDb) ? "Peak constrained" : "Boost constrained") : "Target gain";
        return new(TargetLufs, SourceCeilingDbtp, requested, applied, state, loudness + applied, measurement.TruePeakDbtp + applied);
    }
}
