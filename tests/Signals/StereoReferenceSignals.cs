namespace OnAirNext.MeasurementFixtures;

public sealed record ToneSegment(double Seconds, double PeakDbfs, double Frequency = 1000,
    double PhaseDegrees = 0, bool Taper = false, bool InvertRight = false);

// Synthetic engineering signals only. Never reads, records or connects to a broadcast.
public static class StereoReferenceSignals
{
    public static void Render(Action<ReadOnlyMemory<float>> receive, params ToneSegment[] segments)
    {
        var buffer = new float[1920];
        foreach (var segment in segments)
        {
            var frames = checked((int)Math.Round(segment.Seconds * 48000));
            var amplitude = Math.Pow(10, segment.PeakDbfs / 20);
            for (var offset = 0; offset < frames;)
            {
                var count = Math.Min(960, frames - offset);
                for (var i = 0; i < count; i++)
                {
                    var position = offset + i;
                    var taper = segment.Taper ? Math.Clamp(Math.Min(position, frames - 1 - position) / 480.0, 0, 1) : 1;
                    var value = (float)(amplitude * taper * Math.Sin(2 * Math.PI * segment.Frequency * position / 48000
                        + segment.PhaseDegrees * Math.PI / 180));
                    buffer[i * 2] = value;
                    buffer[i * 2 + 1] = segment.InvertRight ? -value : value;
                }
                receive(buffer.AsMemory(0, count * 2));
                offset += count;
            }
        }
    }
}
