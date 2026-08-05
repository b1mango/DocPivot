using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocPivot.Core.Excel;

namespace DocPivot.OfficeWorker;

internal static class ExcelEmbeddedImageCompressor
{
    private const long MaximumImageBytes = 50L * 1024L * 1024L;
    private const long MaximumImagePixels = 80_000_000L;

    public static ExcelEmbeddedImageCompressionResult Compress(
        string workbookPath,
        ExcelImageCompressionLevel level)
    {
        if (level == ExcelImageCompressionLevel.None ||
            !string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return ExcelEmbeddedImageCompressionResult.Unchanged;
        }

        using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        var candidates = archive.Entries
            .Where(static entry =>
                entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) &&
                IsSupportedImage(entry.FullName))
            .Select(static entry => ReadCandidate(entry))
            .ToArray();
        var compressedCount = 0;
        long bytesBefore = 0;
        long bytesAfter = 0;

        foreach (var candidate in candidates)
        {
            bytesBefore += candidate.Bytes.LongLength;
            var compressed = TryCompress(candidate, level);
            if (compressed is null || compressed.LongLength >= candidate.Bytes.LongLength)
            {
                bytesAfter += candidate.Bytes.LongLength;
                continue;
            }

            archive.GetEntry(candidate.FullName)?.Delete();
            var replacement = archive.CreateEntry(candidate.FullName, CompressionLevel.Optimal);
            replacement.LastWriteTime = candidate.LastWriteTime;
            using (var replacementStream = replacement.Open())
            {
                replacementStream.Write(compressed);
            }

            bytesAfter += compressed.LongLength;
            compressedCount++;
        }

        return new ExcelEmbeddedImageCompressionResult(
            candidates.Length,
            compressedCount,
            bytesBefore,
            bytesAfter);
    }

    public static bool HasEmbeddedMedia(string workbookPath)
    {
        if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries.Any(static entry =>
            entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) &&
            entry.Length > 0);
    }

    private static ImageCandidate ReadCandidate(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return new ImageCandidate(entry.FullName, entry.LastWriteTime, memory.ToArray());
    }

    private static byte[]? TryCompress(
        ImageCandidate candidate,
        ExcelImageCompressionLevel level)
    {
        if (candidate.Bytes.LongLength == 0 || candidate.Bytes.LongLength > MaximumImageBytes)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(candidate.Bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            var source = decoder.Frames[0];
            if ((long)source.PixelWidth * source.PixelHeight > MaximumImagePixels)
            {
                return null;
            }

            var (scale, jpegQuality) = GetProfile(level);
            BitmapSource outputSource = source;
            if (scale < 1d)
            {
                outputSource = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            BitmapEncoder encoder = string.Equals(
                Path.GetExtension(candidate.FullName),
                ".png",
                StringComparison.OrdinalIgnoreCase)
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = jpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(outputSource));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static (double Scale, int JpegQuality) GetProfile(ExcelImageCompressionLevel level) => level switch
    {
        ExcelImageCompressionLevel.HighQuality => (1d, 90),
        ExcelImageCompressionLevel.Balanced => (0.75d, 82),
        ExcelImageCompressionLevel.SmallFile => (0.5d, 72),
        _ => (1d, 100),
    };

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ImageCandidate(
        string FullName,
        DateTimeOffset LastWriteTime,
        byte[] Bytes);
}
