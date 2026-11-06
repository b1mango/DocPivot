using System.Buffers.Binary;
using System.IO.Compression;
using DocPivot.Core.Tables;

namespace DocPivot.PdfWorker;

/// <summary>
/// Finds table rules in a rendered 8-bit grayscale page so scanned grids keep their cell structure.
/// Output lines use top-left-origin PDF points, matching normalized OCR words.
/// </summary>
public static class RasterTableLineReader
{
    private const double DarkRatio = 0.74;
    private const int MaximumDarkThreshold = 185;
    private const double MinimumSegmentPoints = 6;
    private const double MinimumHorizontalPoints = 20;
    private const double MinimumVerticalPoints = 12;
    private const double MaximumThicknessPoints = 3;
    private const double MaximumSkewSlope = 0.05;
    private const double MinimumSkewSlope = 0.0005;
    private const double MinimumSkewRulePoints = 80;
    private const int MaximumGapPixels = 2;
    private const int MaximumLines = 4_000;
    private const long MaximumPixels = 40_000_000;

    public static RasterPageLines Read(string pngPath, double pageWidth, double pageHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        var image = TryDecodeGray8(File.ReadAllBytes(pngPath));
        return image is null ? RasterPageLines.Empty : Detect(image.Pixels, image.Width, image.Height, pageWidth, pageHeight);
    }

    public static RasterPageLines Detect(
        byte[] pixels,
        int width,
        int height,
        double pageWidth,
        double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length < (long)width * height ||
            !double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
        {
            return RasterPageLines.Empty;
        }

        var scaleX = pageWidth / width;
        var scaleY = pageHeight / height;
        var threshold = DarkThreshold(pixels, width * height);
        var horizontal = FindBands(pixels, width, height, threshold, horizontal: true,
            ToPixels(MinimumSegmentPoints, scaleX), ToPixels(MinimumHorizontalPoints, scaleX),
            MaximumThicknessPoints / scaleY);
        var vertical = FindBands(pixels, width, height, threshold, horizontal: false,
            ToPixels(MinimumSegmentPoints, scaleY), ToPixels(MinimumVerticalPoints, scaleY),
            MaximumThicknessPoints / scaleX);
        if (horizontal.Count + vertical.Count > MaximumLines)
        {
            // Halftones and dense line art are not table grids; borderless analysis is the safer fallback.
            return RasterPageLines.Empty;
        }

        var skew = EstimateSkew(horizontal, vertical, ToPixels(MinimumSkewRulePoints, scaleX));
        var (centerX, centerY) = (width / 2.0, height / 2.0);
        var lines = new List<DocumentLine>(horizontal.Count + vertical.Count);
        foreach (var band in horizontal)
        {
            // Rotate the rule about the page centre by the estimated skew so one rule has one y.
            var y = band.MeanLane - (skew * (band.MeanPosition - centerX));
            var shift = skew * (band.MeanLane - centerY);
            lines.Add(new DocumentLine((band.Start + shift) * scaleX, y * scaleY, (band.End + shift) * scaleX, y * scaleY));
        }

        foreach (var band in vertical)
        {
            var x = band.MeanLane + (skew * (band.MeanPosition - centerY));
            var shift = -skew * (band.MeanLane - centerX);
            lines.Add(new DocumentLine(x * scaleX, (band.Start + shift) * scaleY, x * scaleX, (band.End + shift) * scaleY));
        }

        return new RasterPageLines(lines, skew);
    }

