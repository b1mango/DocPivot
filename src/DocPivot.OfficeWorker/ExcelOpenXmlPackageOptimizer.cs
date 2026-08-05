using System.IO.Compression;

namespace DocPivot.OfficeWorker;

internal static class ExcelOpenXmlPackageOptimizer
{
    public static bool TryOptimize(string workbookPath)
    {
        if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceBytes = new FileInfo(workbookPath).Length;
        var optimizedPath = $"{workbookPath}.{Guid.NewGuid():N}.repack";
        try
        {
            using (var sourceStream = new FileStream(
                       workbookPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var source = new ZipArchive(sourceStream, ZipArchiveMode.Read))
            using (var destinationStream = new FileStream(
                       optimizedPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    var replacement = destination.CreateEntry(
                        entry.FullName,
                        CompressionLevel.SmallestSize);
                    replacement.LastWriteTime = entry.LastWriteTime;
                    replacement.ExternalAttributes = entry.ExternalAttributes;
                    using var input = entry.Open();
                    using var output = replacement.Open();
                    input.CopyTo(output);
                }
            }

            if (new FileInfo(optimizedPath).Length >= sourceBytes)
            {
                return false;
            }

            File.Move(optimizedPath, workbookPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(optimizedPath))
            {
                File.Delete(optimizedPath);
            }
        }
    }
}
