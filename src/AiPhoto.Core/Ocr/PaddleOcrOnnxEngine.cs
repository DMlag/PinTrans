using System.Text;
using AiPhoto.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiPhoto.Core.Ocr;

/// <summary>
/// Manages discovery and on-demand downloading of the PP-OCRv4 ONNX detection and recognition models.
/// </summary>
public static class PaddleOcrModelManager
{
    public const string DetModelFileName = "ch_PP-OCRv4_det_infer.onnx";
    public const string RecModelFileName = "ch_PP-OCRv4_rec_infer.onnx";

    private static readonly string[] MirrorBaseUrls =
    [
        "https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv4",
        "https://hf-mirror.com/SWHL/RapidOCR/resolve/main/PP-OCRv4",
    ];

    /// <summary>
    /// Searches for valid PP-OCRv4 ONNX model files in the application directory (<c>models/ppocrv4</c> or <c>models</c>)
    /// or the user data directory (<c>%AppData%\PinTrans\models\ppocrv4</c>).
    /// </summary>
    public static bool TryResolveModelPaths(string? userModelsRootDir, out string detPath, out string recPath)
    {
        var candidateDirs = new List<string>(6);
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            candidateDirs.Add(Path.Combine(baseDir, "models", "ppocrv4"));
            candidateDirs.Add(Path.Combine(baseDir, "models"));
        }
        if (!string.IsNullOrWhiteSpace(userModelsRootDir))
        {
            candidateDirs.Add(Path.Combine(userModelsRootDir, "ppocrv4"));
            candidateDirs.Add(userModelsRootDir);
        }

        foreach (var dir in candidateDirs)
        {
            var d = Path.Combine(dir, DetModelFileName);
            var r = Path.Combine(dir, RecModelFileName);
            if (IsValidModelFile(d, minBytes: 1_000_000) && IsValidModelFile(r, minBytes: 2_000_000))
            {
                detPath = d;
                recPath = r;
                return true;
            }
        }

