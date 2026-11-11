using VideoGrabber.Core;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.Core.Tests;

public class UrlAndNamingTests
{
    [Fact]
    public void Normalize_AddsScheme_WhenMissing()
    {
        Assert.Equal("https://example.com/video", UrlTools.Normalize(" example.com/video "));
    }

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("http://example.com/a")]
    public void Normalize_KeepsHttpsAndHttp(string value)
    {
        Assert.Equal(value, UrlTools.Normalize(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/a")]
    [InlineData("not a link")]
    [InlineData("https://localhost/x")]
    public void Normalize_RejectsInvalid(string value)
    {
        Assert.Null(UrlTools.Normalize(value));
    }

    [Fact]
    public void SanitizedFileBase_ReplacesForbiddenCharacters()
    {
        Assert.Equal("a_b_c_d_e_f_g_h", FileNameRules.SanitizedFileBase("a/b:c\\d\"e<f>g|h").TrimEnd('_', ' '));
        Assert.Equal("1280x720", FileNameRules.SanitizedFileBase("1280×720"));
    }

    [Fact]
    public void SanitizedFileBase_TrimsTrailingDotsAndSpaces()
    {
        Assert.Equal("abc", FileNameRules.SanitizedFileBase("abc. .."));
    }

    [Theory]
    [InlineData("https://site.com/video/abc123", "abc123")]
    [InlineData("https://site.com/video/abc123/index.m3u8", "abc123")]
    [InlineData("https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
        "big_buck_bunny_720_10s_1mb")]
    public void Identifier_PicksStableSegment(string url, string expected)
    {
        Assert.Equal(expected, FileNameRules.Identifier(url));
    }

    [Theory]
    [InlineData("https://cdn.site.com/hls/index-f3-v1-a1.m3u8")]
    [InlineData("https://cdn.site.com/1700000000000/master.m3u8")]
    public void Identifier_ReturnsNull_WhenOnlyGenericSegments(string url)
    {
        Assert.Null(FileNameRules.Identifier(url));
    }

    [Fact]
    public void MakeOutputBase_AppendsStableIdentifier()
    {
        var job = TestJobs.Create("https://site.com/watch/nice-clip", "标题 / 带有非法字符");
        Assert.Equal("标题 _ 带有非法字符 [nice-clip]", FileNameRules.MakeOutputBase(job));
    }

    [Fact]
    public void UniqueOutputBase_AppendsCounter_WhenFileExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var job = TestJobs.Create("https://site.com/watch/abc123", "示例", directory);
            var first = FileNameRules.UniqueOutputBase(job);
            File.WriteAllText(Path.Combine(directory, first + ".mp4"), "x");
            var second = FileNameRules.UniqueOutputBase(job);
            Assert.Equal(first + " (2)", second);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class OutputParserTests
{
    [Fact]
    public void Progress_ParsesAllFields()
    {
        const string line = "PROGRESS| 42.5%|1.20MiB/s|00:12|1048576|2097152|NA";
        Assert.True(OutputParser.TryParseProgress(line, out var sample));
        Assert.Equal(0.425, sample.Progress, 3);
        Assert.Equal("1.20MiB/s", sample.Speed);
        Assert.Equal("00:12", sample.Eta);
        Assert.Equal(1048576, sample.DownloadedBytes);
        Assert.Equal(2097152, sample.TotalBytes);
    }

    [Fact]
    public void Progress_UsesEstimate_WhenTotalMissing()
    {
        const string line = "PROGRESS|NA|NA|NA|100|NA|4096";
        Assert.True(OutputParser.TryParseProgress(line, out var sample));
        Assert.Equal(4096, sample.TotalBytes);
        Assert.Equal("", sample.Speed);
        Assert.Equal("", sample.Eta);
    }

    [Fact]
    public void Destination_MergedOutput_ReplacesEarlierFiles()
    {
        Assert.True(OutputParser.TryParseDestination("Merging formats into \"/tmp/a.mp4\"", out var path, out var isMerge));
        Assert.Equal("/tmp/a.mp4", path);
        Assert.True(isMerge);
    }

    [Fact]
    public void FfmpegProgress_ParsesTotalSize()
    {
        Assert.True(OutputParser.TryParseFfmpegProgress("total_size=2048", out var size));
        Assert.Equal(2048, size);
    }
}

public class ErrorExplainerTests
{
    [Fact]
    public void Drm_WinsOverLogin()
    {
        var info = ErrorExplainer.Explain("ERROR: This video is DRM protected. Please sign in.", "https://x.com/a");
        Assert.Equal(FailureKind.Drm, info.Kind);
    }

    [Fact]
    public void Placeholder_WinsOverUnsupported()
    {
        var info = ErrorExplainer.Explain("Output file does not contain any stream", "https://x.com/a.m3u8");
        Assert.Equal(FailureKind.PlaceholderStream, info.Kind);
    }

    [Fact]
    public void Login_Detected()
    {
        var info = ErrorExplainer.Explain("ERROR: Sign in to confirm you're not a bot", "https://x.com/a");
        Assert.Equal(FailureKind.LoginRequired, info.Kind);
        Assert.True(info.NeedsVerification);
    }

    [Fact]
    public void Forbidden_SuggestsSniffing()
    {
        var info = ErrorExplainer.Explain("ERROR: unable to download video data: HTTP Error 403: Forbidden", "https://x.com/a");
        Assert.Equal(FailureKind.Forbidden, info.Kind);
        Assert.True(info.OffersSniffing);
    }

    [Fact]
    public void UnsupportedMedia_Detected()
    {
        var info = ErrorExplainer.Explain("Requested formats are incompatible for merge", "https://x.com/a");
        Assert.Equal(FailureKind.UnsupportedMedia, info.Kind);
    }

    [Fact]
    public void Fallback_UsesLastErrorLine()
    {
        var info = ErrorExplainer.Explain("line one\nERROR: something strange happened\n", "https://x.com/a");
        Assert.Equal("something strange happened", info.Message);
    }
}

public class QualityCatalogTests
{
    [Fact]
    public void Build_ListsBestFirst_ThenHeightsDescending()
    {
        var options = QualityCatalog.Build(new[] { 720, 1080 });
        Assert.Equal("best", options[0].Id);
        Assert.Equal("h1080", options[1].Id);
        Assert.Equal("h720", options[2].Id);
        Assert.Equal("audio", options[3].Id);
    }

    [Fact]
    public void Select_FallsBackToBest_WhenIdUnknown()
    {
        var options = QualityCatalog.Build(null);
        Assert.Equal("best", QualityCatalog.Select(options, "nope").Id);
        Assert.Equal("h480", QualityCatalog.Select(options, "h480").Id);
    }

    [Fact]
    public void FormatSelector_MatchesParityRules()
    {
        Assert.Equal("bv*+ba/b", QualityChoice.Best.FormatSelector);
        Assert.Equal("bv*[height<=1080]+ba/b[height<=1080]", QualityChoice.HeightChoice(1080).FormatSelector);
        Assert.Equal("bestaudio/best", QualityChoice.AudioOnly.FormatSelector);
    }
}

internal static class TestJobs
{
    public static DownloadJob Create(string url, string title, string? directory = null) => new(
        url,
        title,
        QualityChoice.Best,
        VideoContainer.Auto,
        directory ?? Path.GetTempPath(),
        null,
        "直连");
}