    /// <summary>
    /// A tilted scan turns every rule into a slope: y = c + skew * x for horizontal rules and
    /// x = c - skew * y for vertical ones. The length-weighted median of long rules is robust to strokes.
    /// </summary>
    private static double EstimateSkew(List<Band> horizontal, List<Band> vertical, int minimumLength)
    {
        var samples = horizontal.Where(band => band.End - band.Start >= minimumLength)
            .Select(static band => (Slope: band.Slope, Weight: (double)(band.End - band.Start)))
            .Concat(vertical.Where(band => band.End - band.Start >= minimumLength)
                .Select(static band => (Slope: -band.Slope, Weight: (double)(band.End - band.Start))))
            .Where(static sample => double.IsFinite(sample.Slope) && Math.Abs(sample.Slope) <= MaximumSkewSlope)
            .OrderBy(static sample => sample.Slope)
            .ToArray();
        if (samples.Length < 2)
        {
            return 0;
        }

        var half = samples.Sum(static sample => sample.Weight) / 2;
        var accumulated = 0.0;
        foreach (var sample in samples)
        {
            accumulated += sample.Weight;
            if (accumulated >= half)
            {
                return Math.Abs(sample.Slope) < MinimumSkewSlope ? 0 : sample.Slope;
            }
        }

        return 0;
    }

    /// <summary>
    /// Ink is darker than paper by a ratio, not by a fixed level: a yellowed scan renders its paper
    /// near 200 grey, where a white-paper threshold would turn the background into ink.
    /// </summary>
    private static byte DarkThreshold(byte[] pixels, int count)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (var value in pixels.AsSpan(0, count))
        {
            histogram[value]++;
        }

        var accumulated = 0;
        var median = 255;
        for (var level = 0; level < histogram.Length; level++)
        {
            accumulated += histogram[level];
            if (accumulated * 2L >= count)
            {
                median = level;
                break;
            }
        }

