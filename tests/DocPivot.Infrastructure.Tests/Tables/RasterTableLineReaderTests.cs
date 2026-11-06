using System.Buffers.Binary;
using System.IO.Compression;
using DocPivot.Core.Tables;
using DocPivot.PdfWorker;

namespace DocPivot.Infrastructure.Tests.Tables;

public sealed class RasterTableLineReaderTests : IDisposable
{
    // 1000 x 1000 pixels mapped onto a 500 x 500 point page: two pixels per point.
    private const int Size = 1000;
    private const double PageSize = 500;
    private static readonly double[] ExpectedRuleRows = [50.75, 150.75, 250.75];
    private static readonly double[] ExpectedRuleColumns = [50.75, 200.75, 350.75, 450.75];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", "RasterLines", Guid.NewGuid().ToString("N"));

    public RasterTableLineReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Detect_FindsGridRulesAsPagePointLines()
    {
        var pixels = Blank();
        DrawGrid(pixels, [100, 400, 700, 900], [100, 300, 500], thickness: 3);

        var lines = RasterTableLineReader.Detect(pixels, Size, Size, PageSize, PageSize).Lines;

        var horizontal = lines.Where(static line => line.StartY == line.EndY).ToArray();
        var vertical = lines.Where(static line => line.StartX == line.EndX).ToArray();
        Assert.Equal(3, horizontal.Length);
        Assert.Equal(4, vertical.Length);
        Assert.All(horizontal, line =>
        {
            Assert.InRange(line.StartX, 49, 51);
            Assert.InRange(line.EndX, 450, 452);
        });
        Assert.Equal(ExpectedRuleRows, horizontal.Select(static line => line.StartY).Order().ToArray());
        Assert.Equal(ExpectedRuleColumns, vertical.Select(static line => line.StartX).Order().ToArray());
    }

    [Fact]
    public void Detect_KeepsSlightlySkewedScanRuleAsOneLine()
    {
        var pixels = Blank();
        // A 600 pixel rule that drifts 6 pixels, as a scan tilted by about half a degree.
        for (var x = 200; x < 800; x++)
        {
            var y = 400 + ((x - 200) / 100);
            Fill(pixels, x, y, 1, 3);
        }

        var line = Assert.Single(RasterTableLineReader.Detect(pixels, Size, Size, PageSize, PageSize).Lines);
        Assert.Equal(line.StartY, line.EndY);
        Assert.InRange(line.StartY, 201, 204);
        Assert.InRange(line.EndX - line.StartX, 299, 301);
    }

    [Fact]
    public void Detect_StraightensTiltedGridAndWords()
    {
        var pixels = Blank();
        // A grid rotated by a slope of 0.01 (about 0.6 degrees) about the image centre.
        const double slope = 0.01;
        int[] rules = [200, 500, 800];
        foreach (var rule in rules)
        {
            for (var along = 200; along < 800; along++)
            {
                var y = (int)Math.Round(rule + (slope * (along - 500)));
                var x = (int)Math.Round(rule - (slope * (along - 500)));
                Fill(pixels, along, y, 1, 3);
                Fill(pixels, x, along, 3, 1);
            }
        }

        var result = RasterTableLineReader.Detect(pixels, Size, Size, PageSize, PageSize);

        Assert.InRange(result.Skew, 0.008, 0.012);
        var horizontal = result.Lines.Where(static line => line.StartY == line.EndY).ToArray();
        var vertical = result.Lines.Where(static line => line.StartX == line.EndX).ToArray();
        Assert.Equal(3, horizontal.Length);
        Assert.Equal(3, vertical.Length);
        Assert.All(horizontal.Zip(rules.Order()), pair => Assert.InRange(pair.First.StartY, (pair.Second / 2.0) - 0.5, (pair.Second / 2.0) + 1.5));
        var word = new DocumentWord("x", new DocumentBounds(390, 199, 400, 205), 1);
        var straightened = Assert.Single(result.Straighten([word], PageSize, PageSize));
        Assert.True(straightened.Bounds.Top < word.Bounds.Top);
    }

