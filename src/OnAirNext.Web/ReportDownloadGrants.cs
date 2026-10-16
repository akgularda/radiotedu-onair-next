using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnAirNext.Web;

internal sealed class ReportDownloadGrants
{
    internal sealed record Grant(string Engine, string ReportId, DateTimeOffset Expires, long Started);
    private readonly Dictionary<string, Grant> _grants = [];
    private readonly object _gate = new();
    public string Create(string engine, string id, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Choose a completed report.");
        lock (_gate)
        {
            foreach (var old in _grants.Where(pair => pair.Value.Expires <= now || Stopwatch.GetElapsedTime(pair.Value.Started) >= TimeSpan.FromSeconds(30)).Select(pair => pair.Key).ToArray()) _grants.Remove(old);
            if (_grants.Count >= 16) throw new InvalidOperationException("Too many pending downloads. Wait briefly before retrying.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _grants.Add(Key(token), new(engine, id, now.AddSeconds(30), Stopwatch.GetTimestamp())); return token;
        }
    }
    public Grant? Claim(string token, DateTimeOffset now)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) return null;
        lock (_gate)
            return _grants.Remove(Key(token), out var grant) && grant.Expires > now && Stopwatch.GetElapsedTime(grant.Started) < TimeSpan.FromSeconds(30) ? grant : null;
    }
    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
