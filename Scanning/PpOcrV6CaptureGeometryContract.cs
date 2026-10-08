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

    internal void EnsureCompatible(ScanProfile profile, Size clientSize)
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
        var clientSupported = clientSize.Width >= 1280 && clientSize.Width <= 3840
            && clientSize.Height >= 720 && clientSize.Height <= 2161
            && Math.Abs(clientSize.Height - clientSize.Width * 9.0 / 16) <= 1;
        if (profileMatches && clientSupported
            && panel.Left >= 0 && panel.Top >= 0
            && panel.Right <= clientSize.Width && panel.Bottom <= clientSize.Height
            && PpOcrV6DetailGeometry.ResolveProductionRois(panel.Size, _fields).Count == 7)
        {
            return;
        }

        throw new ScannerFailureException(
            "ppocrv6_detail_geometry_incompatible",
            "游戏窗口尺寸不兼容",
            $"当前游戏窗口为 {clientSize.Width}×{clientSize.Height}，暂不支持扫描。",
            $"请在游戏的全屏或窗口模式中选择 16:9 分辨率（1280×720 至 3840×2160）；推荐 {_referenceClient.Width}×{_referenceClient.Height}，再重新扫描。",
            new Dictionary<string, object?>
            {
                ["clientWidth"] = clientSize.Width,
                ["clientHeight"] = clientSize.Height,
                ["detailWidth"] = panel.Width,
                ["detailHeight"] = panel.Height,
                ["requiredDetailWidth"] = _detailSize.Width,
                ["requiredDetailHeight"] = _detailSize.Height,
                ["fixedFieldCount"] = _fields.Count
            });
    }
}