        detPath = "";
        recPath = "";
        return false;
    }

    public static bool IsValidModelFile(string path, long minBytes = 1_000_000)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length >= minBytes;
        }
        catch
        {
            return false;
        }
    }

    public static async Task DownloadModelsAsync(
        HttpClient http,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        Directory.CreateDirectory(targetDirectory);

        var detTarget = Path.Combine(targetDirectory, DetModelFileName);
        if (!IsValidModelFile(detTarget, 1_000_000))
        {
            progress?.Report("正在下载 PP-OCRv4 文本检测模型 (4.5 MB)…");
            await DownloadFileWithFallbackAsync(http, DetModelFileName, detTarget, cancellationToken).ConfigureAwait(false);
        }

        var recTarget = Path.Combine(targetDirectory, RecModelFileName);
        if (!IsValidModelFile(recTarget, 2_000_000))
        {
            progress?.Report("正在下载 PP-OCRv4 文本识别模型 (10.4 MB)…");
            await DownloadFileWithFallbackAsync(http, RecModelFileName, recTarget, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("PP-OCRv4 离线模型已就绪。");
    }

    private static async Task DownloadFileWithFallbackAsync(
        HttpClient http,
        string fileName,
        string destinationPath,
        CancellationToken ct)
    {
        Exception? lastError = null;
        var tmpPath = destinationPath + ".tmp";

        foreach (var baseUrl in MirrorBaseUrls)
        {
            var url = $"{baseUrl}/{fileName}";
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                File.Move(tmpPath, destinationPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        throw new InvalidOperationException($"下载离线 OCR 模型 {fileName} 失败：{lastError?.Message}", lastError);
    }
}

/// <summary>
/// High-accuracy local neural offline OCR engine powered by PaddleOCR PP-OCRv4 ONNX
/// (<c>DBNet</c> text detector + <c>SVTR_LCNet</c> text recognizer via <see cref="Microsoft.ML.OnnxRuntime"/>).
/// </summary>
public sealed class PaddleOcrOnnxEngine : IDisposable
{
    private const int DetMaxSideLen = 960;
    private const float DetBinThreshold = 0.30f;
    private const float DetBoxThreshold = 0.48f;
    private const float DetUnclipRatio = 1.65f;
    private const int RecImageHeight = 48;
    private const float RecMinScore = 0.45f;

    private readonly InferenceSession _detSession;
    private readonly InferenceSession _recSession;
    private readonly string _detInputName;
    private readonly string _recInputName;
    private readonly string[] _dictionary;
    private readonly object _gate = new();
    private bool _disposed;

    public PaddleOcrOnnxEngine(string detModelPath, string recModelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
        };

        _detSession = new InferenceSession(detModelPath, options);
        _recSession = new InferenceSession(recModelPath, options);
        _detInputName = _detSession.InputMetadata.Keys.First();
        _recInputName = _recSession.InputMetadata.Keys.First();
        _dictionary = LoadEmbeddedDictionary(_recSession);
    }

    private static string[] LoadEmbeddedDictionary(InferenceSession recSession)
    {
        if (recSession.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var raw) &&
            !string.IsNullOrEmpty(raw))
        {
            return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        }
        throw new InvalidOperationException("PP-OCRv4 识别模型缺少内置字符集元数据 (character)。");
    }

    /// <summary>
    /// Runs full PP-OCRv4 detection + recognition + XY-Cut reading-order layout analysis on <paramref name="image"/>.
    /// </summary>
    public IReadOnlyList<OcrLineResult> RecognizeLines(Bgra32Image image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 4 || image.Height < 4) return [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var boxes = DetectTextBoxes(image);
            if (boxes.Count == 0)
            {
                // Tight single-line crop fallback: if the user selected a small single line directly, recognize the whole crop.
                if (image.Height <= 80 && image.Width >= image.Height)
                {
                    boxes.Add(image.Bounds);
                }
                else
                {
                    return [];
                }
            }

            var results = new List<OcrLineResult>(boxes.Count);
            foreach (var box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (box.Width < 6 || box.Height < 6) continue;

                var lineResult = RecognizeSingleLine(image, box);
                if (lineResult is not null)
                {
                    results.Add(lineResult);
                }
            }

            return OcrLayoutAnalyzer.SortLinesInReadingOrder(results);
        }
    }

    // ------------------------------------------------------------------ Stage 1: DBNet Text Detection

    private List<PixelRect> DetectTextBoxes(Bgra32Image source)
    {
        // Pad perimeter so characters touching the crop boundary have room for DBNet unclipping.
        const int pad = 16;
        var borderColor = SampleBorderColor(source);
        var paddedW = source.Width + pad * 2;
        var paddedH = source.Height + pad * 2;
        var padded = new Bgra32Image(paddedW, paddedH);
        FillColor(padded, borderColor);
        padded.Blit(source, pad, pad);

        // Compute target dimensions (multiples of 32). Upscale small crops up to 2x so small 12px glyphs have strong DBNet activation.
        var maxSide = Math.Max(paddedW, paddedH);
        var minSide = Math.Min(paddedW, paddedH);
        var scale = 1.0;
        if (maxSide > DetMaxSideLen)
        {
            scale = (double)DetMaxSideLen / maxSide;
        }
        else if (minSide < 96 && maxSide * 2 <= DetMaxSideLen)
        {
            scale = 2.0;
        }

        var detW = Math.Max(32, (int)Math.Round(paddedW * scale / 32.0) * 32);
        var detH = Math.Max(32, (int)Math.Round(paddedH * scale / 32.0) * 32);

        var resized = ResizeBilinear(padded, detW, detH);
        var inputTensor = new DenseTensor<float>([1, 3, detH, detW]);

        // ImageNet mean/std normalization on BGR channels (RapidOCR / PaddleOCR standard).
        const float meanB = 0.485f, meanG = 0.456f, meanR = 0.406f;
        const float stdB = 0.229f, stdG = 0.224f, stdR = 0.225f;

        var planeSize = detH * detW;
        var span = inputTensor.Buffer.Span;
        for (var y = 0; y < detH; y++)
        {
            var rowOffset = y * resized.Stride;
            var tRow = y * detW;
            for (var x = 0; x < detW; x++)
            {
                var p = rowOffset + x * 4;
                var b = resized.Pixels[p] / 255f;
                var g = resized.Pixels[p + 1] / 255f;
                var r = resized.Pixels[p + 2] / 255f;

                var idx = tRow + x;
                span[idx] = (b - meanB) / stdB;
                span[planeSize + idx] = (g - meanG) / stdG;
                span[planeSize * 2 + idx] = (r - meanR) / stdR;
            }
        }

        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_detInputName, inputTensor),
        };

        using var outputs = _detSession.Run(inputs);
        var probTensor = outputs[0].AsTensor<float>();
        var probSpan = probTensor is DenseTensor<float> dt ? dt.Buffer.Span : probTensor.ToArray().AsSpan();

        var rawBoxes = ExtractBoxesFromProbabilityMap(probSpan, detW, detH, paddedW, paddedH, pad, source.Bounds);
        return MergeCollinearBoxes(rawBoxes, source.Bounds);
    }

    private static List<PixelRect> ExtractBoxesFromProbabilityMap(
        ReadOnlySpan<float> probMap,
        int mapW,
        int mapH,
        int paddedW,
        int paddedH,
        int pad,
        PixelRect sourceBounds)
    {
        var total = mapW * mapH;
        var visited = new bool[total];
        var queue = new int[total];
        var boxes = new List<PixelRect>();

        var scaleX = (double)paddedW / mapW;
        var scaleY = (double)paddedH / mapH;

        for (var idx = 0; idx < total; idx++)
        {
            if (visited[idx] || probMap[idx] < DetBinThreshold) continue;

            // BFS 8-connected component labeling
            var head = 0;
            var tail = 0;
            queue[tail++] = idx;
            visited[idx] = true;

            int minX = idx % mapW, maxX = minX;
            int minY = idx / mapW, maxY = minY;
            var scoreSum = 0.0;
            var count = 0;

            while (head < tail)
            {
                var cur = queue[head++];
                var cy = cur / mapW;
                var cx = cur - cy * mapW;

                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;

                scoreSum += probMap[cur];
                count++;

                var nyMin = Math.Max(0, cy - 1);
                var nyMax = Math.Min(mapH - 1, cy + 1);
                var nxMin = Math.Max(0, cx - 1);
                var nxMax = Math.Min(mapW - 1, cx + 1);

                for (var ny = nyMin; ny <= nyMax; ny++)
                {
                    var nRow = ny * mapW;
                    for (var nx = nxMin; nx <= nxMax; nx++)
                    {
                        var nIdx = nRow + nx;
                        if (!visited[nIdx] && probMap[nIdx] >= DetBinThreshold)
                        {
                            visited[nIdx] = true;
                            queue[tail++] = nIdx;
                        }
                    }
                }
            }

            if (count < 6) continue;
            var meanScore = scoreSum / count;
            if (meanScore < DetBoxThreshold) continue;

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (bw < 2 || bh < 2) continue;

            // DBNet polygon unclip distance: d = Area * unclip_ratio / Perimeter
            var area = (double)(bw * bh);
            var perimeter = 2.0 * (bw + bh);
            var unclipDist = area * DetUnclipRatio / perimeter;
            var expandX = Math.Max(2.0, unclipDist + bh * 0.18);
            var expandY = Math.Max(1.5, unclipDist);

            var l = (int)Math.Floor((minX - expandX) * scaleX) - pad;
            var t = (int)Math.Floor((minY - expandY) * scaleY) - pad;
            var r = (int)Math.Ceiling((maxX + 1 + expandX) * scaleX) - pad;
            var b = (int)Math.Ceiling((maxY + 1 + expandY) * scaleY) - pad;

            var rect = PixelRect.FromLTRB(l, t, r, b).Intersect(sourceBounds);
            if (rect.Width >= 6 && rect.Height >= 6)
            {
                boxes.Add(rect);
            }
        }

        return boxes;
    }

    private static List<PixelRect> MergeCollinearBoxes(List<PixelRect> boxes, PixelRect bounds)
    {
        if (boxes.Count <= 1) return boxes;
        boxes.Sort(static (a, b) =>
        {
            var cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.X.CompareTo(b.X);
        });

        var merged = new List<PixelRect>(boxes.Count);
        var used = new bool[boxes.Count];

        for (var i = 0; i < boxes.Count; i++)
        {
            if (used[i]) continue;
            var cur = boxes[i];
            used[i] = true;

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var j = 0; j < boxes.Count; j++)
                {
                    if (used[j]) continue;
                    var other = boxes[j];

                    // Merge if on the same visual line and horizontally overlapping or separated by less than 0.8 * lineHeight
                    var minH = Math.Min(cur.Height, other.Height);
                    var maxH = Math.Max(cur.Height, other.Height);
                    if (maxH > minH * 1.8) continue;

                    var vOverlap = Math.Min(cur.Bottom, other.Bottom) - Math.Max(cur.Y, other.Y);
                    if (vOverlap < minH * 0.55) continue;

                    var hGap = Math.Max(cur.X, other.X) - Math.Min(cur.Right, other.Right);
                    if (hGap <= minH * 0.85)
                    {
                        cur = cur.Union(other).Intersect(bounds);
                        used[j] = true;
                        changed = true;
                    }
                }
            }
            merged.Add(cur);
        }

        return merged;
    }

    // ------------------------------------------------------------------ Stage 2: SVTR_LCNet Text Recognition

    private OcrLineResult? RecognizeSingleLine(Bgra32Image fullImage, PixelRect box)
    {
        var crop = fullImage.Crop(box);
        if (crop.Width <= 0 || crop.Height <= 0) return null;

        var recW = Math.Clamp((int)Math.Round(crop.Width * (double)RecImageHeight / Math.Max(1, crop.Height)), 32, 3200);
        var paddedRecW = recW + 8; // small right margin for CTC trailing character stability
        var resized = ResizeBilinear(crop, recW, RecImageHeight);

        var borderColor = SampleBorderColor(crop);
        var inputTensor = new DenseTensor<float>([1, 3, RecImageHeight, paddedRecW]);
        var planeSize = RecImageHeight * paddedRecW;
        var span = inputTensor.Buffer.Span;

        var normBorderB = (borderColor.B / 255f - 0.5f) / 0.5f;
        var normBorderG = (borderColor.G / 255f - 0.5f) / 0.5f;
        var normBorderR = (borderColor.R / 255f - 0.5f) / 0.5f;

        for (var y = 0; y < RecImageHeight; y++)
        {
            var rowOffset = y * resized.Stride;
            var tRow = y * paddedRecW;
            for (var x = 0; x < paddedRecW; x++)
            {
                var idx = tRow + x;
                if (x < recW)
                {
                    var p = rowOffset + x * 4;
                    span[idx] = (resized.Pixels[p] / 255f - 0.5f) / 0.5f;
                    span[planeSize + idx] = (resized.Pixels[p + 1] / 255f - 0.5f) / 0.5f;
                    span[planeSize * 2 + idx] = (resized.Pixels[p + 2] / 255f - 0.5f) / 0.5f;
                }
                else
                {
                    span[idx] = normBorderB;
                    span[planeSize + idx] = normBorderG;
                    span[planeSize * 2 + idx] = normBorderR;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_recInputName, inputTensor),
        };

        using var outputs = _recSession.Run(inputs);
        var outTensor = outputs[0].AsTensor<float>();
        var dims = outTensor.Dimensions;
        if (dims.Length < 3) return null;

        var timeSteps = dims[1];
        var numClasses = dims[2];
        var logits = outTensor is DenseTensor<float> dt ? dt.Buffer.Span : outTensor.ToArray().AsSpan();

        var sb = new StringBuilder();
        var words = new List<OcrWordBox>();
        var scoreSum = 0.0;
        var charCount = 0;
        var prevClass = 0;

        var activeSteps = Math.Max(1, (int)Math.Round((double)timeSteps * recW / paddedRecW));

        for (var t = 0; t < timeSteps; t++)
        {
            var rowBase = t * numClasses;
            var bestClass = 0;
            var bestProb = logits[rowBase];
            for (var c = 1; c < numClasses; c++)
            {
                var v = logits[rowBase + c];
                if (v > bestProb)
                {
                    bestProb = v;
                    bestClass = c;
                }
            }

            if (bestClass > 0 && bestClass != prevClass)
            {
                var ch = DecodeClassIndex(bestClass);
                if (ch.Length > 0)
                {
                    sb.Append(ch);
                    scoreSum += bestProb;
                    charCount++;

                    var x0 = box.X + (int)Math.Floor((double)t / activeSteps * box.Width);
                    var x1 = box.X + (int)Math.Ceiling((double)(t + 1) / activeSteps * box.Width);
                    var charRect = PixelRect.FromLTRB(
                        Math.Clamp(x0, box.X, box.Right - 1),
                        box.Y,
                        Math.Clamp(Math.Max(x0 + 1, x1), box.X + 1, box.Right),
                        box.Bottom);
                    words.Add(new OcrWordBox(ch, charRect));
                }
            }
            prevClass = bestClass;
        }

        if (charCount == 0 || scoreSum / charCount < RecMinScore) return null;
        var text = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return new OcrLineResult(text, box, words);
    }

    private string DecodeClassIndex(int classIdx)
    {
        // Index 0 is CTC blank; indices 1 .. _dictionary.Length map to _dictionary[classIdx - 1];
        // index _dictionary.Length + 1 is space ' '.
        var dictIdx = classIdx - 1;
        if (dictIdx >= 0 && dictIdx < _dictionary.Length) return _dictionary[dictIdx];
        if (dictIdx == _dictionary.Length) return " ";
        return "";
    }

    // ------------------------------------------------------------------ image helpers

    private static Bgra32Image ResizeBilinear(Bgra32Image src, int dstW, int dstH)
    {
        if (src.Width == dstW && src.Height == dstH) return src;
        var dst = new Bgra32Image(dstW, dstH);
        var scaleX = (double)src.Width / dstW;
        var scaleY = (double)src.Height / dstH;

        for (var dy = 0; dy < dstH; dy++)
        {
            var fy = (dy + 0.5) * scaleY - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, src.Height - 1);
            var y1 = Math.Clamp(y0 + 1, 0, src.Height - 1);
            var wy = Math.Clamp(fy - y0, 0.0, 1.0);
            var dstRow = dy * dst.Stride;

            for (var dx = 0; dx < dstW; dx++)
            {
                var fx = (dx + 0.5) * scaleX - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, src.Width - 1);
                var x1 = Math.Clamp(x0 + 1, 0, src.Width - 1);
                var wx = Math.Clamp(fx - x0, 0.0, 1.0);

                var c00 = src.GetPixel(x0, y0);
                var c10 = src.GetPixel(x1, y0);
                var c01 = src.GetPixel(x0, y1);
                var c11 = src.GetPixel(x1, y1);

                var o = dstRow + dx * 4;
                dst.Pixels[o] = Lerp2D(c00.B, c10.B, c01.B, c11.B, wx, wy);
                dst.Pixels[o + 1] = Lerp2D(c00.G, c10.G, c01.G, c11.G, wx, wy);
                dst.Pixels[o + 2] = Lerp2D(c00.R, c10.R, c01.R, c11.R, wx, wy);
                dst.Pixels[o + 3] = 255;
            }
        }
        return dst;
    }

    private static byte Lerp2D(byte v00, byte v10, byte v01, byte v11, double wx, double wy)
    {
        var top = v00 * (1.0 - wx) + v10 * wx;
        var bot = v01 * (1.0 - wx) + v11 * wx;
        return (byte)Math.Clamp((int)Math.Round(top * (1.0 - wy) + bot * wy), 0, 255);
    }

    private static Bgra32 SampleBorderColor(Bgra32Image img)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var x = 0; x < img.Width; x++)
        {
            var cTop = img.GetPixel(x, 0);
            var cBot = img.GetPixel(x, img.Height - 1);
            b += cTop.B + cBot.B;
            g += cTop.G + cBot.G;
            r += cTop.R + cBot.R;
            n += 2;
        }
        return n == 0 ? new Bgra32(255, 255, 255) : new Bgra32((byte)(b / n), (byte)(g / n), (byte)(r / n));
    }

    private static void FillColor(Bgra32Image img, Bgra32 color)
    {
        for (var y = 0; y < img.Height; y++)
        {
            var row = y * img.Stride;
            for (var x = 0; x < img.Width; x++)
            {
                var o = row + x * 4;
                img.Pixels[o] = color.B;
                img.Pixels[o + 1] = color.G;
                img.Pixels[o + 2] = color.R;
                img.Pixels[o + 3] = color.A;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _detSession.Dispose();
            _recSession.Dispose();
        }
    }
}
