using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

public sealed class GhostscriptRuntimeTests
{
    private static readonly TimeSpan IntegrationTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ProbeAsync_RejectsAlteredExecutableBeforeStartingProcess()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var runtimeBin = Path.Combine(
                root,
                "vendor",
                "ghostscript",
                GhostscriptPdfOptimizer.PinnedVersion,
                "runtime",
                "bin");
            Directory.CreateDirectory(runtimeBin);
            File.WriteAllText(Path.Combine(runtimeBin, "gswin64c.exe"), "altered");
            var runner = new UnexpectedRunner();
            var probe = new GhostscriptRuntimeProbe(root, runner, TimeSpan.FromSeconds(1));

            var status = await probe.ProbeAsync();

            Assert.False(status.IsReady);
            Assert.Equal("GHOSTSCRIPT_RUNTIME_HASH_MISMATCH", status.ErrorCode);
            Assert.False(runner.WasCalled);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProbeAndOptimize_UsePinnedRuntimeWithoutChangingSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = GhostscriptTestFiles.FindRepositoryRoot();
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "真实 输入.pdf");
            var outputPath = Path.Combine(root, "真实 输出.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "GHOSTSCRIPT_SMOKE");
            var sourceHash = GhostscriptTestFiles.GetSha256(inputPath);
            var optimizer = new GhostscriptPdfOptimizer(repositoryRoot);

            var status = await optimizer.ProbeAsync();
            var result = await optimizer.OptimizeAsync(
                    new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50))
                .WaitAsync(IntegrationTimeout);

            Assert.True(status.IsReady, status.ErrorMessage);
            Assert.Equal(GhostscriptPdfOptimizer.PinnedVersion, status.Version);
            Assert.Equal(
                Path.Combine(repositoryRoot, GhostscriptPdfOptimizer.RelativeExecutablePath),
                status.ExecutablePath);
            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.Equal([outputPath], result.Artifacts);
            Assert.True(PdfFileInspector.Inspect(outputPath).IsValid);
            Assert.Equal(sourceHash, GhostscriptTestFiles.GetSha256(inputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    private sealed class UnexpectedRunner : IGhostscriptProcessRunner
    {
        public bool WasCalled { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The runner must not execute an altered runtime.");
        }
    }
}
