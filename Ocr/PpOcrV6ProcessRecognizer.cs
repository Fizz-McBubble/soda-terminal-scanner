using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ZZZScannerNext.Ocr;

/// <summary>
/// Persistent process seam for Soda Terminal's native PP-OCRv6 worker.
/// The worker is authoritative; this adapter never falls back to PP-OCRv5.
/// </summary>
public sealed class PpOcrV6ProcessRecognizer : IOcrRecognizer
{
    private readonly Process _process;
    private readonly string _scratchDirectory;
    private int _requestId;

    public PpOcrV6ProcessRecognizer(
        string workerPath,
        string modelPath,
        string configPath,
        string scratchDirectory)
    {
        foreach (var path in new[] { workerPath, modelPath, configPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("ppocrv6_runtime_file_missing", path);
            }
        }

        _scratchDirectory = scratchDirectory;
        Directory.CreateDirectory(_scratchDirectory);
        var start = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("recognize-stream");
        start.ArgumentList.Add(modelPath);
        start.ArgumentList.Add(configPath);
        _process = Process.Start(start) ?? throw new InvalidOperationException("ppocrv6_worker_start_failed");
    }

    public IReadOnlyList<OcrResult> Recognize(Bitmap source, IReadOnlyList<Rectangle> rois) =>
        RecognizeBatchDetailed([new PpOcrV6BatchInput(source, rois)]).Results[0];

    public PpOcrV6BatchRecognition RecognizeBatchDetailed(IReadOnlyList<PpOcrV6BatchInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return new PpOcrV6BatchRecognition([], new PpOcrV6Diagnostics(0, 0, 0, 0, 0, 0, 0));
        }

        var request = Interlocked.Increment(ref _requestId);
        var paths = new List<string>();
        try
        {
            var images = new List<object>(inputs.Sum(input => input.Rois.Count));
            var flatIndex = 0;
            for (var imageIndex = 0; imageIndex < inputs.Count; imageIndex++)
            {
                var input = inputs[imageIndex];
                foreach (var roi in input.Rois)
                {
                    if (roi.Width <= 0 || roi.Height <= 0 || roi.Left < 0 || roi.Top < 0
                        || roi.Right > input.Source.Width || roi.Bottom > input.Source.Height)
                    {
                        throw new InvalidDataException("ppocrv6_detail_roi_out_of_bounds");
                    }

                    var path = Path.Combine(
                        _scratchDirectory,
                        $"request-{request:D6}-{imageIndex:D2}-{flatIndex:D2}.png");
                    using (var crop = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb))
                    using (var graphics = Graphics.FromImage(crop))
                    {
                        graphics.DrawImage(
                            input.Source,
                            new Rectangle(0, 0, roi.Width, roi.Height),
                            roi,
                            GraphicsUnit.Pixel);
                        crop.Save(path, ImageFormat.Png);
                    }
                    paths.Add(path);
                    images.Add(new
                    {
                        sequence = flatIndex + 1,
                        imagePath = path,
                        rois = new[]
                        {
                            new
                            {
                                key = "0",
                                x = 0,
                                y = 0,
                                width = roi.Width,
                                height = roi.Height
                            }
                        }
                    });
                    flatIndex++;
                }
            }

            _process.StandardInput.WriteLine(JsonSerializer.Serialize(new { images }));
            _process.StandardInput.Flush();
            var line = _process.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                var error = _process.HasExited ? _process.StandardError.ReadToEnd() : "empty_response";
                throw new InvalidDataException($"ppocrv6_worker_empty_response:{error}");
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                var code = errorElement.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString()
                    : "ppocrv6_recognition_failed";
                var message = errorElement.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "unknown";
                throw new InvalidDataException($"{code}:{message}");
            }

            var responseImages = document.RootElement.GetProperty("images");
            var flattened = new List<OcrResult>(responseImages.GetArrayLength());
            foreach (var image in responseImages.EnumerateArray())
            {
                var fields = image.GetProperty("fields").EnumerateArray().ToArray();
                if (fields.Length != 1)
                    throw new InvalidDataException("ppocrv6_cropped_field_count_invalid");
                flattened.Add(new OcrResult(
                    fields[0].GetProperty("confidence").GetSingle(),
                    fields[0].GetProperty("text").GetString() ?? string.Empty));
            }
            if (flattened.Count != inputs.Sum(input => input.Rois.Count))
                throw new InvalidDataException("ppocrv6_cropped_result_count_invalid");

            var results = new List<IReadOnlyList<OcrResult>>(inputs.Count);
            var resultOffset = 0;
            foreach (var input in inputs)
            {
                results.Add(flattened.Skip(resultOffset).Take(input.Rois.Count).ToArray());
                resultOffset += input.Rois.Count;
            }

            var timing = document.RootElement.GetProperty("timing");
            return new PpOcrV6BatchRecognition(
                results,
                new PpOcrV6Diagnostics(
                    timing.GetProperty("roiCount").GetInt32(),
                    inputs.SelectMany(input => input.Rois).Select(roi => roi.Width).DefaultIfEmpty().Max(),
                    timing.GetProperty("inferenceRuns").GetInt32(),
                    timing.GetProperty("preprocessMs").GetDouble(),
                    timing.GetProperty("inferenceMs").GetDouble(),
                    timing.GetProperty("decodeMs").GetDouble(),
                    timing.GetProperty("totalMs").GetDouble()));
        }
        finally
        {
            foreach (var path in paths)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { _process.StandardInput.Close(); } catch { }
        if (!_process.WaitForExit(3000))
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process.Dispose();
    }

    public readonly record struct PpOcrV6BatchInput(Bitmap Source, IReadOnlyList<Rectangle> Rois);
    public sealed record PpOcrV6BatchRecognition(
        IReadOnlyList<IReadOnlyList<OcrResult>> Results,
        PpOcrV6Diagnostics Diagnostics);
    public sealed record PpOcrV6Diagnostics(
        int RoiCount,
        int MaxWidth,
        int RunCount,
        double PreprocessMs,
        double InferenceMs,
        double DecodeMs,
        double TotalMs);
}
