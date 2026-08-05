using System.IO.Compression;
using System.Text.Json;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Diagnostics;

public static class DiagnosticBundleExporter
{
    public static async Task ExportAsync(
        string logDirectory,
        string destinationPath,
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);

        var normalizedLogDirectory = Path.GetFullPath(logDirectory);
        var logFiles = Directory.Exists(normalizedLogDirectory)
            ? Directory.GetFiles(normalizedLogDirectory, "*.ndjson", SearchOption.TopDirectoryOnly)
            : [];
        var stagingPath = AtomicOutputFile.CreateStagingPath(destinationPath, Guid.NewGuid());

        try
        {
            await using (var stream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteManifestAsync(
                    archive,
                    applicationVersion,
                    logFiles.Length,
                    cancellationToken).ConfigureAwait(false);

                foreach (var logFile in logFiles.Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(
                        $"logs/{Path.GetFileName(logFile)}",
                        CompressionLevel.SmallestSize);
                    await using var input = new FileStream(
                        logFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 64 * 1024,
                        useAsync: true);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            AtomicOutputFile.Commit(stagingPath, destinationPath);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive,
        string applicationVersion,
        int logCount,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow,
            applicationVersion,
            logCount,
            contentPolicy = "redacted-structured-logs-only",
        };
        var entry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
