using System.Net;
using System.Text;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class TrailerPreviewTests
{
    [Theory]
    [InlineData("av01.0.09M.10.0.110.09.16.09.0", true)]
    [InlineData("av01.0.09M.10.0.110.09.18.09.0", true)]
    [InlineData("av01.0.09M.08.0.110.09.16.09.0", false)]
    [InlineData("av01.0.09M.10.0.110.01.01.01.0", false)]
    [InlineData("av01.0.09M.08.0.110.01.01.01.0", false)]
    [InlineData("avc1.640029", false)]
    public void HdrRequiresTenBitAv1AndPqOrHlgTransfer(string codec, bool hdr)
    {
        var manifest = Encoding.UTF8.GetString(Mpd()).Replace("avc1.640029", codec);
        Assert.Equal(hdr, TrailerPreviewService.GetHdrSegments(Encoding.UTF8.GetBytes(manifest), Manifest).Count > 0);
    }

    [Fact]
    public void HdrAcceptsExplicitCicpAndRejectsOffSiteSegments()
    {
        var descriptor = "<SupplementalProperty schemeIdUri=\"urn:mpeg:mpegB:cicp:TransferCharacteristics\" value=\"16\" />";
        var manifest = Encoding.UTF8.GetString(Mpd(extra: descriptor)).Replace("avc1.640029", "av01.0.09M.10");
        Assert.NotEmpty(TrailerPreviewService.GetHdrSegments(Encoding.UTF8.GetBytes(manifest), Manifest));
        Assert.Empty(TrailerPreviewService.GetHdrSegments(Encoding.UTF8.GetBytes(manifest.Replace("h264/chunk-", "https://example.org/chunk-")), Manifest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HdrCacheAndSdrFallbackKeepTheirSourceFormatsSeparate(bool available)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-hdr-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new FixtureHandler { ProbeHdr = true, HdrAvailable = available };
            using var http = new HttpClient(handler);
            using var service = new TrailerPreviewService(http, directory);
            var video = await service.GetVideoPreviewAsync(42);
            Assert.NotNull(video);
            Assert.Equal(available, video.IsHdr);
            Assert.Equal(available, video.Source.LocalPath.Contains("hdr-av1", StringComparison.Ordinal));
            var requests = handler.Requests;
            Assert.Equal(video, await service.GetVideoPreviewAsync(42));
            Assert.Equal(requests, handler.Requests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static readonly Uri Manifest = new("https://video.akamai.steamstatic.com/trailer/dash_h264.mpd");
    private static byte[] Mpd(string media = "h264/chunk-$RepresentationID$-$Number%05d$.m4s", string extra = "") => Encoding.UTF8.GetBytes($$"""
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT9S"><Period><AdaptationSet contentType="video">
          <Representation id="0" mimeType="video/mp4" codecs="avc1.640029" height="1080"><SegmentTemplate initialization="high-init.m4s" media="high-$Number$.m4s" duration="3" /></Representation>
          <Representation id="2" mimeType="video/mp4" codecs="avc1.640029" height="480"><SegmentTemplate initialization="h264/init-$RepresentationID$.m4s" media="{{media}}" duration="3000000" timescale="1000000" startNumber="1" />{{extra}}</Representation>
        </AdaptationSet></Period></MPD>
        """);

    [Fact]
    public void AudioPreviewUsesTheMatchingBoundedOfficialSegments()
    {
        var xml = Encoding.UTF8.GetString(Mpd()).Replace("</Period>", """
            <AdaptationSet mimeType="audio/mp4"><Representation id="audio" codecs="mp4a.40.2" bandwidth="128000">
            <SegmentTemplate initialization="audio/init-$RepresentationID$.m4s" media="audio/chunk-$Number$.m4s" duration="3" />
            </Representation></AdaptationSet></Period>
            """);
        var segments = TrailerPreviewService.GetAudioSegments(Encoding.UTF8.GetBytes(xml), Manifest);
        Assert.Equal(4, segments.Count);
        Assert.EndsWith("audio/init-audio.m4s", segments[0].AbsoluteUri);
        Assert.EndsWith("audio/chunk-3.m4s", segments[^1].AbsoluteUri);
        Assert.Empty(TrailerPreviewService.GetAudioSegments(Encoding.UTF8.GetBytes(xml.Replace("audio/chunk-", "https://example.org/chunk-")), Manifest));
    }

    [Fact]
    public void AdaptiveSteamTrailerUses480pAndOnlyThePreviewSegments()
    {
        var segments = TrailerPreviewService.GetSegments(Mpd(), Manifest);
        Assert.Equal(4, segments.Count);
        Assert.EndsWith("h264/init-2.m4s", segments[0].AbsoluteUri);
        Assert.EndsWith("h264/chunk-2-00001.m4s", segments[1].AbsoluteUri);
        Assert.EndsWith("h264/chunk-2-00003.m4s", segments[^1].AbsoluteUri);
    }

    [Theory]
    [InlineData(360, "3")]
    [InlineData(480, "2")]
    public void PreviewPrefersCardSizedVideoWithLargerFallback(int height, string expected)
    {
        var xml = Encoding.UTF8.GetString(Mpd());
        if (height == 360)
            xml = xml.Replace("</AdaptationSet>", "<Representation id=\"3\" mimeType=\"video/mp4\" codecs=\"avc1.640029\" height=\"360\"><SegmentTemplate initialization=\"init-$RepresentationID$.m4s\" media=\"chunk-$Number$.m4s\" duration=\"3\" /></Representation></AdaptationSet>");
        var segments = TrailerPreviewService.GetSegments(Encoding.UTF8.GetBytes(xml), Manifest);
        Assert.EndsWith($"init-{expected}.m4s", segments[0].AbsoluteUri);
    }

    [Theory]
    [InlineData("https://video.akamai.steamstatic.com/trailer.mp4", true)]
    [InlineData("https://steamstatic.com.example.org/trailer.mp4", false)]
    [InlineData("file:///C:/video.mp4", false)]
    [InlineData("https://user:password@video.akamai.steamstatic.com/trailer.mp4", false)]
    public void RemoteTrailersAreRestrictedToOfficialSteamHttps(string uri, bool accepted) => Assert.Equal(accepted, TrailerPreviewService.TrySteamUri(uri, out _));

    [Fact]
    public void OffSiteSegmentsAndExternalXmlEntitiesAreRejected()
    {
        Assert.Empty(TrailerPreviewService.GetSegments(Mpd("https://example.org/chunk-$Number$.m4s"), Manifest));
        Assert.Throws<System.Xml.XmlException>(() => TrailerPreviewService.GetSegments(Encoding.UTF8.GetBytes("<!DOCTYPE MPD [<!ENTITY x SYSTEM 'file:///secret'>]><MPD>&x;</MPD>"), Manifest));
    }

    [Fact]
    public async Task ConcurrentHoversShareTheDownloadAndCacheSurvivesARevisit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-trailer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new FixtureHandler();
            using var http = new HttpClient(handler);
            using var service = new TrailerPreviewService(http, directory);
            var previews = await Task.WhenAll(service.GetPreviewAsync(42), service.GetPreviewAsync(42));
            Assert.NotNull(previews[0]);
            Assert.Equal(previews[0], previews[1]);
            Assert.Equal(6, handler.Requests); // Metadata, manifest, initialization, three fragments.
            Assert.Equal(new byte[] { 1, 1, 1, 1 }, await File.ReadAllBytesAsync(previews[0]!.LocalPath));
            using var revisit = new TrailerPreviewService(http, directory);
            Assert.Equal(previews[0], await revisit.GetPreviewAsync(42));
            Assert.Equal(6, handler.Requests);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetPreviewAsync(42, cancellation.Token));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingTrailerKeepsThePosterAndDoesNotCreateAVideoFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-trailer-test-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new FixtureHandler { Missing = true });
        using var service = new TrailerPreviewService(http, directory);
        Assert.Null(await service.GetPreviewAsync(42));
        Assert.False(Directory.Exists(directory));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Missing;
        public bool ProbeHdr;
        public bool HdrAvailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var bytes = request.RequestUri!.Host == "store.steampowered.com"
                ? Encoding.UTF8.GetBytes(Missing ? "{\"42\":{\"data\":{}}}" : "{\"42\":{\"data\":{\"movies\":[{\"dash_h264\":\"https://video.akamai.steamstatic.com/trailer/dash_h264.mpd\",\"highlight\":true}]}}}")
                : request.RequestUri.AbsolutePath.EndsWith(".mpd", StringComparison.Ordinal) ? Mpd() : new byte[] { 1 };
            if (ProbeHdr && request.RequestUri.Host == "store.steampowered.com")
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"dash_h264\":", "\"dash_av1\":\"https://video.akamai.steamstatic.com/trailer/dash_av1.mpd\",\"dash_h264\":"));
            else if (ProbeHdr && request.RequestUri.AbsolutePath.EndsWith("dash_av1.mpd", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Mpd()).Replace("avc1.640029", HdrAvailable ? "av01.0.09M.10.0.110.09.16.09.0" : "av01.0.09M.08.0.110.01.01.01.0"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
