using System.Drawing;

namespace ZZZScannerNext.Scanning;

internal readonly record struct WindowCaptureContext(Rectangle Client, int Dpi, bool Visible, bool Foreground);

internal static class WindowCaptureContextPolicy
{
    internal static string? Failure(Rectangle expectedClient, int expectedDpi, WindowCaptureContext current)
    {
        if (!current.Visible || current.Client.Width <= 0 || current.Client.Height <= 0)
            return "game_window_not_visible";
        // A new origin is unsafe even when the dimensions are unchanged: all
        // in-flight panel, row and cursor coordinates belong to the old client.
        if (current.Client != expectedClient || current.Dpi != expectedDpi)
            return "window_geometry_changed";
        return current.Foreground ? null : "game_window_not_foreground";
    }

    internal static void EnsureCurrent(Rectangle expectedClient, int expectedDpi, WindowCaptureContext current)
    {
        var failure = Failure(expectedClient, expectedDpi, current);
        if (failure is null) return;
        var (title, message, remedy) = failure switch
        {
            "window_geometry_changed" => ("游戏窗口发生变化", "窗口位置、大小或显示缩放发生变化，已停止扫描。",
                "将游戏窗口放好并保持大小不变，再重新扫描。"),
            "game_window_not_visible" => ("游戏窗口不可见", "游戏窗口已关闭或最小化，已停止扫描。",
                "恢复游戏窗口，完整显示驱动仓库后重新扫描。"),
            _ => ("游戏已离开前台", "其他窗口切到了前台，已停止扫描。",
                "回到驱动仓库后重新扫描；扫描期间保持游戏在前台。")
        };
        throw new ScannerFailureException(failure, title, message, remedy,
            new Dictionary<string, object?>
            {
                ["clientWidth"] = current.Client.Width,
                ["clientHeight"] = current.Client.Height,
                ["dpi"] = current.Dpi,
                ["windowVisible"] = current.Visible,
                ["windowForeground"] = current.Foreground
            });
    }
}
