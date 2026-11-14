using System.Diagnostics;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixelForge.Domain;

namespace PixelForge.Engines.OCR;

internal sealed record OcrTextLine(string Text, float Confidence, double X, double Y, double Width, double Height,
    int Column = 0)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

internal sealed record OcrPageResult(IReadOnlyList<OcrTextLine> Lines, int Width, int Height);

internal sealed class PpOcrV6Runner : IDisposable
{
    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;
    private readonly string[] _dictionary;
    private readonly ExecutionProviderKind _detectorProvider;
    private readonly ExecutionProviderKind _recognizerProvider;
    private readonly ExecutionProviderKind _actualProvider;
    private readonly int _detectorWidth;
    private readonly int _detectorHeight;
    private readonly string _detectorInput;
    private readonly string _recognizerInput;
    private bool _disposed;

    public PpOcrV6Runner(string detectorPath, string recognizerPath, string dictionaryPath,
        ExecutionProviderKind requestedProvider)
    {
        var (detectorProvider, detector) = CreateSession(detectorPath, requestedProvider);
        var (recognizerProvider, recognizer) = CreateSession(recognizerPath, requestedProvider);
        _detector = detector;
        _recognizer = recognizer;
        _detectorProvider = detectorProvider;
        _recognizerProvider = recognizerProvider;
        _actualProvider = detectorProvider == ExecutionProviderKind.DirectML &&
                          recognizerProvider == ExecutionProviderKind.DirectML
            ? ExecutionProviderKind.DirectML
            : detectorProvider == ExecutionProviderKind.Cpu && recognizerProvider == ExecutionProviderKind.Cpu
                ? ExecutionProviderKind.Cpu
                : ExecutionProviderKind.Auto;
        _detectorInput = detector.InputMetadata.Keys.First();
        _recognizerInput = recognizer.InputMetadata.Keys.First();
        _dictionary = LoadDictionary(dictionaryPath);
    }

    public ExecutionProviderKind ActualProvider => _actualProvider;
    public ExecutionProviderKind DetectorProvider => _detectorProvider;
    public ExecutionProviderKind RecognizerProvider => _recognizerProvider;
    public string ProviderDescription => _detectorProvider == _recognizerProvider
        ? _detectorProvider.ToString()
        : $"mixed ({_detectorProvider} detector, {_recognizerProvider} recognizer)";
    public bool IsMixedProvider => _detectorProvider != _recognizerProvider;

    public OcrPageResult Recognize(string imagePath, bool preprocess, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var source = new MagickImage(imagePath);
        source.AutoOrient();
        source.ColorSpace = ColorSpace.sRGB;
        if (preprocess)
        {
            try { source.Deskew(new Percentage(80)); }
            catch (MagickException) { }
        }

        var originalWidth = (int)source.Width;
        var originalHeight = (int)source.Height;
        if (originalWidth < 8 || originalHeight < 8)
            return new OcrPageResult([], originalWidth, originalHeight);

        var (detectorImage, scaleX, scaleY) = PrepareDetectorImage(source);
        using (detectorImage)
        {
            var bgr = ToBgr(detectorImage);
            var tensor = BuildDetectorTensor(bgr, (int)detectorImage.Width, (int)detectorImage.Height);
            using var outputs = _detector.Run([NamedOnnxValue.CreateFromTensor(_detectorInput, tensor)], [_detector.OutputMetadata.Keys.First()]);
            var map = outputs.First().AsTensor<float>();
            var boxes = ExtractBoxes(map, (int)detectorImage.Width, (int)detectorImage.Height, cancellationToken);
            var sourceBgr = ToBgr(source);
            var lines = new List<OcrTextLine>();
            foreach (var box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x = (int)Math.Round(box.X / scaleX);
                var y = (int)Math.Round(box.Y / scaleY);
                var w = (int)Math.Round(box.Width / scaleX);
                var h = (int)Math.Round(box.Height / scaleY);
                if (w < 3 || h < 3) continue;
                var text = RecognizeBox(sourceBgr, originalWidth, originalHeight, x, y, w, h,
                    out var confidence, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new OcrTextLine(text.Trim(), confidence, x, y, w, h));
            }

            return new OcrPageResult(OrderLines(lines), originalWidth, originalHeight);
        }
    }

