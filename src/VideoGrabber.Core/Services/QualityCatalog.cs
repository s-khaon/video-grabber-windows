using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>清晰度选项：最佳画质 + 实际可用高度 + 仅音频。</summary>
public static class QualityCatalog
{
    public static readonly int[] FallbackHeights = { 2160, 1440, 1080, 720, 480 };

    public static List<QualityChoice> Build(IReadOnlyList<int>? heights)
    {
        var values = heights is { Count: > 0 } ? heights : FallbackHeights;
        var options = new List<QualityChoice> { QualityChoice.Best };
        options.AddRange(values.Distinct().OrderByDescending(value => value).Select(QualityChoice.HeightChoice));
        options.Add(QualityChoice.AudioOnly);
        return options;
    }

    public static QualityChoice Select(IReadOnlyList<QualityChoice> options, string? id) =>
        options.FirstOrDefault(option => option.Id == id) ?? QualityChoice.Best;
}
