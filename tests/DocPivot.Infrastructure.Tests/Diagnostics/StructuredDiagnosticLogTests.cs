using System.IO.Compression;
using System.Text.Json;
using DocPivot.Infrastructure.Diagnostics;

namespace DocPivot.Infrastructure.Tests.Diagnostics;

public sealed class StructuredDiagnosticLogTests
{
    [Fact]
    public async Task WriteAsync_RedactsDocumentIdentityAndSecrets()
    {
        var root = CreateTestRoot();
        try
        {
            var logPath = Path.Combine(root, "app.ndjson");
            var documentPath = Path.Combine(root, "customer-payroll.xlsx");
            using var log = new StructuredDiagnosticLog(logPath);

            await log.WriteAsync(
                DiagnosticLevel.Error,
                "office.export.failed",
                $"Export failed for {documentPath}",
                new Dictionary<string, string?>
                {
                    ["inputPath"] = documentPath,
                    ["apiToken"] = "do-not-log-this",
                    ["errorCode"] = "OFFICE_TIMEOUT",
                });

            var content = await File.ReadAllTextAsync(logPath);
            using var document = JsonDocument.Parse(content);
            var rootElement = document.RootElement;
            var serializedProperties = rootElement.GetProperty("properties");

            Assert.DoesNotContain(documentPath, content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer-payroll.xlsx", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("do-not-log-this", content, StringComparison.Ordinal);
            Assert.Contains(
                "<path:redacted>",
                rootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "sha256:",
                serializedProperties.GetProperty("inputPath").GetString(),
                StringComparison.Ordinal);
            Assert.Equal("<redacted>", serializedProperties.GetProperty("apiToken").GetString());
            Assert.Equal("OFFICE_TIMEOUT", serializedProperties.GetProperty("errorCode").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_IncludesOnlyManifestAndStructuredLogs()
    {
        var root = CreateTestRoot();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDirectory);
            await File.WriteAllTextAsync(Path.Combine(logDirectory, "app.ndjson"), "{\"eventName\":\"ready\"}");
            await File.WriteAllTextAsync(Path.Combine(logDirectory, "document.txt"), "private document body");
            var bundlePath = Path.Combine(root, "diagnostics.zip");

            await DiagnosticBundleExporter.ExportAsync(logDirectory, bundlePath, "0.4.0-dev");

            using var archive = ZipFile.OpenRead(bundlePath);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("logs/app.ndjson"));
            Assert.Null(archive.GetEntry("logs/document.txt"));
            Assert.Equal(2, archive.Entries.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndRejectsFurtherWrites()
    {
        var root = CreateTestRoot();
        try
        {
            var logPath = Path.Combine(root, "app.ndjson");
            var log = new StructuredDiagnosticLog(logPath);

            log.Dispose();
            log.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                log.WriteAsync(
                    DiagnosticLevel.Information,
                    "application.closed",
                    "This event must not be written."));
            Assert.False(File.Exists(logPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
