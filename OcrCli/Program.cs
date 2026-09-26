// Minimal PP-OCRv6 recognition runtime for Soda Terminal.
// Preprocessing and bounded ROI batching are adapted from:
// ZztIsolation/ZZZ-Scanner.Next@b4f3b53f6e9acb1f42af1a68c63cfe326bf912d4
// Ocr/PaddleOcrRecognizer.cs (MIT). See THIRD_PARTY_NOTICES.md.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using YamlDotNet.Serialization;

if (args.Length == 3 && args[0] == "recognize-stream")
{
    try
    {
        using var recognizer = new PpOcrV6Recognizer(args[1], args[2]);
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            try
            {
                var request = JsonSerializer.Deserialize<OcrBatchRequest>(line, JsonOptions.Default)
                    ?? throw new InvalidDataException("ocr_request_invalid");
                Console.WriteLine(JsonSerializer.Serialize(recognizer.Recognize(request), JsonOptions.Default));
            }
            catch (Exception exception)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new { error = new { code = "ppocrv6_recognition_failed", message = exception.Message } },
                    JsonOptions.Default));
            }
        }
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(
            new { code = "ppocrv6_runtime_failed", message = exception.Message },
            JsonOptions.Default));
        return 1;
    }
}

if (args.Length == 4 && args[0] == "recognize-batch")
{
    try
    {
        var request = JsonSerializer.Deserialize<OcrBatchRequest>(
            await File.ReadAllTextAsync(args[3]),
            JsonOptions.Default) ?? throw new InvalidDataException("ocr_request_invalid");
        using var recognizer = new PpOcrV6Recognizer(args[1], args[2]);
        var response = recognizer.Recognize(request);
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions.Default));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(
            new { code = "ppocrv6_recognition_failed", message = exception.Message },
            JsonOptions.Default));
        return 1;
    }
}

Console.Error.WriteLine(
    "usage: recognize-batch <model.onnx> <inference.yml> <input.json> | "
    + "recognize-stream <model.onnx> <inference.yml>");
return 2;

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

internal sealed record OcrBatchRequest(IReadOnlyList<OcrImageRequest> Images);
internal sealed record OcrImageRequest(int Sequence, string ImagePath, IReadOnlyList<OcrRoi> Rois);
internal sealed record OcrRoi(string Key, int X, int Y, int Width, int Height);
internal sealed record OcrTextResult(string Key, string Text, float Confidence);
internal sealed record OcrImageResult(int Sequence, IReadOnlyList<OcrTextResult> Fields);
internal sealed record OcrBatchResponse(
    string SchemaVersion,
    string Engine,
    IReadOnlyList<OcrImageResult> Images,
    OcrTiming Timing);
internal sealed record OcrTiming(
    int ImageCount,
    int RoiCount,
    int InferenceRuns,
    double PreprocessMs,
    double InferenceMs,
    double DecodeMs,
    double TotalMs);

internal sealed class PpOcrV6Recognizer : IDisposable
{
    private const int Height = 48;
    private readonly string[] _characters;
    private readonly InferenceSession _session;

    internal PpOcrV6Recognizer(string modelPath, string modelConfigPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("ppocrv6_model_missing", modelPath);
        if (!File.Exists(modelConfigPath))
            throw new FileNotFoundException("ppocrv6_config_missing", modelConfigPath);

        var config = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<PpOcrV6ModelConfig>(File.ReadAllText(modelConfigPath));
        _characters = (config.PostProcess?.CharacterDictionary
                ?? throw new InvalidDataException("ppocrv6_dictionary_missing"))
            .Append(" ")
            .ToArray();
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        _session = new InferenceSession(modelPath, options);
    }

    internal OcrBatchResponse Recognize(OcrBatchRequest request)
    {
        if (request.Images.Count == 0) throw new InvalidDataException("ocr_request_empty");
        var total = Stopwatch.StartNew();
        var flat = request.Images
            .SelectMany(image => image.Rois.Select(roi => new FlatRoi(image, roi)))
            .ToArray();
        if (flat.Length == 0) throw new InvalidDataException("ocr_rois_empty");
        if (request.Images.Select(image => image.Sequence).Distinct().Count() != request.Images.Count)
            throw new InvalidDataException("ocr_sequence_duplicate");

        var decoded = new OcrTextResult[flat.Length];
        var preprocessMs = 0d;
        var inferenceMs = 0d;
        var decodeMs = 0d;
        var inferenceRuns = 0;

        foreach (var bucket in BuildBuckets(flat))
        {
            var prepare = Stopwatch.StartNew();
            using var images = new BitmapSet(bucket.Items.Select(item => item.Image.ImagePath));
            var input = CreateInput(bucket.Items, images);
            prepare.Stop();
            preprocessMs += prepare.Elapsed.TotalMilliseconds;

            var infer = Stopwatch.StartNew();
            using var outputs = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor("x", input) });
            infer.Stop();
            inferenceMs += infer.Elapsed.TotalMilliseconds;
            inferenceRuns++;

