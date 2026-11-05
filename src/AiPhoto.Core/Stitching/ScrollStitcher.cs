using System.Runtime.InteropServices;
using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Stitching;

public enum StitchStatus
{
    /// <summary>The first frame was stored.</summary>
    First,
    /// <summary>The frame scrolled by <see cref="StitchFrameResult.Offset"/> pixels and its new rows were appended.</summary>
    Appended,
    /// <summary>Nothing moved between this frame and the previous one (end of page or scroll not settled).</summary>
    NoChange,
    /// <summary>No reliable overlap was found (scrolled too far, scrolled up, or content changed).</summary>
    NoOverlap,
    /// <summary>The output reached <see cref="StitchOptions.MaxHeight"/>.</summary>
    LimitReached,
}

public readonly record struct StitchFrameResult(StitchStatus Status, int Offset = 0, int TotalHeight = 0);

public sealed record StitchOptions
{
    /// <summary>Columns on the right ignored when comparing rows (scroll bar thumbs move between frames).</summary>
    public int IgnoreRightColumns { get; init; } = 24;

    /// <summary>Minimum fraction of informative overlapping rows that must match for an offset to be accepted.</summary>
    public double MinMatchRatio { get; init; } = 0.92;

    /// <summary>Minimum number of informative (non-uniform) rows that must overlap.</summary>
    public int MinInformativeRows { get; init; } = 6;

    /// <summary>Static header/footer detection is capped to this fraction of the frame height.</summary>
    public double MaxStaticFraction { get; init; } = 0.4;

    public int MaxHeight { get; init; } = 60000;
}

/// <summary>
/// Incrementally stitches frames of a vertically scrolling region into one long image.
/// <para>
/// For each new frame the stitcher detects rows that stayed in place at the top (sticky header) and
/// bottom (sticky footer), then searches the scroll offset <c>d</c> such that
/// <c>previous[y + d] == current[y]</c> for the moving middle part. Only the newly revealed rows are kept.
/// </para>
/// </summary>
public sealed class ScrollStitcher
{
    private readonly StitchOptions _options;
    private readonly List<Chunk> _chunks = [];
    private Bgra32Image? _first;
    private ulong[]? _prevHashes;
    private bool[]? _prevInformative;
    private int _width;
    private int _height;
    private int _globalFooter = int.MaxValue;
    private int _lastOffset;
    private int _totalHeight;

    public ScrollStitcher(StitchOptions? options = null) => _options = options ?? new StitchOptions();

    public int FrameCount => (_first is null ? 0 : 1) + _chunks.Count;

    /// <summary>Height the composed image will have (approximately, final footer may shift it slightly).</summary>
    public int TotalHeight => _totalHeight;

    public int Width => _width;

    public StitchFrameResult AddFrame(Bgra32Image frame)
    {
        if (_first is null)
        {
            _first = frame.Clone();
            _width = frame.Width;
            _height = frame.Height;
            (_prevHashes, _prevInformative) = HashRows(frame);
            _totalHeight = frame.Height;
            return new StitchFrameResult(StitchStatus.First, 0, _totalHeight);
        }

        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException("All frames must have the same size.", nameof(frame));

        var (hashes, informative) = HashRows(frame);
        var match = FindOffset(_prevHashes!, _prevInformative!, hashes, informative);

        if (match.Status != StitchStatus.Appended)
            return new StitchFrameResult(match.Status, 0, _totalHeight);

        if (_totalHeight + match.Offset > _options.MaxHeight)
            return new StitchFrameResult(StitchStatus.LimitReached, 0, _totalHeight);

        // Keep rows [H - footer - d, H): enough to compose with any final footer <= this pair's footer.
        var keepFrom = Math.Max(0, _height - match.Footer - match.Offset);
        var rows = frame.Crop(new PixelRect(0, keepFrom, _width, _height - keepFrom));
        _chunks.Add(new Chunk(rows, keepFrom, match.Offset, match.Footer));

        _globalFooter = Math.Min(_globalFooter, match.Footer);
        _lastOffset = match.Offset;
        _totalHeight += match.Offset;
        _prevHashes = hashes;
        _prevInformative = informative;
        return new StitchFrameResult(StitchStatus.Appended, match.Offset, _totalHeight);
    }

    /// <summary>Builds the final long image from everything added so far.</summary>
    public Bgra32Image Compose()
    {
        if (_first is null) throw new InvalidOperationException("No frames were added.");
        if (_chunks.Count == 0) return _first.Clone();

        var footer = _globalFooter == int.MaxValue ? 0 : _globalFooter;
        var total = _height + _chunks.Sum(c => c.Offset);
        var result = new Bgra32Image(_width, total);

        // First frame without its footer.
        result.Blit(_first.Crop(new PixelRect(0, 0, _width, _height - footer)), 0, 0);
        var y = _height - footer;

        foreach (var chunk in _chunks)
        {
            // Rows [H - footer - d, H - footer) of this frame are the newly revealed content.
            var from = _height - footer - chunk.Offset - chunk.KeepFrom;
            var part = chunk.Rows.Crop(new PixelRect(0, from, _width, chunk.Offset));
            result.Blit(part, 0, y);
            y += chunk.Offset;
        }

        // Footer from the last frame.
        if (footer > 0)
        {
            var last = _chunks[^1];
            var part = last.Rows.Crop(new PixelRect(0, _height - footer - last.KeepFrom, _width, footer));
            result.Blit(part, 0, y);
        }

        return result;
    }

