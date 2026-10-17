using System.Text;

namespace OnAirNext.Installation;

// Only these fixed labels may cross the helper boundary. Paths and exception text never do.
public static class RuntimeSupplementReviewProgress
{
    public const string Prefix = "ONAIR_AUDIO_REVIEW:";
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ConsoleTimeout = TimeSpan.FromMinutes(11);

    public static string? Describe(string stage) => stage switch
    {
        "ownership" => "Checking the original installation and operator ownership",
        "service" => "Checking the separate service and its unarmed status",
        "installed-image" => "Verifying the installed application files",
        "candidate-image" => "Verifying the complete candidate application package",
        "provider" => "Verifying the original complete audio tools provider",
        "analyzer" => "Checking the media analyzer version",
        "provider-readback" => "Verifying the provider again before saving the review",
        "installed-tools" => "Verifying that the original codec files are unchanged",
        "final-ownership" => "Rechecking installation ownership and unarmed service state",
        _ => null
    };

    public static string FailureMessage(Exception error, string? stage)
    {
        var reason = error switch
        {
            OperationCanceledException => "Audio tools review reached its time limit",
            UnauthorizedAccessException => "Audio tools review could not read a protected file",
            ArgumentException or InvalidOperationException => "Audio tools review rejected the package or installation state",
            _ => "Audio tools review could not finish verification"
        };
        var detail = stage is null ? null : Describe(stage);
        return reason + (detail is null ? "." : ": " + detail + ".") +
            " No files were installed. Keep the original provider and check the displayed review step before retrying.";
    }

    public sealed class Reader(Action<string> report)
    {
        private readonly StringBuilder _line = new();
        private int _characters;
        private bool _discard;
        public void Append(ReadOnlySpan<char> input)
        {
            if (input.Length > 524288 - _characters) throw new IOException("Tools review output exceeded its bound.");
            _characters += input.Length;
            foreach (var character in input)
            {
                if (character == '\n')
                {
                    if (!_discard)
                    {
                        var line = _line.ToString().TrimEnd('\r');
                        if (line.StartsWith(Prefix, StringComparison.Ordinal))
                        {
                            var stage = line[Prefix.Length..];
                            if (Describe(stage) is not null) report(stage);
                        }
                    }
                    _line.Clear(); _discard = false;
                }
                else if (!_discard)
                {
                    if (_line.Length == 128) { _line.Clear(); _discard = true; }
                    else _line.Append(character);
                }
            }
        }
    }
}