            var decode = Stopwatch.StartNew();
            var bucketResults = Decode(outputs);
            decode.Stop();
            decodeMs += decode.Elapsed.TotalMilliseconds;
            for (var index = 0; index < bucketResults.Count; index++)
            {
                decoded[bucket.OriginalIndices[index]] = new OcrTextResult(
                    bucket.Items[index].Roi.Key,
                    bucketResults[index].Text,
                    bucketResults[index].Confidence);
            }
        }

        var results = new List<OcrImageResult>(request.Images.Count);
        var offset = 0;
        foreach (var image in request.Images)
        {
            var fields = decoded.Skip(offset).Take(image.Rois.Count).ToArray();
            results.Add(new OcrImageResult(image.Sequence, fields));
            offset += image.Rois.Count;
        }

        total.Stop();
        return new OcrBatchResponse(
            "soda.ppocrv6-recognition.v1",
            "PP-OCRv6-small-rec-onnx",
            results,
            new OcrTiming(
                request.Images.Count,
                flat.Length,
                inferenceRuns,
                Math.Round(preprocessMs, 3),
                Math.Round(inferenceMs, 3),
                Math.Round(decodeMs, 3),
                Math.Round(total.Elapsed.TotalMilliseconds, 3)));
    }

    public void Dispose() => _session.Dispose();

    private static IReadOnlyList<Bucket> BuildBuckets(IReadOnlyList<FlatRoi> items)
    {
        var buckets = new[] { new List<IndexedRoi>(), new List<IndexedRoi>(), new List<IndexedRoi>() };
        for (var index = 0; index < items.Count; index++)
        {
            var width = ResizedWidth(items[index].Roi.Width, items[index].Roi.Height);
            var bucket = width <= 96 ? 0 : width <= 192 ? 1 : 2;
            buckets[bucket].Add(new IndexedRoi(index, items[index]));
        }

        return buckets
            .Where(bucket => bucket.Count > 0)
            .Select(bucket => new Bucket(
                bucket.Select(item => item.Item).ToArray(),
                bucket.Select(item => item.Index).ToArray()))
            .ToArray();
    }

    private static DenseTensor<float> CreateInput(IReadOnlyList<FlatRoi> items, BitmapSet images)
    {
        var width = items.Max(item => ResizedWidth(item.Roi.Width, item.Roi.Height));
        if (width > 3200) throw new InvalidDataException("ocr_roi_too_wide");
        var tensor = new DenseTensor<float>(new[] { items.Count, 3, Height, width });
        var batchStride = 3 * Height * width;
        var channelStride = Height * width;
        var span = tensor.Buffer.Span;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var pixels = images.Get(item.Image.ImagePath);
            var resizedWidth = ResizedWidth(item.Roi.Width, item.Roi.Height);
            CopyResizedBgr(
                pixels,
                new Rectangle(item.Roi.X, item.Roi.Y, item.Roi.Width, item.Roi.Height),
                span,
                index * batchStride,
                channelStride,
                width,
                resizedWidth);
        }
        return tensor;
    }

    private IReadOnlyList<(string Text, float Confidence)> Decode(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensor = outputs.First().AsTensor<float>();
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length != 3) throw new InvalidDataException("ocr_output_shape_invalid");
        var values = tensor.ToArray();
        var timeStride = dimensions[2];
        var batchStride = dimensions[1] * timeStride;
        var results = new List<(string Text, float Confidence)>(dimensions[0]);

        for (var batch = 0; batch < dimensions[0]; batch++)
        {
            var text = new List<string>();
            var confidence = 0f;
            var count = 0;
            var previous = -1;
            for (var time = 0; time < dimensions[1]; time++)
            {
                var offset = batch * batchStride + time * timeStride;
                var best = 0;
                var score = values[offset];
                for (var type = 1; type < timeStride; type++)
                {
                    var candidate = values[offset + type];
                    if (!float.IsNaN(candidate) && candidate > score)
                    {
                        best = type;
                        score = candidate;
                    }
                }

                if (best != 0 && best != previous && best - 1 < _characters.Length)
                {
                    text.Add(_characters[best - 1]);
                    confidence += score;
                    count++;
                }
                previous = best;
            }
            results.Add((string.Concat(text), count == 0 ? 0 : confidence / count));
        }
        return results;
    }

    private static int ResizedWidth(int width, int height) =>
        Math.Max(1, (int)Math.Ceiling(width * (double)Height / height));

    private static void CopyResizedBgr(
        BitmapPixels source,
        Rectangle roi,
        Span<float> target,
        int batchOffset,
        int channelStride,
        int rowStride,
        int destinationWidth)
    {
        if (roi.Width <= 0 || roi.Height <= 0 || roi.Left < 0 || roi.Top < 0
            || roi.Right > source.Width || roi.Bottom > source.Height)
            throw new InvalidDataException("ocr_roi_out_of_bounds");

        var xSamples = BuildSamples(roi.X, roi.Width, destinationWidth);
        var ySamples = BuildSamples(roi.Y, roi.Height, Height);
        for (var y = 0; y < Height; y++)
        {
            var sy = ySamples[y];
            for (var x = 0; x < destinationWidth; x++)
            {
                var sx = xSamples[x];
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source.Data[source.Offset(sx.Low, sy.Low) + channel] * sx.LowWeight
                        + source.Data[source.Offset(sx.High, sy.Low) + channel] * sx.HighWeight;
                    var bottom = source.Data[source.Offset(sx.Low, sy.High) + channel] * sx.LowWeight
                        + source.Data[source.Offset(sx.High, sy.High) + channel] * sx.HighWeight;
                    var value = (((top >> 4) * sy.LowWeight) >> 16)
                        + (((bottom >> 4) * sy.HighWeight) >> 16);
                    var rounded = Math.Clamp((value + 2) >> 2, 0, 255);
                    target[batchOffset + channel * channelStride + y * rowStride + x] =
                        rounded / 127.5f - 1f;
                }
            }
        }
    }

    private static ResizeSample[] BuildSamples(int origin, int sourceLength, int destinationLength)
    {
        const int coefficientScale = 1 << 11;
        var scale = sourceLength / (double)destinationLength;
        var samples = new ResizeSample[destinationLength];
        var maximum = sourceLength - 1;
        for (var index = 0; index < destinationLength; index++)
        {
            var coordinate = ((index + 0.5) * scale) - 0.5;
            var low = (int)Math.Floor(coordinate);
            var highWeight = coordinate - low;
            if (low < 0)
            {
                low = 0;
                highWeight = 0;
            }
            else if (low >= maximum)
            {
                low = maximum;
                highWeight = 0;
            }

            var high = Math.Min(maximum, low + 1);
            var highCoefficient = Math.Clamp(
                (int)Math.Round(highWeight * coefficientScale, MidpointRounding.ToEven),
                0,
                coefficientScale);
            samples[index] = new ResizeSample(
                origin + low,
                origin + high,
                coefficientScale - highCoefficient,
                highCoefficient);
        }
        return samples;
    }

    private sealed class BitmapSet : IDisposable
    {
        private readonly Dictionary<string, BitmapPixels> _pixels = new(StringComparer.OrdinalIgnoreCase);

        internal BitmapSet(IEnumerable<string> paths)
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) throw new FileNotFoundException("ocr_image_missing", path);
                using var bitmap = new Bitmap(path);
                _pixels[path] = BitmapPixels.Read(bitmap);
            }
        }

        internal BitmapPixels Get(string path) => _pixels[path];
        public void Dispose() => _pixels.Clear();
    }

    private sealed record BitmapPixels(int Width, int Height, int BytesPerPixel, byte[] Data)
    {
        internal int Offset(int x, int y) => (y * Width + x) * BytesPerPixel;

        internal static BitmapPixels Read(Bitmap source)
        {
            using var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(converted))
                graphics.DrawImageUnscaled(source, 0, 0);
            var bounds = new Rectangle(0, 0, converted.Width, converted.Height);
            var data = converted.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                const int bytesPerPixel = 3;
                var rowBytes = converted.Width * bytesPerPixel;
                var pixels = new byte[rowBytes * converted.Height];
                for (var y = 0; y < converted.Height; y++)
                {
                    var row = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (converted.Height - 1 - y) * -data.Stride);
                    Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
                }
                return new BitmapPixels(converted.Width, converted.Height, bytesPerPixel, pixels);
            }
            finally
            {
                converted.UnlockBits(data);
            }
        }
    }

    private sealed record FlatRoi(OcrImageRequest Image, OcrRoi Roi);
    private sealed record IndexedRoi(int Index, FlatRoi Item);
    private sealed record Bucket(IReadOnlyList<FlatRoi> Items, IReadOnlyList<int> OriginalIndices);
    private readonly record struct ResizeSample(int Low, int High, int LowWeight, int HighWeight);
}

internal sealed class PpOcrV6ModelConfig
{
    public PpOcrV6PostProcess? PostProcess { get; set; }
}

internal sealed class PpOcrV6PostProcess
{
    [YamlMember(Alias = "character_dict")]
    public List<string>? CharacterDictionary { get; set; }
}
