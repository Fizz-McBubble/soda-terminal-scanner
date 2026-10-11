using System.Drawing;
using System.Text.Json;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning;

internal sealed class PpOcrV6CaptureGeometryContract
{
    private readonly Size _referenceClient;
    private readonly Size _detailSize;
    private readonly Rectangle _detailCrop;
    private readonly IReadOnlyList<Rectangle> _fields;

    private PpOcrV6CaptureGeometryContract(Size referenceClient, Rectangle detailCrop, IReadOnlyList<Rectangle> fields)
    {
        _referenceClient = referenceClient;
        _detailCrop = detailCrop;
        _detailSize = detailCrop.Size;
        _fields = fields;
    }

    internal static PpOcrV6CaptureGeometryContract Load()
    {
        // Reuse the recognizer's canonical size/order/bounds validation. Read
        // the expected crop and client dimensions from that same source.
        var fields = PpOcrV6DetailGeometry.LoadProductionRois();
        using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.DataFile("scanner-detail-geometry.v1.json")));
        var production = document.RootElement.GetProperty("production");
        var standard = production.GetProperty("standardScreen");
        var crop = production.GetProperty("detailCrop");
        return new PpOcrV6CaptureGeometryContract(
            new Size(standard.GetProperty("width").GetInt32(), standard.GetProperty("height").GetInt32()),
            Rectangle.FromLTRB(crop.GetProperty("left").GetInt32(), crop.GetProperty("top").GetInt32(),
                crop.GetProperty("right").GetInt32(), crop.GetProperty("bottom").GetInt32()),
            fields);
    }

    internal void EnsureCompatible(ScanProfile profile, Size clientSize, bool isCloud = false)
    {
        // Validate the source profile as well as client aspect: a large bitmap
        // or a similarly shaped but displaced crop is not a compatible panel.
        var profileMatches = profile.StandardScreen.SequenceEqual(new[] { _referenceClient.Width, _referenceClient.Height })
            && profile.Rectangles.TryGetValue("detailPanel", out var crop)
            && crop.SequenceEqual(new[] { _detailCrop.Left, _detailCrop.Top, _detailCrop.Right, _detailCrop.Bottom });
        var panel = Rectangle.Empty;
        if (profileMatches)
        {
            // Match GameWindow.ToScreenRectangle, including float arithmetic.
            var normalized = profile.Rectangle("detailPanel");
            panel = new Rectangle(
                (int)Math.Round(normalized.X * clientSize.Width),
                (int)Math.Round(normalized.Y * clientSize.Height),
                Math.Max(1, (int)Math.Round(normalized.Width * clientSize.Width)),
                Math.Max(1, (int)Math.Round(normalized.Height * clientSize.Height)));
        }
        // Cloud presets do not expose a resolution setting. Their render surface
        // can differ slightly from 16:9 (live large window: 2130x1190). Admit at
        // most 1% height deviation only for an identified cloud render window.
        // The local contract keeps its established 1px bound.
        var rounding = isCloud ? Math.Max(2, clientSize.Width * 9.0 / 16 * 0.01) : 1;
        var clientSupported = clientSize.Width >= 1280 && clientSize.Width <= 3840
            && clientSize.Height >= 720 - (isCloud ? 2 : 1) && clientSize.Height <= 2160 + rounding
            && Math.Abs(clientSize.Height - clientSize.Width * 9.0 / 16) <= rounding;
        if (profileMatches && clientSupported
            && panel.Left >= 0 && panel.Top >= 0
            && panel.Right <= clientSize.Width && panel.Bottom <= clientSize.Height
            && PpOcrV6DetailGeometry.ResolveProductionRois(panel.Size, _fields, isCloud).Count == 7)
        {
            return;
        }

        throw new ScannerFailureException(
            "ppocrv6_detail_geometry_incompatible",
            "游戏窗口尺寸不兼容",
            $"当前游戏窗口为 {clientSize.Width}×{clientSize.Height}，暂不支持扫描。",
            isCloud
                ? "云·绝区零只有全屏、大尺寸窗口、窗口三种显示模式。请完整显示驱动仓库并关闭云端面板后重试；若仍提示尺寸不兼容，请反馈此问题。"
                : $"请在本机绝区零的全屏或窗口模式中选择 16:9 分辨率（1280×720 至 3840×2160）；推荐 {_referenceClient.Width}×{_referenceClient.Height}，再重新扫描。",
            new Dictionary<string, object?>
            {
                ["clientWidth"] = clientSize.Width,
                ["clientHeight"] = clientSize.Height,
                ["clientKind"] = isCloud ? "cloud" : "local",
                ["detailWidth"] = panel.Width,
                ["detailHeight"] = panel.Height,
                ["requiredDetailWidth"] = _detailSize.Width,
                ["requiredDetailHeight"] = _detailSize.Height,
                ["fixedFieldCount"] = _fields.Count
            });
    }
}
