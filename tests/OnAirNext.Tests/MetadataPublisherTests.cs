using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class MetadataPublisherTests
{
    private const string Secret = "fixture-only-not-a-source-secret";
    private static OutputSettings Settings() => new("metadata", "127.0.0.1", 18980, "/next-metadata", Secret);
    private static HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
        { Content = new StringContent("<iceresponse><message>Metadata update successful</message><return>1</return></iceresponse>") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, limit.Token);
    }

    [Fact]
    public void FormattingIsBoundedUnicodeSafeAndDoesNotInjectControlCharacters()
    {
        Assert.Equal("Şebnem Ferah - Yağmurlar 🌧", IcecastMetadataPublisher.Format("Şebnem\r\n Ferah", "Yağmurlar 🌧", "Radio"));
        Assert.Equal("Radio", IcecastMetadataPublisher.Format(null, "\0\r\n", "Radio"));
        var text = IcecastMetadataPublisher.Format("", string.Concat(Enumerable.Repeat("🎵", 900)), "Radio");
        Assert.Equal(512, text.EnumerateRunes().Count());
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public async Task SendsEscapedSongWithSourceAuthenticationAfterConnection()
    {
        string? address = null, auth = null;
        await using var publisher = new IcecastMetadataPublisher(Settings(), handler: new Handler((request, _) =>
        { address = request.RequestUri!.AbsoluteUri; auth = request.Headers.Authorization?.Parameter; return Task.FromResult(Accepted()); }));
        publisher.Start(); publisher.SetNowPlaying("Şebnem Ferah", "Yağmurlar & günler"); publisher.SetConnected(true);
        await Until(() => publisher.Snapshot().Updates == 1);
        Assert.Contains("/admin/metadata?mode=updinfo&charset=UTF-8&mount=%2Fnext-metadata&song=", address);
        Assert.Contains("Şebnem Ferah - Yağmurlar & günler", Uri.UnescapeDataString(address!));
        Assert.Equal("source:" + Secret, Encoding.UTF8.GetString(Convert.FromBase64String(auth!)));
        Assert.Null(publisher.Snapshot().LastError);
    }

    [Fact]
    public async Task ABlockedRequestCoalescesNewTitlesWithoutBlockingTheCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        await using var publisher = new IcecastMetadataPublisher(Settings(), handler: new Handler(async (request, token) =>
        {
            requests.Enqueue(Uri.UnescapeDataString(request.RequestUri!.Query));
            if (requests.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return Accepted();
        }));
        publisher.Start(); publisher.SetConnected(true); publisher.SetNowPlaying("", "first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 1000; i++) publisher.SetNowPlaying("", "new-" + i);
        Assert.Single(requests);
        release.SetResult(); await Until(() => publisher.Snapshot().PublishedTitle == "new-999");
        Assert.Equal(2, requests.Count);
        Assert.EndsWith("song=new-999", requests.Last());
    }

    [Fact]
    public async Task SourceReconnectRepublishesTheCurrentTitle()
    {
        await using var publisher = new IcecastMetadataPublisher(Settings(), handler: new Handler((_, _) => Task.FromResult(Accepted())));
        publisher.Start(); publisher.SetNowPlaying("", "current"); publisher.SetConnected(true);
        await Until(() => publisher.Snapshot().Updates == 1);
        publisher.SetConnected(false); publisher.SetConnected(true);
        await Until(() => publisher.Snapshot().Updates == 2);
        Assert.Equal("current", publisher.Snapshot().PublishedTitle);
    }

    [Fact]
    public async Task FailedRequestRetriesTheNewestTitleAndClearsItsDiagnostic()
    {
        var requests = new ConcurrentQueue<string>();
        await using var publisher = new IcecastMetadataPublisher(Settings(), retry: TimeSpan.FromMilliseconds(30), handler: new Handler((request, _) =>
        {
            requests.Enqueue(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(requests.Count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Accepted());
        }));
        publisher.Start(); publisher.SetNowPlaying("", "old"); publisher.SetConnected(true);
        await Until(() => publisher.Snapshot().Failures > 0);
        publisher.SetNowPlaying("", "latest"); await Until(() => publisher.Snapshot().PublishedTitle == "latest");
        Assert.EndsWith("song=latest", requests.Last()); Assert.Null(publisher.Snapshot().LastError);
    }

    [Fact]
    public async Task ErrorAcknowledgementsNeverExposeTheBodyOrSourceCredential()
    {
        await using var publisher = new IcecastMetadataPublisher(Settings(), handler: new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not successful " + Secret) })));
        publisher.Start(); publisher.SetNowPlaying("", "current"); publisher.SetConnected(true);
        await Until(() => publisher.Snapshot().Failures > 0);
        Assert.Equal(0, publisher.Snapshot().Updates);
        Assert.DoesNotContain(Secret, publisher.Snapshot().LastError!);
        Assert.Equal("Metadata acknowledgement was not successful.", publisher.Snapshot().LastError);
    }

    [Fact]
    public async Task DisposalCancelsAnInFlightRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new IcecastMetadataPublisher(Settings(), handler: new Handler(async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Accepted(); }));
        publisher.Start(); publisher.SetNowPlaying("", "current"); publisher.SetConnected(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Stopped", publisher.Snapshot().State);
    }

    [Theory]
    [InlineData("/lofi")]
    [InlineData("/lofi-low")]
    public async Task CommissionedLoFiKeepsTheFormerSuppressionPolicy(string mount)
    {
        var calls = 0;
        var settings = Settings() with { Commissioned = true, Mount = mount,
            Information = new("RadioTEDU LoFi", "", "", "", true) };
        await using var publisher = new IcecastMetadataPublisher(settings, () => { }, handler: new Handler((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Accepted()); }));
        publisher.Start(); publisher.SetConnected(true); publisher.SetNowPlaying("", "private source title");
        await Task.Delay(60);
        Assert.Equal("Suppressed", publisher.Snapshot().State); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConfiguredSuppressionDoesNotOpenAMetadataRequest()
    {
        var calls = 0;
        await using var publisher = new IcecastMetadataPublisher(Settings() with { MetadataSuppressed = true }, handler: new Handler((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Accepted()); }));
        publisher.Start(); publisher.SetNowPlaying("", "private"); publisher.SetConnected(true);
        await Task.Delay(60); Assert.Equal("Suppressed", publisher.Snapshot().State); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OggDoesNotPollAnUnsupportedIcyMetadataEndpoint()
    {
        var calls = 0;
        await using var publisher = new IcecastMetadataPublisher(Settings() with { Profile = CodecProfile.OggFlac }, handler: new Handler((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Accepted()); }));
        publisher.Start(); publisher.SetNowPlaying("", "source title"); publisher.SetConnected(true);
        await Task.Delay(60); Assert.Equal("NotApplicable", publisher.Snapshot().State); Assert.Equal(0, calls);
    }
}
