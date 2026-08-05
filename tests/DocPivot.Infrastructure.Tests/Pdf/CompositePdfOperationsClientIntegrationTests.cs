using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class CompositePdfOperationsClientIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LossyOptimize_RunsBothPinnedEnginesAndPreservesPageCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = GhostscriptTestFiles.FindRepositoryRoot();
        var testRoot = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(testRoot, "组合 输入.pdf");
            var outputPath = Path.Combine(testRoot, "组合 输出.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(
                inputPath,
                "COMPOSITE_RUNTIME_SMOKE",
                unusedPaddingBytes: 256 * 1024);
            var sourceHash = GhostscriptTestFiles.GetSha256(inputPath);
            var client = new CompositePdfOperationsClient(repositoryRoot);

            var engines = await client.ProbeEnginesAsync();
            var source = await client.PreflightAsync(new PdfPreflightRequest(inputPath));
            var result = await client.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));
            var output = await client.PreflightAsync(new PdfPreflightRequest(outputPath));

            Assert.True(engines.IsCoreReady, engines.Qpdf.ErrorMessage);
            Assert.True(engines.IsLossyCompressionReady, engines.Ghostscript.ErrorMessage);
            Assert.True(source.IsSucceeded, source.ErrorMessage);
            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.True(output.IsSucceeded, output.ErrorMessage);
            Assert.Equal(source.PageCount, output.PageCount);
            Assert.Equal("true", result.Metrics["optimizationApplied"]);
            Assert.Equal("verified", result.Metrics["searchableTextStatus"]);
            Assert.Equal(
                result.Metrics["sourceTextCharacters"],
                result.Metrics["outputTextCharacters"]);
            Assert.True(PdfFileInspector.Inspect(outputPath).IsValid);
            Assert.Equal(sourceHash, GhostscriptTestFiles.GetSha256(inputPath));
            Assert.Empty(Directory.EnumerateFiles(testRoot, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(testRoot);
        }
    }
}
