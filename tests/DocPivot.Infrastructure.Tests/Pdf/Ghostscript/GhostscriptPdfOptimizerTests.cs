using System.Globalization;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

public sealed class GhostscriptPdfOptimizerTests
{
    [Fact]
    public async Task OptimizeAsync_MapsCoreProfileAndAtomicallyCommitsOutput()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "输入 文档.pdf");
            var outputPath = Path.Combine(root, "输出 文档.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            var sourceHash = GhostscriptTestFiles.GetSha256(inputPath);
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                var stagingPath = GetStagingPath(arguments);
                File.Copy(inputPath, stagingPath);
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var optimizer = CreateOptimizer(runner);
            var profile = PdfCompressionProfile.FromStrength(67);

            var result = await optimizer.OptimizeAsync(new PdfOptimizeRequest(
                inputPath,
                outputPath,
                CompressionStrength: profile.Strength));

            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.Equal([outputPath], result.Artifacts);
            Assert.Equal(
                profile.ImageDpi!.Value.ToString(CultureInfo.InvariantCulture),
                result.Metrics["imageDpi"]);
            Assert.Equal(
                profile.JpegQuality!.Value.ToString(CultureInfo.InvariantCulture),
                result.Metrics["jpegQuality"]);
            Assert.Equal("false", result.Metrics["optimizationApplied"]);
            Assert.Contains(
                result.Notices,
                static notice => notice.Code == "PDF_NO_COMPRESSION_BENEFIT");
            Assert.Equal(sourceHash, GhostscriptTestFiles.GetSha256(inputPath));
            Assert.Equal(sourceHash, GhostscriptTestFiles.GetSha256(outputPath));
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
            Assert.Contains(
                $"-dColorImageResolution={profile.ImageDpi.Value}",
                runner.Arguments);
            Assert.Contains(
                $"-dGrayImageResolution={profile.ImageDpi.Value}",
                runner.Arguments);
            Assert.Contains(
                "-dMonoImageResolution=300",
                runner.Arguments);
            Assert.Contains($"-dJPEGQ={profile.JpegQuality.Value}", runner.Arguments);
            Assert.Contains("-dPassThroughJPEGImages=true", runner.Arguments);
            Assert.Contains("-dAutoFilterColorImages=true", runner.Arguments);
            Assert.Contains("-dAutoFilterGrayImages=true", runner.Arguments);
            Assert.Contains("-dPreserveAnnots=true", runner.Arguments);
            Assert.Contains("-dPreserveMarkedContent=true", runner.Arguments);
            Assert.StartsWith(
                "-sOutputFile=",
                runner.Arguments.Single(static argument =>
                    argument.StartsWith("-sOutputFile=", StringComparison.Ordinal)),
                StringComparison.Ordinal);
            Assert.Equal("-f", runner.Arguments[^2]);
            Assert.Equal(Path.GetFullPath(inputPath), runner.Arguments[^1]);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenOutputIsSmaller_MarksOptimizationApplied()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, new string('X', 256));
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-1.7\n%%EOF\n");
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var optimizer = CreateOptimizer(runner);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.Equal("true", result.Metrics["optimizationApplied"]);
            Assert.Equal("verified", result.Metrics["searchableTextStatus"]);
            Assert.DoesNotContain(
                result.Notices,
                static notice => notice.Code == "PDF_NO_COMPRESSION_BENEFIT");
            Assert.Contains(
                result.Notices,
                static notice => notice.Code == "PDF_LOSSY_PRESERVATION_LIMITS");
            Assert.True(new FileInfo(outputPath).Length < new FileInfo(inputPath).Length);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenSearchableTextVerificationFails_RejectsAndCleansOutput()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, new string('X', 256));
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-1.7\n%%EOF\n");
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var verifier = new FakeTextVerifier(
                GhostscriptSearchableTextVerification.Failed(
                    "PDF_SEARCHABLE_TEXT_MISMATCH",
                    "The text changed.",
                    12,
                    11));
            var optimizer = CreateOptimizer(runner, verifier);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_SEARCHABLE_TEXT_MISMATCH", result.ErrorCode);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
            Assert.Equal(2, verifier.CallCount);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_RetriesOnceWhenSearchableTextVerificationFailsTransiently()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, new string('X', 256));
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-1.7\n%%EOF\n");
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var verifier = new FakeTextVerifier(
                GhostscriptSearchableTextVerification.Failed(
                    "PDF_SEARCHABLE_TEXT_LOST",
                    "Compression removed the searchable text layer.",
                    12,
                    0),
                GhostscriptSearchableTextVerification.Succeeded("verified", 12, 12));
            var optimizer = CreateOptimizer(runner, verifier);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.Equal("verified", result.Metrics["searchableTextStatus"]);
            Assert.Equal(2, verifier.CallCount);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenRunnerFails_ReturnsErrorAndCleansPartialOutput()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-partial");
                return Task.FromResult(new ExternalProcessResult(
                    1,
                    string.Empty,
                    "Error: injected Ghostscript failure"));
            });
            var optimizer = CreateOptimizer(runner);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("GHOSTSCRIPT_OPTIMIZATION_FAILED", result.ErrorCode);
            Assert.Contains("injected Ghostscript failure", result.ErrorMessage, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenRunnerProducesIncompletePdf_RejectsAndCleansOutput()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-1.7\nmissing eof");
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var optimizer = CreateOptimizer(runner);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_OUTPUT_INVALID", result.ErrorCode);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenCanceled_CleansPartialOutputAndPropagatesCancellation()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new RecordingRunner(async (_, arguments, _, cancellationToken) =>
            {
                File.WriteAllText(GetStagingPath(arguments), "%PDF-partial");
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ExternalProcessResult(0, string.Empty, string.Empty);
            });
            var optimizer = CreateOptimizer(runner);
            using var cancellation = new CancellationTokenSource();

            var operation = optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50),
                cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_WhenCanceledAfterProcessExit_DoesNotCommitValidStagingOutput()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            using var cancellation = new CancellationTokenSource();
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                File.Copy(inputPath, GetStagingPath(arguments));
                cancellation.Cancel();
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var optimizer = CreateOptimizer(runner);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50),
                cancellation.Token));

            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.pdf"));
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_RejectsExistingDestinationWithoutCallingRunner()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            File.WriteAllText(outputPath, "existing");
            var runner = new UnexpectedRunner();
            var optimizer = CreateOptimizer(runner);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_OUTPUT_ALREADY_EXISTS", result.ErrorCode);
            Assert.Equal("existing", File.ReadAllText(outputPath));
            Assert.False(runner.WasCalled);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_RejectsSourceAsDestinationWithoutCallingRunner()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "SOURCE");
            var sourceHash = GhostscriptTestFiles.GetSha256(inputPath);
            var runner = new UnexpectedRunner();
            var optimizer = CreateOptimizer(runner);

            var result = await optimizer.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, inputPath, CompressionStrength: 50));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_REQUEST_INVALID", result.ErrorCode);
            Assert.Equal(sourceHash, GhostscriptTestFiles.GetSha256(inputPath));
            Assert.False(runner.WasCalled);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OptimizeAsync_RejectsLosslessStrengthBecauseQpdfOwnsThatPath()
    {
        var runner = new UnexpectedRunner();
        var optimizer = CreateOptimizer(runner);

        var result = await optimizer.OptimizeAsync(
            new PdfOptimizeRequest("input.pdf", "output.pdf", CompressionStrength: 0));

        Assert.False(result.IsSucceeded);
        Assert.Equal("PDF_REQUEST_INVALID", result.ErrorCode);
        Assert.False(runner.WasCalled);
    }

    private static GhostscriptPdfOptimizer CreateOptimizer(
        IGhostscriptProcessRunner runner,
        IGhostscriptSearchableTextVerifier? textVerifier = null) =>
        new(
            new ReadyProbe(),
            runner,
            textVerifier ?? new FakeTextVerifier(
                GhostscriptSearchableTextVerification.Succeeded("verified", 10, 10)),
            TimeSpan.FromSeconds(10));

    private static string GetStagingPath(IReadOnlyList<string> arguments) =>
        arguments.Single(static argument =>
                argument.StartsWith("-sOutputFile=", StringComparison.Ordinal))
            ["-sOutputFile=".Length..];

    private sealed class ReadyProbe : IGhostscriptRuntimeProbe
    {
        public Task<GhostscriptEngineStatus> ProbeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GhostscriptEngineStatus.Ready(
                "fake-gswin64c.exe",
                GhostscriptPdfOptimizer.PinnedVersion));
        }
    }

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ExternalProcessResult>> handler)
        : IGhostscriptProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            return handler(executablePath, arguments, timeout, cancellationToken);
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
            throw new InvalidOperationException("The runner must not be called.");
        }
    }

    private sealed class FakeTextVerifier : IGhostscriptSearchableTextVerifier
    {
        private readonly GhostscriptSearchableTextVerification[] _results;

        public FakeTextVerifier(params GhostscriptSearchableTextVerification[] results)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(results.Length, 1);
            _results = results;
        }

        public int CallCount { get; private set; }

        public Task<GhostscriptSearchableTextVerification> VerifyAsync(
            string executablePath,
            string sourcePath,
            string outputPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(CallCount, _results.Length - 1);
            CallCount++;
            return Task.FromResult(_results[index]);
        }
    }
}