    [Theory]
    [InlineData(255, 165)]
    [InlineData(200, 120)]
    public void Detect_ScalesInkThresholdWithPaperBrightness(byte paper, byte ink)
    {
        // A faint copier rule on white paper, and a dark rule on yellowed paper that renders grey.
        var pixels = Blank(paper);
        DrawGrid(pixels, [100, 500, 900], [100, 400, 700], thickness: 3, ink);

        var lines = RasterTableLineReader.Detect(pixels, Size, Size, PageSize, PageSize).Lines;

        Assert.Equal(3, lines.Count(static line => line.StartY == line.EndY));
        Assert.Equal(3, lines.Count(static line => line.StartX == line.EndX));
    }

    [Fact]
    public void Detect_IgnoresSolidFillsAndShortStrokes()
    {
        var pixels = Blank();
        Fill(pixels, 100, 100, 600, 80);
        Fill(pixels, 100, 600, 20, 2);
        Fill(pixels, 300, 600, 2, 10);

        Assert.Empty(RasterTableLineReader.Detect(pixels, Size, Size, PageSize, PageSize).Lines);
    }

    [Fact]
    public void Read_DecodesFilteredGrayscalePng()
    {
        var pixels = Blank();
        DrawGrid(pixels, [100, 500, 900], [100, 400, 700], thickness: 2);
        var path = Path.Combine(_root, "page.png");
        File.WriteAllBytes(path, EncodeGray8(pixels, Size, Size));

        var lines = RasterTableLineReader.Read(path, PageSize, PageSize).Lines;

        Assert.Equal(3, lines.Count(static line => line.StartY == line.EndY));
        Assert.Equal(3, lines.Count(static line => line.StartX == line.EndX));
    }

    [Fact]
    public void Read_SkipsUnsupportedImageFormats()
    {
        var path = Path.Combine(_root, "not-a-png.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Empty(RasterTableLineReader.Read(path, PageSize, PageSize).Lines);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static byte[] Blank(byte paper = 255)
    {
        var pixels = new byte[Size * Size];
        Array.Fill(pixels, paper);
        return pixels;
    }

    private static void DrawGrid(byte[] pixels, int[] xs, int[] ys, int thickness, byte ink = 0)
    {
        foreach (var y in ys)
        {
            Fill(pixels, xs[0], y, xs[^1] - xs[0] + thickness, thickness, ink);
        }

        foreach (var x in xs)
        {
            Fill(pixels, x, ys[0], thickness, ys[^1] - ys[0] + thickness, ink);
        }
    }

    private static void Fill(byte[] pixels, int left, int top, int width, int height, byte ink = 0)
    {
        for (var y = top; y < top + height; y++)
        {
            pixels.AsSpan((y * Size) + left, width).Fill(ink);
        }
    }

    /// <summary>Writes rows with alternating Up and Paeth filters so decoding covers predictors.</summary>
    private static byte[] EncodeGray8(byte[] pixels, int width, int height)
    {
        var raw = new byte[(width + 1) * height];
        for (var row = 0; row < height; row++)
        {
            var filter = row == 0 ? (byte)0 : row % 2 == 0 ? (byte)2 : (byte)4;
            raw[row * (width + 1)] = filter;
            for (var x = 0; x < width; x++)
            {
                var value = pixels[(row * width) + x];
                var left = x > 0 ? pixels[(row * width) + x - 1] : 0;
                var up = row > 0 ? pixels[((row - 1) * width) + x] : 0;
                var upLeft = row > 0 && x > 0 ? pixels[((row - 1) * width) + x - 1] : 0;
                var predictor = filter switch
                {
                    2 => up,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw[(row * (width + 1)) + 1 + x] = (byte)(value - predictor);
            }
        }

        using var compressed = new MemoryStream();
        using (var deflater = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflater.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        // The decoder ignores CRCs, so a placeholder keeps the test encoder small.
        stream.Write([0, 0, 0, 0]);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }
}
