using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VideoGrabber.App.Platform;

/// <summary>跟随系统深浅色，并把窗口标题栏接入 Windows 11 的 Mica 材质。</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    private static readonly Dictionary<string, string> Light = new()
    {
        ["AccentBrush"] = "#0F6CBD",
        ["AccentHoverBrush"] = "#115EA3",
        ["AccentSoftBrush"] = "#EBF3FC",
        ["AccentTextBrush"] = "#FFFFFF",
        ["CanvasBrush"] = "#F3F3F3",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceAltBrush"] = "#FAFAFA",
        ["ControlFillBrush"] = "#FBFBFB",
        ["SubtleHoverBrush"] = "#F0F0F0",
        ["SubtlePressedBrush"] = "#E6E6E6",
        ["LineBrush"] = "#E5E5E5",
        ["ControlBorderBrush"] = "#D2D2D2",
        ["PrimaryTextBrush"] = "#1B1B1B",
        ["SecondaryTextBrush"] = "#616161",
        ["DangerBrush"] = "#C42B1C",
        ["DangerSoftBrush"] = "#FDF3F2",
        ["WarningBrush"] = "#9D5D00",
        ["WarningSoftBrush"] = "#FFF8E8",
        ["SuccessBrush"] = "#0F7B0F",
        ["LogBackgroundBrush"] = "#F8F8F8",
        ["HoverOverlayBrush"] = "#10000000",
        ["PressedOverlayBrush"] = "#1C000000",
        ["ScrollThumbBrush"] = "#9E9E9E",
        ["ScrollThumbHoverBrush"] = "#757575",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["AccentBrush"] = "#60CDFF",
        ["AccentHoverBrush"] = "#7FD8FF",
        ["AccentSoftBrush"] = "#1E3A4D",
        ["AccentTextBrush"] = "#00243D",
        ["CanvasBrush"] = "#202020",
        ["SurfaceBrush"] = "#2B2B2B",
        ["SurfaceAltBrush"] = "#323232",
        ["ControlFillBrush"] = "#2D2D2D",
        ["SubtleHoverBrush"] = "#383838",
        ["SubtlePressedBrush"] = "#414141",
        ["LineBrush"] = "#3D3D3D",
        ["ControlBorderBrush"] = "#4D4D4D",
        ["PrimaryTextBrush"] = "#FFFFFF",
        ["SecondaryTextBrush"] = "#BDBDBD",
        ["DangerBrush"] = "#FF99A4",
        ["DangerSoftBrush"] = "#442726",
        ["WarningBrush"] = "#FCE100",
        ["WarningSoftBrush"] = "#433519",
        ["SuccessBrush"] = "#6CCB5F",
        ["LogBackgroundBrush"] = "#252525",
        ["HoverOverlayBrush"] = "#1AFFFFFF",
        ["PressedOverlayBrush"] = "#26FFFFFF",
        ["ScrollThumbBrush"] = "#7A7A7A",
        ["ScrollThumbHoverBrush"] = "#9A9A9A",
    };

    /// <summary>在创建窗口之前调用，把调色板写入应用资源。</summary>
    public static void Apply()
    {
        IsDark = DetectDarkMode();
        var palette = IsDark ? Dark : Light;
        foreach (var (key, hex) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);
    }

    private static bool DetectDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyWindowChrome(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }

        // DWMSBT_MAINWINDOW = 2（Mica）。Windows 10 上会失败，退回纯色标题栏。
        var backdrop = 2;
        var backdropApplied = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) == 0;
        if (!backdropApplied)
        {
            var caption = IsDark ? 0x00202020 : 0x00F3F3F3;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        }

        var text = IsDark ? 0x00FFFFFF : 0x001B1B1B;
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));

        var corner = 2;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