        return (byte)Math.Min(MaximumDarkThreshold, Math.Round(median * DarkRatio));
    }

    private static int ToPixels(double points, double pointsPerPixel) =>
        Math.Max(2, (int)Math.Ceiling(points / pointsPerPixel));

    /// <summary>
    /// Joins dark runs of consecutive lanes (rows for horizontal rules, columns for vertical ones)
    /// into bands. Joining by overlap keeps slightly skewed scan lines in one band.
    /// </summary>
    private static List<Band> FindBands(
        byte[] pixels,
        int width,
        int height,
        byte threshold,
        bool horizontal,
        int minimumSegment,
        int minimumLength,
        double maximumThickness)
    {
        var laneCount = horizontal ? height : width;
        var length = horizontal ? width : height;
        var active = new List<Band>();
        var next = new List<Band>();
        var runs = new List<(int Start, int End)>();
        var accepted = new List<Band>();
        for (var lane = 0; lane < laneCount; lane++)
        {
            runs.Clear();
            FindRuns(pixels, width, threshold, horizontal, lane, length, minimumSegment, runs);
            next.Clear();
            foreach (var (start, end) in runs)
            {
                Band? match = null;
                foreach (var band in active)
                {
                    if (band.Merged || start > band.PreviousEnd + MaximumGapPixels ||
                        end < band.PreviousStart - MaximumGapPixels)
                    {
                        continue;
                    }

                    if (match is null)
                    {
                        match = band;
                    }
                    else
                    {
                        // Pieces of one rule that began apart (a faint spot, a skew step) join here.
                        match.Absorb(band);
                    }
                }

                if (match is null)
                {
                    next.Add(new Band(lane, start, end));
                }
                else
                {
                    match.Add(lane, start, end);
                }
            }

            foreach (var band in active)
            {
                if (band.Merged)
                {
                    continue;
                }

                if (band.LastLane == lane)
                {
                    next.Add(band);
                }
                else
                {
                    Accept(band, minimumLength, maximumThickness, accepted);
                }
            }

            foreach (var band in next)
            {
                band.EndLane();
            }

            (active, next) = (next, active);
        }

        foreach (var band in active.Where(static band => !band.Merged))
        {
            Accept(band, minimumLength, maximumThickness, accepted);
        }

        return accepted;
    }

    private static void Accept(Band band, int minimumLength, double maximumThickness, List<Band> accepted)
    {
        var length = band.End - band.Start;
        if (length < minimumLength)
        {
            return;
        }

        var thickness = (double)band.PixelCount / length;
        var laneSpan = band.LastLane - band.FirstLane + 1;
        if (thickness <= maximumThickness && laneSpan <= (maximumThickness * 2) + (length * MaximumSkewSlope))
        {
            accepted.Add(band);
        }
    }

    private static void FindRuns(
        byte[] pixels,
        int width,
        byte threshold,
        bool horizontal,
        int lane,
        int length,
        int minimumSegment,
        List<(int Start, int End)> runs)
    {
        var start = -1;
        var lastDark = -1;
        for (var index = 0; index < length; index++)
        {
            var value = horizontal ? pixels[(lane * width) + index] : pixels[(index * width) + lane];
            if (value >= threshold)
            {
                continue;
            }

            if (start >= 0 && index - lastDark > MaximumGapPixels + 1)
            {
                AddRun(start, lastDark + 1);
                start = -1;
            }

            if (start < 0)
            {
                start = index;
            }

            lastDark = index;
        }

        if (start >= 0)
        {
            AddRun(start, lastDark + 1);
        }

        void AddRun(int runStart, int runEnd)
        {
            if (runEnd - runStart >= minimumSegment)
            {
                runs.Add((runStart, runEnd));
            }
        }
    }

    private static GrayImage? TryDecodeGray8(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(signature))
        {
            return null;
        }

        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (chunkLength < 0 || offset + 12L + chunkLength > png.Length)
            {
                return null;
            }

            var type = png.AsSpan(offset + 4, 4);
            var data = png.AsSpan(offset + 8, chunkLength);
            if (type.SequenceEqual("IHDR"u8))
            {
                // Ghostscript pnggray output: 8-bit grayscale, no interlace. Anything else is skipped.
                if (chunkLength < 13 || data[8] != 8 || data[9] != 0 || data[12] != 0)
                {
                    return null;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
                {
                    return null;
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + chunkLength;
        }

        if (width == 0 || compressed.Length == 0)
        {
            return null;
        }

        var stride = width + 1;
        var raw = new byte[(long)stride * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var count = inflater.Read(raw, read, raw.Length - read);
                if (count == 0)
                {
                    return null;
                }

                read += count;
            }
        }

        var pixels = new byte[(long)width * height];
        for (var row = 0; row < height; row++)
        {
            var filter = raw[row * stride];
            var source = raw.AsSpan((row * stride) + 1, width);
            var target = pixels.AsSpan(row * width, width);
            ReadOnlySpan<byte> previous = row == 0 ? new byte[width] : pixels.AsSpan((row - 1) * width, width);
            if (!Unfilter(filter, source, previous, target))
            {
                return null;
            }
        }

        return new GrayImage(width, height, pixels);
    }

    private static bool Unfilter(byte filter, ReadOnlySpan<byte> source, ReadOnlySpan<byte> previous, Span<byte> target)
    {
        for (var index = 0; index < source.Length; index++)
        {
            var left = index > 0 ? target[index - 1] : 0;
            var up = previous[index];
            var upLeft = index > 0 ? previous[index - 1] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => -1,
            };
            if (predictor < 0)
            {
                return false;
            }

            target[index] = (byte)(source[index] + predictor);
        }

        return true;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    private sealed record GrayImage(int Width, int Height, byte[] Pixels);

    private sealed class Band
    {
        private int _currentStart = int.MaxValue;
        private int _currentEnd = int.MinValue;
        private double _sumPosition;
        private double _sumLane;
        private double _sumPositionSquared;
        private double _sumPositionLane;

        public Band(int lane, int start, int end)
        {
            FirstLane = lane;
            Start = start;
            End = end;
            Add(lane, start, end);
        }

        public int FirstLane { get; private set; }

        public int LastLane { get; private set; }

        public int Start { get; private set; }

        public int End { get; private set; }

        public long PixelCount { get; private set; }

        public int PreviousStart { get; private set; }

        public int PreviousEnd { get; private set; }

        public bool Merged { get; private set; }

        /// <summary>Centre of the dark pixels across lanes, in pixel coordinates.</summary>
        public double MeanLane => (_sumLane / PixelCount) + 0.5;

        public double MeanPosition => (_sumPosition / PixelCount) + 0.5;

        /// <summary>Least-squares lane change per pixel along the rule.</summary>
        public double Slope
        {
            get
            {
                var denominator = (PixelCount * _sumPositionSquared) - (_sumPosition * _sumPosition);
                return denominator <= 0
                    ? 0
                    : ((PixelCount * _sumPositionLane) - (_sumPosition * _sumLane)) / denominator;
            }
        }

        public void Add(int lane, int start, int end)
        {
            var count = end - start;
            var sumPosition = count * (start + end - 1) / 2.0;
            LastLane = Math.Max(LastLane, lane);
            Start = Math.Min(Start, start);
            End = Math.Max(End, end);
            PixelCount += count;
            _sumPosition += sumPosition;
            _sumLane += (double)lane * count;
            _sumPositionSquared += SumOfSquares(end - 1) - SumOfSquares(start - 1);
            _sumPositionLane += lane * sumPosition;
            _currentStart = Math.Min(_currentStart, start);
            _currentEnd = Math.Max(_currentEnd, end);
        }

        public void Absorb(Band other)
        {
            FirstLane = Math.Min(FirstLane, other.FirstLane);
            LastLane = Math.Max(LastLane, other.LastLane);
            Start = Math.Min(Start, other.Start);
            End = Math.Max(End, other.End);
            PixelCount += other.PixelCount;
            _sumPosition += other._sumPosition;
            _sumLane += other._sumLane;
            _sumPositionSquared += other._sumPositionSquared;
            _sumPositionLane += other._sumPositionLane;
            _currentStart = Math.Min(_currentStart, other._currentStart);
            _currentEnd = Math.Max(_currentEnd, other._currentEnd);
            PreviousStart = Math.Min(PreviousStart, other.PreviousStart);
            PreviousEnd = Math.Max(PreviousEnd, other.PreviousEnd);
            other.Merged = true;
        }

        /// <summary>Makes this lane's runs the overlap target for the next lane.</summary>
        public void EndLane()
        {
            if (_currentStart <= _currentEnd)
            {
                PreviousStart = _currentStart;
                PreviousEnd = _currentEnd;
            }

            _currentStart = int.MaxValue;
            _currentEnd = int.MinValue;
        }

        private static double SumOfSquares(double value) =>
            value < 0 ? 0 : value * (value + 1) * ((2 * value) + 1) / 6;
    }
}

/// <summary>Rules found in a rendered page, already rotated by the scan skew they revealed.</summary>
public sealed record RasterPageLines(IReadOnlyList<DocumentLine> Lines, double Skew)
{
    public static RasterPageLines Empty { get; } = new([], 0);

    /// <summary>Applies the same deskew to OCR words so text and rules share one frame.</summary>
    public IReadOnlyList<DocumentWord> Straighten(IReadOnlyList<DocumentWord> words, double pageWidth, double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (Skew == 0)
        {
            return words;
        }

        var (centerX, centerY) = (pageWidth / 2, pageHeight / 2);
        return words.Select(word =>
        {
            var deltaX = Skew * (word.Bounds.CenterY - centerY);
            var deltaY = -Skew * (word.Bounds.CenterX - centerX);
            return word with
            {
                Bounds = new DocumentBounds(
                    word.Bounds.Left + deltaX,
                    word.Bounds.Top + deltaY,
                    word.Bounds.Right + deltaX,
                    word.Bounds.Bottom + deltaY),
            };
        }).ToArray();
    }
}