    private (MagickImage Image, double ScaleX, double ScaleY) PrepareDetectorImage(MagickImage source)
    {
        const int maxSide = 1280;
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(32, (int)Math.Round(source.Width * scale / 32.0) * 32);
        var height = Math.Max(32, (int)Math.Round(source.Height * scale / 32.0) * 32);
        var clone = (MagickImage)source.Clone();
        if (clone.Width != (uint)width || clone.Height != (uint)height)
            clone.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true },
                FilterType.Lanczos);
        var scaleX = width / (double)source.Width;
        var scaleY = height / (double)source.Height;
        return (clone, scaleX, scaleY);
    }

    private static DenseTensor<float> BuildDetectorTensor(byte[] bgr, int width, int height)
    {
        var tensor = new DenseTensor<float>([1, 3, height, width]);
        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var std = new[] { 0.229f, 0.224f, 0.225f };
        var plane = width * height;
        for (var i = 0; i < plane; i++)
        {
            var b = bgr[i * 3] / 255f;
            var g = bgr[i * 3 + 1] / 255f;
            var r = bgr[i * 3 + 2] / 255f;
            tensor[0, 0, i / width, i % width] = (b - mean[0]) / std[0];
            tensor[0, 1, i / width, i % width] = (g - mean[1]) / std[1];
            tensor[0, 2, i / width, i % width] = (r - mean[2]) / std[2];
        }

        return tensor;
    }

    private static List<(double X, double Y, double Width, double Height)> ExtractBoxes(Tensor<float> map,
        int width, int height, CancellationToken cancellationToken)
    {
        var dims = map.Dimensions.ToArray();
        var mapWidth = dims[^1];
        var mapHeight = dims[^2];
        var mapScaleX = width / (double)mapWidth;
        var mapScaleY = height / (double)mapHeight;
        var binary = new bool[mapWidth * mapHeight];
        for (var y = 0; y < mapHeight; y++)
        for (var x = 0; x < mapWidth; x++)
            binary[y * mapWidth + x] = map[0, 0, y, x] > 0.2f;

        var visited = new bool[binary.Length];
        var boxes = new List<(double X, double Y, double Width, double Height)>();
        var queue = new Queue<int>();
        for (var y = 0; y < mapHeight; y++)
        for (var x = 0; x < mapWidth; x++)
        {
            var index = y * mapWidth + x;
            if (!binary[index] || visited[index]) continue;
            visited[index] = true;
            queue.Enqueue(index);
            var minX = x;
            var maxX = x;
            var minY = y;
            var maxY = y;
            var count = 0;
            var score = 0f;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var cx = current % mapWidth;
                var cy = current / mapWidth;
                count++;
                score += map[0, 0, cy, cx];
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= mapWidth || ny >= mapHeight) continue;
                    var next = ny * mapWidth + nx;
                    if (!binary[next] || visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (count < 6) continue;
            var average = score / count;
            if (average < 0.4f) continue;
            var boxWidth = maxX - minX + 1;
            var boxHeight = maxY - minY + 1;
            if (boxWidth < 3 || boxHeight < 3) continue;
            var area = boxWidth * boxHeight;
            var perimeter = 2.0 * (boxWidth + boxHeight);
            var padding = area * 1.4 / Math.Max(1, perimeter);
            boxes.Add((
                Math.Max(0, (minX - padding) * mapScaleX),
                Math.Max(0, (minY - padding) * mapScaleY),
                Math.Min(width, (boxWidth + padding * 2) * mapScaleX),
                Math.Min(height, (boxHeight + padding * 2) * mapScaleY)));
        }

        return MergeLineBoxes(boxes);
    }

    private static List<(double X, double Y, double Width, double Height)> MergeLineBoxes(
        List<(double X, double Y, double Width, double Height)> boxes)
    {
        var result = new List<(double X, double Y, double Width, double Height)>();
        foreach (var box in boxes.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            var merged = false;
            for (var i = 0; i < result.Count; i++)
            {
                var current = result[i];
                var vertical = Overlap(current.Y, current.Y + current.Height, box.Y, box.Y + box.Height);
                var minHeight = Math.Min(current.Height, box.Height);
                var gap = Math.Max(box.X, current.X) - Math.Min(box.X + box.Width, current.X + current.Width);
                if (vertical < minHeight * 0.45 || gap > minHeight * 0.65) continue;
                var x = Math.Min(current.X, box.X);
                var y = Math.Min(current.Y, box.Y);
                var right = Math.Max(current.X + current.Width, box.X + box.Width);
                var bottom = Math.Max(current.Y + current.Height, box.Y + box.Height);
                result[i] = (x, y, right - x, bottom - y);
                merged = true;
                break;
            }

            if (!merged) result.Add(box);
        }

        return result;
    }

    private static double Overlap(double a1, double a2, double b1, double b2) =>
        Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));

    private string RecognizeBox(byte[] sourceBgr, int sourceWidth, int sourceHeight,
        int x, int y, int width, int height, out float confidence, CancellationToken cancellationToken)
    {
        confidence = 0;
        var left = Math.Clamp(x - 2, 0, Math.Max(0, sourceWidth - 1));
        var top = Math.Clamp(y - 2, 0, Math.Max(0, sourceHeight - 1));
        var right = Math.Clamp(x + width + 2, left + 1, sourceWidth);
        var bottom = Math.Clamp(y + height + 2, top + 1, sourceHeight);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        var targetHeight = 48;
        var targetWidth = Math.Clamp((int)Math.Ceiling(targetHeight * cropWidth / (double)Math.Max(1, cropHeight)), 16, 3200);
        var bgr = ResizeRegionBgr(sourceBgr, sourceWidth, sourceHeight, left, top, cropWidth, cropHeight,
            targetWidth, targetHeight);
        var tensor = new DenseTensor<float>([1, 3, targetHeight, targetWidth]);
        var plane = targetWidth * targetHeight;
        for (var i = 0; i < plane; i++)
        {
            tensor[0, 0, i / targetWidth, i % targetWidth] = (bgr[i * 3] / 255f - 0.5f) / 0.5f;
            tensor[0, 1, i / targetWidth, i % targetWidth] = (bgr[i * 3 + 1] / 255f - 0.5f) / 0.5f;
            tensor[0, 2, i / targetWidth, i % targetWidth] = (bgr[i * 3 + 2] / 255f - 0.5f) / 0.5f;
        }

        using var outputs = _recognizer.Run([NamedOnnxValue.CreateFromTensor(_recognizerInput, tensor)],
            [_recognizer.OutputMetadata.Keys.First()]);
        var logits = outputs.First().AsTensor<float>();
        var dims = logits.Dimensions.ToArray();
        var timesteps = dims[^2];
        var classes = dims[^1];
        var builder = new System.Text.StringBuilder();
        var score = 0f;
        var count = 0;
        var previous = -1;
        for (var t = 0; t < timesteps; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = 0;
            var bestScore = logits[0, t, 0];
            for (var c = 1; c < classes; c++)
            {
                var value = logits[0, t, c];
                if (value <= bestScore) continue;
                bestScore = value;
                best = c;
            }

            if (best == 0)
            {
                previous = 0;
                continue;
            }

            if (best == previous) continue;
            previous = best;
            if (best < _dictionary.Length) builder.Append(_dictionary[best]);
            score += bestScore;
            count++;
        }

        confidence = count == 0 ? 0 : score / count;
        return builder.ToString();
    }

    private static string[] LoadDictionary(string path)
    {
        var lines = File.ReadAllLines(path);
        var values = new string[lines.Length + 2];
        values[0] = "";
        for (var i = 0; i < lines.Length; i++) values[i + 1] = lines[i];
        values[^1] = " ";
        return values;
    }

    private static byte[] ToBgr(MagickImage image)
    {
        var bgra = image.ToByteArray(MagickFormat.Bgra);
        var width = (int)image.Width;
        var height = (int)image.Height;
        if (bgra.Length < width * height * 4) throw new InvalidDataException("The image could not be decoded.");
        var bgr = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            bgr[i * 3] = bgra[i * 4];
            bgr[i * 3 + 1] = bgra[i * 4 + 1];
            bgr[i * 3 + 2] = bgra[i * 4 + 2];
        }

        return bgr;
    }

    private static byte[] ResizeRegionBgr(byte[] source, int sourceWidth, int sourceHeight,
        int left, int top, int cropWidth, int cropHeight, int targetWidth, int targetHeight)
    {
        var result = new byte[targetWidth * targetHeight * 3];
        var scaleX = cropWidth / (double)targetWidth;
        var scaleY = cropHeight / (double)targetHeight;
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = top + (y + 0.5) * scaleY - 0.5;
            sourceY = Math.Clamp(sourceY, top, top + cropHeight - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(y0 + 1, top + cropHeight - 1);
            var fy = sourceY - y0;
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = left + (x + 0.5) * scaleX - 0.5;
                sourceX = Math.Clamp(sourceX, left, left + cropWidth - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(x0 + 1, left + cropWidth - 1);
                var fx = sourceX - x0;
                var i00 = (y0 * sourceWidth + x0) * 3;
                var i01 = (y0 * sourceWidth + x1) * 3;
                var i10 = (y1 * sourceWidth + x0) * 3;
                var i11 = (y1 * sourceWidth + x1) * 3;
                var target = (y * targetWidth + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var topValue = source[i00 + c] * (1 - fx) + source[i01 + c] * fx;
                    var bottomValue = source[i10 + c] * (1 - fx) + source[i11 + c] * fx;
                    result[target + c] = (byte)Math.Clamp((int)Math.Round(topValue * (1 - fy) + bottomValue * fy), 0, 255);
                }
            }
        }

        return result;
    }

    private static List<OcrTextLine> OrderLines(List<OcrTextLine> lines) =>
        lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();

    private static (ExecutionProviderKind Provider, InferenceSession Session) CreateSession(string path,
        ExecutionProviderKind requested)
    {
        var providers = requested switch
        {
            ExecutionProviderKind.Auto => new[] { ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu },
            ExecutionProviderKind.DirectML => new[] { ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu },
            _ => new[] { ExecutionProviderKind.Cpu }
        };
        Exception? last = null;
        foreach (var provider in providers)
        {
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    EnableMemoryPattern = false,
                    EnableCpuMemArena = false
                };
                if (provider == ExecutionProviderKind.DirectML)
                {
                    options.AppendExecutionProvider_DML(0);
                    options.EnableMemoryPattern = false;
                }
                else
                {
                    options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                }

                return (provider, new InferenceSession(path, options));
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("The OCR model could not be loaded.", last);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PpOcrV6Runner));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector.Dispose();
        _recognizer.Dispose();
    }
}
