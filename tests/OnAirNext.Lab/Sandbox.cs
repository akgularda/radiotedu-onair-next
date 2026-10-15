using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml.Linq;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal sealed class Sandbox : IAsyncDisposable
{
    private readonly string _icecastRoot, _configuration;
    private Process? _server;
    public int Port { get; }
    public string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    public string Root { get; }
    public Sandbox(string icecastRoot, string root)
    {
        _icecastRoot = icecastRoot; Root = root;
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "origin-logs"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        _configuration = Path.Combine(root, "local.icecast.xml");
        var document = new XDocument(new XElement("icecast",
            new XElement("location", "Isolated OnAir Next development"), new XElement("admin", "test@localhost"),
            new XElement("hostname", "localhost"),
            new XElement("limits", new XElement("clients", 64), new XElement("sources", 32),
                new XElement("queue-size", 524288), new XElement("source-timeout", 4), new XElement("burst-size", 8192)),
            new XElement("authentication", new XElement("source-password", Password),
                new XElement("relay-password", Convert.ToHexString(RandomNumberGenerator.GetBytes(24))),
                new XElement("admin-user", "admin"), new XElement("admin-password", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))),
            new XElement("listen-socket", new XElement("port", Port), new XElement("bind-address", "127.0.0.1")),
            new XElement("fileserve", 1),
            new XElement("paths", new XElement("basedir", icecastRoot), new XElement("logdir", Path.Combine(root, "origin-logs")),
                new XElement("webroot", Path.Combine(icecastRoot, "web")), new XElement("adminroot", Path.Combine(icecastRoot, "admin")),
                new XElement("mime-types", Path.Combine(icecastRoot, "mime.types"))),
            new XElement("logging", new XElement("accesslog", "access.log"), new XElement("errorlog", "error.log"), new XElement("loglevel", 2))));
        document.Save(_configuration);
    }

    public async Task StartAsync()
    {
        if (_server is not null) throw new InvalidOperationException("Test origin already owned.");
        _server = ChildProcess.Start(Path.Combine(_icecastRoot, "bin", "icecast.exe"), ["-c", _configuration], output: true, role: "test-origin");
        _ = _server.StandardOutput.ReadToEndAsync();
        var errors = ChildProcess.DrainErrorAsync(_server);
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (_server.HasExited) throw new IOException("Owned test Icecast exited: " + await errors);
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{Port}/status-json.xsl");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Loopback test origin did not become ready.");
    }

    public async Task StopAsync()
    {
        if (_server is null) return;
        var owned = _server; _server = null;
        await ChildProcess.StopAsync(owned);
    }
    public async ValueTask DisposeAsync() => await StopAsync();

    public OutputSettings Output(string id, CodecProfile profile = CodecProfile.AacLc192)
        => new(id, "127.0.0.1", Port, "/next-" + id, Password, profile);

    public static IReadOnlyList<MediaItem> Fixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        var definitions = new[] { ("song-a", 440, 97776, ContentKind.Music), ("advert", 880, 34224, ContentKind.Advertisement),
            ("sweeper", 1320, 60048, ContentKind.Sweeper), ("song-b", 660, 96528, ContentKind.Music) };
        var result = new List<MediaItem>();
        foreach (var (id, frequency, samples, kind) in definitions)
        {
            var path = Path.Combine(directory, id + ".wav");
            using var file = new BinaryWriter(File.Create(path));
            file.Write("RIFF"u8); file.Write(36 + samples * 4); file.Write("WAVEfmt "u8);
            file.Write(16); file.Write((short)1); file.Write((short)2); file.Write(48000); file.Write(192000);
            file.Write((short)4); file.Write((short)16); file.Write("data"u8); file.Write(samples * 4);
            for (var i = 0; i < samples; i++)
            {
                var value = (short)(7000 * Math.Sin(2 * Math.PI * frequency * i / 48000));
                file.Write(value); file.Write(value);
            }
            result.Add(new MediaItem(id, path, "RadioTEDU synthetic fixture", id, kind));
        }
        return result;
    }
}
