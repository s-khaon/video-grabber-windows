using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.Core.Tests;

public class HlsPlaylistTests
{
    private const string MediaPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-MAP:URI="init.mp4"
        #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x1234
        #EXTINF:6.0,
        seg0.m4s
        #EXTINF:5.5,
        seg1.m4s
        #EXT-X-DISCONTINUITY
        #EXTINF:6.0,
        https://cdn.example.com/other/seg2.m4s
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void ParsesSegmentsKeysInitAndDiscontinuity()
    {
        var spec = HlsPlaylistParser.ParseMedia(MediaPlaylist, new Uri("https://example.com/hls/master.m3u8"));

        Assert.Equal(3, spec.Segments.Count);
        Assert.Equal(6, spec.TargetDuration);
        Assert.Equal(17.5, spec.TotalDuration, precision: 3);
        Assert.Equal("https://example.com/hls/init.mp4", spec.InitSegment?.Url);
        Assert.Equal("https://example.com/hls/seg0.m4s", spec.Segments[0].Url);
        Assert.Contains("https://example.com/hls/key.bin", spec.Segments[0].KeyLine);
        Assert.Contains("https://example.com/hls/key.bin", spec.Segments[1].KeyLine);
        Assert.True(spec.Segments[2].Discontinuity);
        Assert.Equal("https://cdn.example.com/other/seg2.m4s", spec.Segments[2].Url);
    }

    [Fact]
    public void RewritesKeyUriToAbsolute()
    {
        var spec = HlsPlaylistParser.ParseMedia(MediaPlaylist, new Uri("https://example.com/hls/master.m3u8"));
        Assert.Contains("URI=\"https://example.com/hls/key.bin\"", spec.Segments[0].KeyLine);
    }

    [Fact]
    public void ParsesMasterVariantAttributes()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,AVERAGE-BANDWIDTH=600000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS="avc1.4d401f,mp4a.40.2"
            720/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="hvc1.1.6.L120.90,mp4a.40.2"
            https://cdn.example.com/1080/index.m3u8
            """;

        var variants = HlsPlaylistParser.ParseMaster(master, new Uri("https://example.com/v/master.m3u8"));

        Assert.NotNull(variants);
        Assert.Equal(2, variants!.Count);
        Assert.Equal(720, variants[0].Height);
        Assert.Equal(29.97, variants[0].FrameRate);
        Assert.Equal(600000, variants[0].EffectiveBandwidth);
        Assert.Equal("https://cdn.example.com/1080/index.m3u8", variants[1].UrlString);
        Assert.False(variants[1].IsPlaceholder);
    }

    [Fact]
    public void MarksImageCodecVariantsAsPlaceholder()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1x1,CODECS="png"
            ad.m3u8
            """;
        var variants = HlsPlaylistParser.ParseMaster(master, new Uri("https://example.com/v/master.m3u8"));
        Assert.True(variants![0].IsPlaceholder);
    }

    [Fact]
    public void ReturnsNullForMediaPlaylist()
    {
        Assert.Null(HlsPlaylistParser.ParseMaster(MediaPlaylist, new Uri("https://example.com/hls/media.m3u8")));
    }
}

public class AdStreamDetectorTests
{
    [Fact]
    public void DetectsKnownAdHostsOnly()
    {
        Assert.True(AdStreamDetector.IsAdUrl("https://ads.doubleclick.net/a/b.m3u8"));
        Assert.False(AdStreamDetector.IsAdUrl("https://cdn.example.com/ads/advertisement-video.m3u8"));
        Assert.False(AdStreamDetector.IsAdUrl("not a url"));
    }

    [Fact]
    public void StripsFakeImageHeaderBeforeTransportStream()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var ts = new byte[188 * 6];
        for (var index = 0; index < 6; index++) ts[index * 188] = 0x47;
        var data = png.Concat(ts).ToArray();

        var cleaned = AdStreamDetector.StrippingFakeImageHeader(data);

        Assert.Equal(ts.Length, cleaned.Length);
        Assert.Equal(0x47, cleaned[0]);
    }

    [Fact]
    public void LeavesRegularTransportStreamUntouched()
    {
        var ts = new byte[188 * 6];
        for (var index = 0; index < 6; index++) ts[index * 188] = 0x47;
        var cleaned = AdStreamDetector.StrippingFakeImageHeader(ts);
        Assert.Equal(ts.Length, cleaned.Length);
    }
}

public class MediaVerifierSniffTests
{
    [Fact]
    public void DetectsTransportStreamInsideMp4Extension()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vg-sniff-{Guid.NewGuid():N}.mp4");
        var data = new byte[188 * 4];
        for (var index = 0; index < 4; index++) data[index * 188] = 0x47;
        File.WriteAllBytes(path, data);
        try
        {
            Assert.Equal(MediaContainerKind.MpegTs, MediaVerifier.Sniff(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectsMp4AndMatroskaHeaders()
    {
        var mp4 = Path.Combine(Path.GetTempPath(), $"vg-sniff-{Guid.NewGuid():N}.mp4");
        var mp4Bytes = new byte[16];
        mp4Bytes[4] = (byte)'f';
        mp4Bytes[5] = (byte)'t';
        mp4Bytes[6] = (byte)'y';
        mp4Bytes[7] = (byte)'p';
        File.WriteAllBytes(mp4, mp4Bytes);

        var mkv = Path.Combine(Path.GetTempPath(), $"vg-sniff-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(mkv, new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 });
        try
        {
            Assert.Equal(MediaContainerKind.Mp4, MediaVerifier.Sniff(mp4));
            Assert.Equal(MediaContainerKind.Matroska, MediaVerifier.Sniff(mkv));
        }
        finally
        {
            File.Delete(mp4);
            File.Delete(mkv);
        }
    }
}

public class HlsResumeStoreTests
{
    private sealed class TempPaths : IPathProvider
    {
        public TempPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "vg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
            SettingsDirectory = Path.Combine(DataDirectory, "Settings");
            DownloadsDirectory = Path.Combine(DataDirectory, "Downloads");
        }

        public string SettingsDirectory { get; }
        public string DataDirectory { get; }
        public string DownloadsDirectory { get; }
        public string ToolsDirectory => Path.Combine(DataDirectory, "Tools");
        public IReadOnlyList<string> ToolSearchDirectories => new[] { ToolsDirectory };
        public string ExecutableSuffix => ".exe";
    }

    [Fact]
    public void CompletedJsonRoundTripsAndIgnoresGarbage()
    {
        var paths = new TempPaths();
        var store = new TaskMetadataStore(paths);
        var id = Guid.NewGuid();

        store.SaveCompleted(id, new[] { 5, 1, 3 });

        var loaded = store.LoadCompleted(id);
        Assert.Equal(new[] { 1, 3, 5 }, loaded.OrderBy(value => value));

        File.WriteAllText(Path.Combine(store.TaskDirectory(id), "completed.json"), "{not json");
        Assert.Empty(store.LoadCompleted(id));
    }

    [Fact]
    public void PartialSourcePrefersPartFileAndSkipsTinyFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vg-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "video.f137.mp4.part"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(directory, "video.f140.m4a.part"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(directory, "merged.mp4"), new byte[8192]);

            var source = PartialMediaBuilder.FindPartialSource(directory);
            Assert.EndsWith("video.f137.mp4.part", source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UniquePathAppendsNumberWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vg-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "视频.mp4");
            File.WriteAllText(target, "a");

            var unique = FileNameRules.UniquePath(target);

            Assert.Equal(Path.Combine(directory, "视频 (2).mp4"), unique);
            Assert.NotEqual(target, unique);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
