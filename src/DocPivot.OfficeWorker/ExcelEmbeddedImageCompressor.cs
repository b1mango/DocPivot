using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocPivot.Core.Excel;

namespace DocPivot.OfficeWorker;

internal static class ExcelEmbeddedImageCompressor
{
    internal const long MaximumImageBytes = 50L * 1024L * 1024L;
    private const long MaximumImagePixels = 80_000_000L;

    public static ExcelEmbeddedImageCompressionResult Compress(
        string workbookPath,
        ExcelCompressionProfile profile)
    {
        if (profile.IsLossless ||
            !string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return ExcelEmbeddedImageCompressionResult.Unchanged;
        }

        using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        var candidates = archive.Entries
            .Where(static entry =>
                entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) &&
                IsSupportedImage(entry.FullName) &&
                entry.Length <= MaximumImageBytes)
            .Select(static entry => ReadCandidate(entry))
            .ToArray();
        var compressedCount = 0;
        long bytesBefore = 0;
        long bytesAfter = 0;

        foreach (var candidate in candidates)
        {
            bytesBefore += candidate.Bytes.LongLength;
            var compressed = TryCompress(candidate.FullName, candidate.Bytes, profile);
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

    internal static byte[]? TryCompress(
        string imagePath,
        byte[] imageBytes,
        ExcelCompressionProfile profile)
    {
        if (profile.IsLossless || imageBytes.LongLength == 0 || imageBytes.LongLength > MaximumImageBytes)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            var source = decoder.Frames[0];
            if ((long)source.PixelWidth * source.PixelHeight > MaximumImagePixels)
            {
                return null;
            }

            BitmapSource outputSource = source;
            if (profile.ImageScale < 1d)
            {
                outputSource = new TransformedBitmap(source, new ScaleTransform(profile.ImageScale, profile.ImageScale));
            }

            BitmapEncoder encoder = string.Equals(
                Path.GetExtension(imagePath),
                ".png",
                StringComparison.OrdinalIgnoreCase)
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = profile.JpegQuality };
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

    internal static bool IsSupportedImage(string path)
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
