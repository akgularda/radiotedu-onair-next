using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OnAirNext.Engine;

// One coalesced current value, independent of the encoder and audio transport.
internal sealed class IcecastMetadataPublisher : IAsyncDisposable
{
    private readonly OutputSettings _settings;
    private readonly Action? _requireOwnership;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _retry;
    private readonly bool _disabled;
    private Task? _run;
    private volatile bool _connected;
    private sealed record DesiredMetadata(string Song);
    private volatile DesiredMetadata? _desired;
    private DesiredMetadata? _acknowledged;
    private volatile string? _published, _error;
    private volatile string _state;
    private long _connectionVersion, _acknowledgedConnection = -1, _updates, _failures;

    internal IcecastMetadataPublisher(OutputSettings settings, Action? requireOwnership = null,
        HttpMessageHandler? handler = null, TimeSpan? retry = null)
    {
        if (settings.Commissioned)
        {
            if (requireOwnership is null) throw new ArgumentException("Metadata requires the same exclusive source ownership guard.");
            settings.ValidateCommissioned();
        }
        else settings.Validate();
        _settings = settings; _requireOwnership = requireOwnership;
        // Preserve the former application's unconditional Lo-Fi privacy policy.
        var suppressed = settings.MetadataSuppressed || settings.Commissioned && (settings.Mount is "/lofi" or "/lofi-low");
        // Ogg carries its own in-band comments, not ICY URL metadata updates.
        _disabled = suppressed || settings.Profile == CodecProfile.OggFlac;
        _state = suppressed ? "Suppressed" : _disabled ? "NotApplicable" : "Waiting";
        _retry = retry ?? TimeSpan.FromSeconds(2);
        if (_retry < TimeSpan.FromMilliseconds(10) || _retry > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(retry));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
    }

    internal void Start()
    {
        if (_run is not null) throw new InvalidOperationException("Metadata publisher already started.");
        _run = _disabled ? Task.CompletedTask : Task.Run(RunAsync);
    }

    internal void SetConnected(bool connected)
    {
        _connected = connected;
        if (_disabled || _stop.IsCancellationRequested) return;
        if (connected) { Interlocked.Increment(ref _connectionVersion); Wake(); }
        else _state = "Waiting";
    }

    internal void SetNowPlaying(string artist, string title)
    {
        if (_disabled || _stop.IsCancellationRequested) return;
        _desired = new(Format(artist, title, _settings.Information?.Name ?? "RadioTEDU"));
        Wake();
    }

    internal static string Format(string? artist, string? title, string? fallback)
    {
        static string Clean(string? value)
        {
            var text = new StringBuilder(); var count = 0;
            foreach (var rune in (value ?? "").EnumerateRunes())
            {
                if (++count > 512) break;
                if (!Rune.IsControl(rune)) text.Append(rune);
            }
            return text.ToString().Trim();
        }
        var a = Clean(artist); var t = Clean(title);
        return Clean(t.Length == 0 ? (a.Length == 0 ? fallback : a) : a.Length == 0 ? t : a + " - " + t);
    }

    internal MetadataSnapshot Snapshot() => new(_state, _published, Interlocked.Read(ref _updates), Interlocked.Read(ref _failures), _error);

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _wake.WaitAsync(_stop.Token);
                while (_connected && _desired is { } desired)
                {
                    var connection = Interlocked.Read(ref _connectionVersion);
                    if (ReferenceEquals(desired, _acknowledged) && connection == _acknowledgedConnection) break;
                    var song = desired.Song;
                    try
                    {
                        _state = "Updating"; _requireOwnership?.Invoke();
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(3));
                        var address = new UriBuilder("http", _settings.Host, _settings.Port, "/admin/metadata")
                        { Query = "mode=updinfo&charset=UTF-8&mount=" + Uri.EscapeDataString(_settings.Mount) + "&song=" + Uri.EscapeDataString(song) };
                        using var request = new HttpRequestMessage(HttpMethod.Get, address.Uri);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(_settings.SourceUsername + ":" + _settings.SourcePassword)));
                        request.Headers.UserAgent.ParseAdd("RadioTEDU-OnAirNext");
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        if (!response.IsSuccessStatusCode) throw new MetadataRejectedException($"Metadata rejected (HTTP {(int)response.StatusCode}).");
                        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                        using var body = new MemoryStream(); var buffer = new byte[1024];
                        while (true)
                        {
                            var read = await stream.ReadAsync(buffer, deadline.Token);
                            if (read == 0) break;
                            if (body.Length + read > 8192) throw new MetadataRejectedException("Metadata acknowledgement exceeded its limit.");
                            body.Write(buffer, 0, read);
                        }
                        var acknowledgement = Encoding.UTF8.GetString(body.ToArray());
                        if (!IsAcceptedAcknowledgement(acknowledgement))
                            throw new MetadataRejectedException("Metadata acknowledgement was not successful.");
                        _published = song; _acknowledged = desired; _acknowledgedConnection = connection; _error = null;
                        Interlocked.Increment(ref _updates); _state = "Published";
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Never expose credentials, response bodies or exception URLs.
                        _error = ex is MetadataRejectedException ? ex.Message : "Metadata update unavailable; retrying independently of audio.";
                        Interlocked.Increment(ref _failures); _state = "Retrying";
                        await _wake.WaitAsync(_retry, _stop.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { if (!_disabled) _state = "Stopped"; }
    }

    internal static bool IsAcceptedAcknowledgement(string body)
    {
        if (body.Trim().Equals("Metadata updated successfully", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var text = new StringReader(body);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8192 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name.LocalName != "iceresponse") return false;
            // Icecast 2.5 also includes a modules tree. Check the direct response
            // fields rather than assuming a fixed serialization or field order.
            var messages = root.Elements().Where(e => e.Name.LocalName == "message").ToArray();
            var codes = root.Elements().Where(e => e.Name.LocalName == "return").ToArray();
            return messages.Length == 1 && codes.Length == 1 && codes[0].Value.Trim() == "1" &&
                messages[0].Value.Trim().Equals("Metadata update successful", StringComparison.OrdinalIgnoreCase);
        }
        catch (XmlException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); Wake();
        if (_run is not null) await _run;
        _http.Dispose(); _wake.Dispose(); _stop.Dispose();
    }

    private sealed class MetadataRejectedException(string message) : Exception(message);
}
