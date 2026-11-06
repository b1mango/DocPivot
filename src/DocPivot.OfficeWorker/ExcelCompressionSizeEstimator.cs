using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocPivot.Core.Excel;
using DocPivot.Infrastructure.Office;

namespace DocPivot.OfficeWorker;

internal static class ExcelCompressionSizeEstimator
{
    private const int MaximumSampleImages = 12;
    private const long MaximumSampleBytes = 32L * 1024L * 1024L;
    private const long MaximumStructureEntryBytes = 4L * 1024L * 1024L;

    public static ExcelCompressionEstimate Estimate(string workbookPath, int strength, string cacheDirectory)
    {
        var profile = ExcelCompressionProfile.FromStrength(strength);
        var file = new FileInfo(workbookPath);
        var originalBytes = file.Length;
        if (!string.Equals(file.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return new ExcelCompressionEstimate(originalBytes, originalBytes);
        }

        var identity = string.Create(CultureInfo.InvariantCulture,
            $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{originalBytes}");
        var cache = Path.Combine(cacheDirectory, Hash(identity));
        Directory.CreateDirectory(cache);
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var structureSavings = GetOrMeasure(
            Path.Combine(cache, "structure.txt"),
            () => MeasureStructureSavings(archive));
        long imageSavings = 0;
        if (!profile.IsLossless)
        {
            var images = archive.Entries
                .Where(static entry =>
                    entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) &&
                    ExcelEmbeddedImageCompressor.IsSupportedImage(entry.FullName) &&
                    entry.Length > 0 && entry.Length <= ExcelEmbeddedImageCompressor.MaximumImageBytes)
                .OrderByDescending(static entry => entry.CompressedLength)
                .ToArray();
            long sampledBytes = 0;
            long sampledOriginalBytes = 0;
            var sampledCount = 0;
            foreach (var image in images)
            {
                if (sampledCount == MaximumSampleImages ||
                    (sampledCount > 0 && sampledBytes + image.Length > MaximumSampleBytes))
                {
                    break;
                }

                var imageCache = Path.Combine(cache,
                    $"image-{Hash(image.FullName)}-{strength.ToString(CultureInfo.InvariantCulture)}.txt");
                imageSavings += GetOrMeasure(imageCache, () => MeasureImageSavings(image, profile));
                sampledOriginalBytes += image.CompressedLength;
                sampledBytes += image.Length;
                sampledCount++;
            }

            if (sampledCount < images.Length && sampledOriginalBytes > 0)
            {
                // Extrapolate the measured ZIP-byte saving ratio, not the decoded bitmap size.
                var totalImageBytes = images.Sum(static image => image.CompressedLength);
                imageSavings = (long)Math.Round(imageSavings * (double)totalImageBytes / sampledOriginalBytes);
            }
        }

        return new ExcelCompressionEstimate(
            originalBytes,
            Math.Clamp(originalBytes - structureSavings - imageSavings, 0, originalBytes));
    }

    private static long MeasureStructureSavings(ZipArchive archive)
    {
        long measuredBytes = 0;
        long savings = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) ||
                entry.Length > MaximumStructureEntryBytes ||
                measuredBytes + entry.Length > MaximumSampleBytes)
            {
                continue;
            }

            using var input = entry.Open();
            savings += entry.CompressedLength - MeasureDeflatedLength(input);
            measuredBytes += entry.Length;
        }

        return Math.Max(0, savings);
    }

    private static long MeasureImageSavings(ZipArchiveEntry entry, ExcelCompressionProfile profile)
    {
        using var input = entry.Open();
        using var original = new MemoryStream();
        input.CopyTo(original);
        var compressed = ExcelEmbeddedImageCompressor.TryCompress(entry.FullName, original.ToArray(), profile);
        if (compressed is null || compressed.LongLength >= entry.Length)
        {
            return 0;
        }

        using var transformed = new MemoryStream(compressed, writable: false);
        return Math.Max(0, entry.CompressedLength - MeasureDeflatedLength(transformed));
    }

    private static long MeasureDeflatedLength(Stream input)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            input.CopyTo(deflate);
        }

        return output.Length;
    }

    private static long GetOrMeasure(string cachePath, Func<long> measure)
    {
        if (File.Exists(cachePath) && long.TryParse(
                File.ReadAllText(cachePath),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cached))
        {
            return cached;
        }

        var result = measure();
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, result.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, cachePath, overwrite: true);
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
