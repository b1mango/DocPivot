using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class CompositePdfOperationsClientTests
{
    [Fact]
    public async Task OptimizeAsync_WithZeroStrength_RoutesToQpdf()
    {
        var expected = OperationExecutionResult.Succeeded(["qpdf-output.pdf"]);
        var qpdf = new FakePdfOperationsClient
        {
            OptimizeResult = expected,
        };
        var ghostscript = new FakeGhostscriptPdfOptimizer();
        var client = CreateClient(qpdf, ghostscript);
        var request = new PdfOptimizeRequest("input.pdf", "output.pdf", CompressionStrength: 0);

        var result = await client.OptimizeAsync(request);

        Assert.Same(expected, result);
        Assert.Equal([request], qpdf.OptimizeRequests);
        Assert.Empty(qpdf.PreflightRequests);
        Assert.Empty(ghostscript.OptimizeRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task OptimizeAsync_WithLossyStrength_RoutesToGhostscript(int compressionStrength)
    {
        const string inputPath = "input.pdf";
        const string outputPath = "output.pdf";
        var qpdf = new FakePdfOperationsClient(
            PdfPreflightResult.Succeeded(inputPath, pageCount: 4, hasSignatureFields: false),
            PdfPreflightResult.Succeeded(outputPath, pageCount: 4, hasSignatureFields: false));
        var expected = OperationExecutionResult.Succeeded([outputPath]);
        var ghostscript = new FakeGhostscriptPdfOptimizer
        {
            OptimizeResult = expected,
        };
        var client = CreateClient(qpdf, ghostscript);
        var request = new PdfOptimizeRequest(inputPath, outputPath, compressionStrength);

        var result = await client.OptimizeAsync(request);

        Assert.Same(expected, result);
        Assert.Empty(qpdf.OptimizeRequests);
        Assert.Equal([request], ghostscript.OptimizeRequests);
        Assert.Equal(
            [inputPath, outputPath],
            qpdf.PreflightRequests.Select(static preflight => preflight.InputPath));
    }

    [Fact]
    public async Task OptimizeAsync_WithEncryptedSource_RejectsBeforeGhostscript()
    {
        const string inputPath = "encrypted.pdf";
        var qpdf = new FakePdfOperationsClient(PdfPreflightResult.Failed(
            inputPath,
            "PDF_PASSWORD_REQUIRED",
            "A password is required.",
            isRetryable: false,
            isEncrypted: true));
        var ghostscript = new FakeGhostscriptPdfOptimizer();
        var client = CreateClient(qpdf, ghostscript);

        var result = await client.OptimizeAsync(
            new PdfOptimizeRequest(inputPath, "output.pdf", CompressionStrength: 1));

        Assert.False(result.IsSucceeded);
        Assert.Equal("PDF_PASSWORD_REQUIRED", result.ErrorCode);
        Assert.False(result.IsRetryable);
        Assert.Single(qpdf.PreflightRequests);
        Assert.Empty(ghostscript.OptimizeRequests);
    }

    [Fact]
    public async Task OptimizeAsync_WithSignedSource_ForcesCompressionAndSucceeds()
    {
        const string inputPath = "signed.pdf";
        var qpdf = new FakePdfOperationsClient(
            PdfPreflightResult.Succeeded(inputPath, pageCount: 2, hasSignatureFields: true),
            PdfPreflightResult.Succeeded("output.pdf", pageCount: 2, hasSignatureFields: false));
        var ghostscript = new FakeGhostscriptPdfOptimizer
        {
            OptimizeResult = OperationExecutionResult.Succeeded(["output.pdf"]),
        };
        var client = CreateClient(qpdf, ghostscript);

        var result = await client.OptimizeAsync(
            new PdfOptimizeRequest(inputPath, "output.pdf", CompressionStrength: 100));

        // Signature fields no longer block compression: the output is a
        // rewritten working copy and the source file is never modified.
        Assert.True(result.IsSucceeded);
        Assert.Equal(2, qpdf.PreflightRequests.Count);
        Assert.Single(ghostscript.OptimizeRequests);
    }

    [Fact]
    public async Task OptimizeAsync_WhenOutputPageCountDiffers_DeletesOutputAndReturnsFailure()
    {
        var root = CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            File.WriteAllText(outputPath, "partial Ghostscript artifact");
            var qpdf = new FakePdfOperationsClient(
                PdfPreflightResult.Succeeded(inputPath, pageCount: 3, hasSignatureFields: false),
                PdfPreflightResult.Succeeded(outputPath, pageCount: 2, hasSignatureFields: false));
            var ghostscript = new FakeGhostscriptPdfOptimizer
            {
                OptimizeResult = OperationExecutionResult.Succeeded([outputPath]),
            };
            var client = CreateClient(qpdf, ghostscript);

            var result = await client.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_OUTPUT_PAGE_COUNT_MISMATCH", result.ErrorCode);
            Assert.False(result.IsRetryable);
            Assert.False(File.Exists(outputPath));
            Assert.Single(ghostscript.OptimizeRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenGhostscriptSucceeds_PreservesMetricsAndNotices()
    {
        const string inputPath = "input.pdf";
        const string outputPath = "output.pdf";
        var notice = new WorkerNotice(
            "PDF_NO_COMPRESSION_BENEFIT",
            "The optimized output was not smaller.",
            "info");
        var metrics = new Dictionary<string, string>
        {
            ["imageDpi"] = "150",
            ["jpegQuality"] = "72",
        };
        var expected = OperationExecutionResult.Succeeded([outputPath], [notice], metrics);
        var qpdf = new FakePdfOperationsClient(
            PdfPreflightResult.Succeeded(inputPath, pageCount: 5, hasSignatureFields: false),
            PdfPreflightResult.Succeeded(outputPath, pageCount: 5, hasSignatureFields: false));
        var ghostscript = new FakeGhostscriptPdfOptimizer
        {
            OptimizeResult = expected,
        };
        var client = CreateClient(qpdf, ghostscript);

        var result = await client.OptimizeAsync(
            new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 75));

        Assert.Same(expected, result);
        Assert.Equal([notice], result.Notices);
        Assert.Equal(metrics, result.Metrics);
    }

    [Fact]
    public async Task ProbeEnginesAsync_AggregatesQpdfAndGhostscriptStatuses()
    {
        var qpdfStatus = QpdfToolStatus.Ready("qpdf.exe", "12.3.2");
        var ghostscriptStatus = GhostscriptEngineStatus.Unavailable(
            "GHOSTSCRIPT_RUNTIME_MISSING",
            "Ghostscript is not installed.");
        var qpdfProbe = new FakeQpdfToolProvider(qpdfStatus);
        var ghostscript = new FakeGhostscriptPdfOptimizer
        {
            ProbeResult = ghostscriptStatus,
        };
        var client = new CompositePdfOperationsClient(
            new FakePdfOperationsClient(),
            qpdfProbe,
            ghostscript);

        var result = await client.ProbeEnginesAsync();

        Assert.Same(qpdfStatus, result.Qpdf);
        Assert.Same(ghostscriptStatus, result.Ghostscript);
        Assert.True(result.IsCoreReady);
        Assert.False(result.IsLossyCompressionReady);
        Assert.Equal(1, qpdfProbe.ProbeCallCount);
        Assert.Equal(1, ghostscript.ProbeCallCount);
    }

    private static CompositePdfOperationsClient CreateClient(
        FakePdfOperationsClient qpdf,
        FakeGhostscriptPdfOptimizer ghostscript) =>
        new(qpdf, new FakeQpdfToolProvider(QpdfToolStatus.Ready("qpdf.exe", "12.3.2")), ghostscript);

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakePdfOperationsClient(params PdfPreflightResult[] preflightResults)
        : IPdfOperationsClient
    {
        private readonly Queue<PdfPreflightResult> _preflightResults = new(preflightResults);

        public List<PdfPreflightRequest> PreflightRequests { get; } = [];

        public List<PdfOptimizeRequest> OptimizeRequests { get; } = [];

        public OperationExecutionResult? OptimizeResult { get; init; }

        public Task<PdfPreflightResult> PreflightAsync(
            PdfPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreflightRequests.Add(request);
            if (_preflightResults.Count == 0)
            {
                throw new InvalidOperationException("No fake preflight result was configured.");
            }

            return Task.FromResult(_preflightResults.Dequeue());
        }

        public Task<OperationExecutionResult> MergeAsync(
            PdfMergeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationExecutionResult> SplitAsync(
            PdfSplitRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OptimizeRequests.Add(request);
            return Task.FromResult(
                OptimizeResult ?? throw new InvalidOperationException("No fake optimize result was configured."));
        }
    }

    private sealed class FakeQpdfToolProvider(QpdfToolStatus result) : IQpdfToolProvider
    {
        public int ProbeCallCount { get; private set; }

        public Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeGhostscriptPdfOptimizer : IGhostscriptPdfOptimizer
    {
        public GhostscriptEngineStatus ProbeResult { get; init; } = GhostscriptEngineStatus.Ready(
            "gswin64c.exe",
            "10.06.0");

        public OperationExecutionResult? OptimizeResult { get; init; }

        public int ProbeCallCount { get; private set; }

        public List<PdfOptimizeRequest> OptimizeRequests { get; } = [];

        public Task<GhostscriptEngineStatus> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCallCount++;
            return Task.FromResult(ProbeResult);
        }

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OptimizeRequests.Add(request);
            return Task.FromResult(
                OptimizeResult ?? throw new InvalidOperationException("No fake optimize result was configured."));
        }
    }
}
