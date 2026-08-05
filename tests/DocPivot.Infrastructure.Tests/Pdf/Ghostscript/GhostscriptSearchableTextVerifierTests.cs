using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

public sealed class GhostscriptSearchableTextVerifierTests
{
    [Fact]
    public async Task VerifyAsync_NormalizesWhitespaceAndRequiresMatchingText()
    {
        var runner = new SequenceRunner(
            new ExternalProcessResult(0, "Alpha  Beta\r\nGamma", string.Empty),
            new ExternalProcessResult(0, "Alpha\tBeta Gamma", string.Empty));
        var verifier = new GhostscriptSearchableTextVerifier(runner);

        var result = await verifier.VerifyAsync(
            "gswin64c.exe",
            "source.pdf",
            "output.pdf",
            TimeSpan.FromSeconds(5));

        Assert.True(result.IsSucceeded, result.ErrorMessage);
        Assert.Equal("verified", result.Status);
        Assert.Equal(14, result.SourceCharacterCount);
        Assert.Equal(result.SourceCharacterCount, result.OutputCharacterCount);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, static call =>
        {
            Assert.Contains("-sDEVICE=txtwrite", call);
            Assert.Contains("-sOutputFile=-", call);
            Assert.Equal("-f", call[^2]);
        });
        Assert.Equal("source.pdf", runner.Calls[0][^1]);
        Assert.Equal("output.pdf", runner.Calls[1][^1]);
    }

    [Fact]
    public async Task VerifyAsync_WhenSourceHasNoText_ReportsNotPresent()
    {
        var runner = new SequenceRunner(
            new ExternalProcessResult(0, " \r\n\t", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty));
        var verifier = new GhostscriptSearchableTextVerifier(runner);

        var result = await verifier.VerifyAsync(
            "gswin64c.exe",
            "source.pdf",
            "output.pdf",
            TimeSpan.FromSeconds(5));

        Assert.True(result.IsSucceeded, result.ErrorMessage);
        Assert.Equal("not-present", result.Status);
        Assert.Equal(0, result.SourceCharacterCount);
    }

    [Fact]
    public async Task VerifyAsync_WhenTextChanges_ReturnsBlockingMismatch()
    {
        var runner = new SequenceRunner(
            new ExternalProcessResult(0, "Invoice 2026", string.Empty),
            new ExternalProcessResult(0, "Invoice 2025", string.Empty));
        var verifier = new GhostscriptSearchableTextVerifier(runner);

        var result = await verifier.VerifyAsync(
            "gswin64c.exe",
            "source.pdf",
            "output.pdf",
            TimeSpan.FromSeconds(5));

        Assert.False(result.IsSucceeded);
        Assert.Equal("PDF_SEARCHABLE_TEXT_MISMATCH", result.ErrorCode);
        Assert.True(result.SourceCharacterCount > 0);
        Assert.True(result.OutputCharacterCount > 0);
    }

    [Fact]
    public async Task VerifyAsync_WhenExtractionFails_ReturnsVerificationFailure()
    {
        var runner = new SequenceRunner(
            new ExternalProcessResult(1, string.Empty, "injected failure"));
        var verifier = new GhostscriptSearchableTextVerifier(runner);

        var result = await verifier.VerifyAsync(
            "gswin64c.exe",
            "source.pdf",
            "output.pdf",
            TimeSpan.FromSeconds(5));

        Assert.False(result.IsSucceeded);
        Assert.Equal("PDF_TEXT_VERIFICATION_FAILED", result.ErrorCode);
        Assert.Single(runner.Calls);
    }

    private sealed class SequenceRunner(params ExternalProcessResult[] results)
        : IGhostscriptProcessRunner
    {
        private readonly Queue<ExternalProcessResult> _results = new(results);

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