    private (StitchStatus Status, int Offset, int Footer) FindOffset(ulong[] prev, bool[] prevInfo, ulong[] cur, bool[] curInfo)
    {
        var h = _height;
        var maxStatic = (int)(h * _options.MaxStaticFraction);

        var top = 0;
        while (top < h && prev[top] == cur[top]) top++;
        if (top == h) return (StitchStatus.NoChange, 0, 0);

        var bottom = 0;
        while (bottom < h - top && prev[h - 1 - bottom] == cur[h - 1 - bottom]) bottom++;

        top = Math.Min(top, maxStatic);
        bottom = Math.Min(bottom, maxStatic);

        var middle = h - top - bottom;
        var minOverlap = Math.Max(_options.MinInformativeRows, middle / 10);
        if (middle <= minOverlap) return (StitchStatus.NoOverlap, 0, 0);

        var bestOffset = -1;
        var bestRatio = 0.0;
        var bestMatched = 0;

        for (var d = 1; d <= middle - minOverlap; d++)
        {
            var overlap = middle - d;
            var informative = 0;
            var matched = 0;
            var mismatchBudget = (int)(overlap * (1 - _options.MinMatchRatio)) + 1;
            var mismatches = 0;

            for (var i = 0; i < overlap; i++)
            {
                var py = top + d + i;
                var cy = top + i;
                if (!curInfo[cy] && !prevInfo[py])
                {
                    // Uniform rows on both sides carry little information, but a uniform/non-uniform mismatch is still a mismatch.
                    if (prev[py] != cur[cy]) mismatches++;
                    continue;
                }
                informative++;
                if (prev[py] == cur[cy]) matched++;
                else if (++mismatches > mismatchBudget) break;
            }

            if (mismatches > mismatchBudget || informative < _options.MinInformativeRows) continue;
            var ratio = (double)matched / informative;
            if (ratio < _options.MinMatchRatio) continue;

            var better = ratio > bestRatio + 1e-9
                || (Math.Abs(ratio - bestRatio) <= 1e-9 && IsBetterTie(d, matched, bestOffset, bestMatched));
            if (better)
            {
                bestOffset = d;
                bestRatio = ratio;
                bestMatched = matched;
            }
        }

        return bestOffset < 0 ? (StitchStatus.NoOverlap, 0, 0) : (StitchStatus.Appended, bestOffset, bottom);
    }

    private bool IsBetterTie(int d, int matched, int bestD, int bestMatched)
    {
        if (bestD < 0) return true;
        // With repeating content several offsets match equally well: prefer the one closest to the previous scroll step.
        if (_lastOffset > 0) return Math.Abs(d - _lastOffset) < Math.Abs(bestD - _lastOffset);
        return matched > bestMatched;
    }

    private (ulong[] Hashes, bool[] Informative) HashRows(Bgra32Image image)
    {
        var hashes = new ulong[image.Height];
        var informative = new bool[image.Height];
        var usable = image.Width - (image.Width > _options.IgnoreRightColumns * 8 ? _options.IgnoreRightColumns : 0);
        var bytes = usable * Bgra32Image.BytesPerPixel;

        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(y * image.Stride, bytes);
            hashes[y] = HashRow(row, out informative[y]);
        }
        return (hashes, informative);
    }

    private static ulong HashRow(ReadOnlySpan<byte> row, out bool informative)
    {
        // FNV-1a style mixing over 8-byte words; alpha is ignored by masking.
        const ulong prime = 0x100000001B3UL;
        const ulong alphaMask = 0x00FFFFFF00FFFFFFUL;
        var hash = 0xCBF29CE484222325UL;
        var words = MemoryMarshal.Cast<byte, ulong>(row);
        var first = words.Length > 0 ? words[0] & alphaMask : 0;
        var uniform = true;
        foreach (var w in words)
        {
            var v = w & alphaMask;
            if (v != first) uniform = false;
            hash = (hash ^ v) * prime;
            hash ^= hash >> 29;
        }
        for (var i = words.Length * 8; i < row.Length; i++)
        {
            hash = (hash ^ row[i]) * prime;
        }
        informative = !uniform;
        return hash;
    }

    private sealed record Chunk(Bgra32Image Rows, int KeepFrom, int Offset, int Footer);
}
