using System.Drawing;

namespace ZZZScannerNext.Ocr;

public interface IOcrRecognizer : IDisposable
{
    IReadOnlyList<OcrResult> Recognize(Bitmap source, IReadOnlyList<Rectangle> rois);
}
