using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocPivot.Infrastructure.Office;

namespace DocPivot.Infrastructure.Tests.Office;

public sealed class ExcelCompressionEstimatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocPivot-excel-estimate-{Guid.NewGuid():N}");

    public ExcelCompressionEstimatorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task EstimateAsync_LegacyWorkbookReturnsOriginalSizeWithoutWorker()
    {
        var input = Path.Combine(_directory, "legacy.xls");
        await File.WriteAllBytesAsync(input, new byte[128]);
        var estimator = new ExcelCompressionEstimator("worker-must-not-start");

        var estimate = await estimator.EstimateAsync(input, 85);

        Assert.Equal(new ExcelCompressionEstimate(128, 128), estimate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task EstimateAsync_RejectsInvalidStrengthBeforeWorker(int strength)
    {
        var estimator = new ExcelCompressionEstimator("worker-must-not-start");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => estimator.EstimateAsync("unused.xlsx", strength));
    }

    [Fact]
    public async Task EstimateAsync_PreCanceledRequestDoesNotReadOrStartWorker()
    {
        var estimator = new ExcelCompressionEstimator("worker-must-not-start");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            estimator.EstimateAsync("unused.xlsx", 55, cancellation.Token));
    }

    [Fact]
    public async Task EstimateAsync_WindowsWorkerMeasuresImagesAndReusesFileVersionCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = Path.Combine(_directory, "synthetic.xlsx");
        CreateWorkbook(input);
        var original = await File.ReadAllBytesAsync(input);
        var worker = FindWorker();
        var cache = Path.Combine(_directory, "cache");
        var estimator = new ExcelCompressionEstimator(worker, cacheDirectory: cache);

        var lossless = await estimator.EstimateAsync(input, 0);
        var compressed = await estimator.EstimateAsync(input, 100);
        var cached = await new ExcelCompressionEstimator(worker, cacheDirectory: cache).EstimateAsync(input, 100);

        Assert.Equal(original.Length, compressed.OriginalBytes);
        Assert.True(lossless.EstimatedBytes < lossless.OriginalBytes);
        Assert.True(compressed.EstimatedBytes < lossless.EstimatedBytes);
        Assert.Equal(compressed, cached);
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Single(Directory.GetDirectories(cache));
        Assert.NotEmpty(Directory.GetFiles(cache, "image-*-100.txt", SearchOption.AllDirectories));

        File.SetLastWriteTimeUtc(input, File.GetLastWriteTimeUtc(input).AddSeconds(2));
        Assert.Equal(compressed, await estimator.EstimateAsync(input, 100));
        Assert.Equal(2, Directory.GetDirectories(cache).Length);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static string FindWorker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocPivot.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(directory.FullName,
            "src", "DocPivot.OfficeWorker", "bin", configuration, "net10.0-windows", "DocPivot.OfficeWorker.exe");
        Assert.True(File.Exists(executable), $"Build the solution before running the Windows worker test: {executable}");
        return executable;
    }

    private static void CreateWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
        {
            writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("xl/workbook.xml", CompressionLevel.NoCompression).Open()))
        {
            writer.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            writer.Write(new string(' ', 20_000));
            writer.Write("</workbook>");
        }

        using var image = archive.CreateEntry("xl/media/image1.png", CompressionLevel.NoCompression).Open();
        image.Write(CreateNoisePng());
    }

    private static byte[] CreateNoisePng()
    {
        const int size = 256;
        using var image = new MemoryStream();
        image.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(image, "IHDR", header);
        using var pixels = new MemoryStream();
        using (var encoded = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            var random = new Random(12345);
            var row = new byte[size * 3];
            for (var y = 0; y < size; y++)
            {
                encoded.WriteByte(0);
                random.NextBytes(row);
                encoded.Write(row);
            }
        }

        WritePngChunk(image, "IDAT", pixels.ToArray());
        WritePngChunk(image, "IEND", []);
        return image.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] bytes)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, bytes.Length);
        output.Write(value);
        var kind = Encoding.ASCII.GetBytes(type);
        output.Write(kind);
        output.Write(bytes);
        var crc = uint.MaxValue;
        foreach (var item in kind.Concat(bytes))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        output.Write(value);
    }
}
