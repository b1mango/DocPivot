using System.Text;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class PdfCompressionEstimatorTests : IDisposable
{
    private readonly string _root = GhostscriptTestFiles.CreateTestRoot();

    [Fact]
    public async Task ExactEstimate_CachesAndInvalidatesOnFileChange()
    {
        var path = CreatePdf();
        var client = new RecordingClient();
        using var estimator = new PdfCompressionEstimator(client);
        var first = await estimator.EstimateAsync(path, 0, null, default);
        Assert.True(first.IsExact);
        Assert.Equal(100, first.EstimatedBytes);
        Assert.Equal(first, await estimator.EstimateAsync(path, 0, null, default));
        Assert.Equal(1, client.OptimizeCalls);
        Assert.Equal(0, client.LastOptimize!.CompressionStrength);
        Assert.Equal(path, client.LastOptimize.InputPath);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await estimator.EstimateAsync(path, 0, null, default);
        await File.AppendAllTextAsync(path, "\n% changed\n");
        await estimator.EstimateAsync(path, 0, null, default);
        await estimator.EstimateAsync(path, 0, "different", default);
        Assert.Equal(4, client.OptimizeCalls);
        Assert.Equal(0, client.SplitCalls);
        Assert.False(Directory.Exists(Path.GetDirectoryName(client.LastOptimize.OutputPath)));
    }

    [Fact]
    public async Task LargeEstimate_ReusesUniformSampleAcrossStrengthsAndClampsGrowth()
    {
        var path = CreatePdf(41);
        var client = new RecordingClient { PageCount = 41 };
        using var estimator = new PdfCompressionEstimator(client);
        var estimate = await estimator.EstimateAsync(path, 25, "password", default);
        Assert.False(estimate.IsExact);
        Assert.Equal((long)Math.Round(estimate.OriginalBytes / 2d), estimate.EstimatedBytes);
        Assert.Equal("1,6,12,18,23,29,35,41", client.LastSplit!.Selection.PageRange);
        Assert.Equal("password", client.LastSplit.Password);
        Assert.Null(client.LastOptimize!.Password);
        client.OutputSize = 400;
        var next = await estimator.EstimateAsync(path, 75, "password", default);
        Assert.Equal(next.OriginalBytes, next.EstimatedBytes);
        Assert.Equal(1, client.SplitCalls);
        Assert.Equal(1, client.PreflightCalls);
        Assert.Equal(2, client.OptimizeCalls);
    }

    [Fact]
    public async Task Cancellation_CleansWorkspaceAndDoesNotCacheResult()
    {
        var path = CreatePdf();
        var client = new RecordingClient { Block = true };
        using var estimator = new PdfCompressionEstimator(client);
        using var cancellation = new CancellationTokenSource();
        var pending = estimator.EstimateAsync(path, 50, null, cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Directory.Exists(Path.GetDirectoryName(client.LastOptimize!.OutputPath)));
        client.Block = false;
        await estimator.EstimateAsync(path, 50, null, default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            estimator.EstimateAsync(path, 50, null, canceled.Token));
        Assert.Equal(2, client.OptimizeCalls);
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsedFile()
    {
        var client = new RecordingClient();
        using var estimator = new PdfCompressionEstimator(client);
        var first = CreatePdf();
        await estimator.EstimateAsync(first, 0, null, default);
        for (var index = 0; index < 8; index++)
        {
            await estimator.EstimateAsync(CreatePdf(), 0, null, default);
        }

        await estimator.EstimateAsync(first, 0, null, default);
        Assert.Equal(10, client.OptimizeCalls);
    }

    [Fact]
    public async Task ExactEstimate_MatchesRealCompressionWithImagePages()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var client = new CompositePdfOperationsClient(GhostscriptTestFiles.FindRepositoryRoot());
        var engines = await client.ProbeEnginesAsync();
        using var estimator = new PdfCompressionEstimator(client);
        var path = CreatePdf();
        foreach (var strength in engines.IsLossyCompressionReady ? new[] { 0, 50 } : new[] { 0 })
        {
            var output = Path.Combine(_root, $"actual-{strength}.pdf");
            var actual = await client.OptimizeAsync(new PdfOptimizeRequest(path, output, strength));
            Assert.True(actual.IsSucceeded, actual.ErrorMessage);
            var estimate = await estimator.EstimateAsync(path, strength, null, default);
            Assert.True(estimate.IsExact);
            Assert.Equal(Math.Min(new FileInfo(path).Length, new FileInfo(actual.Artifacts[0]).Length),
                estimate.EstimatedBytes);
        }
    }

    [Fact]
    public async Task LosslessEstimate_UsesInstalledQpdfOnMac()
    {
        const string executable = "/opt/homebrew/bin/qpdf";
        if (!OperatingSystem.IsMacOS() || !File.Exists(executable))
        {
            return;
        }

        var client = new QpdfOperationsClient(new InstalledQpdf(executable), new QpdfCliRunner(), TimeSpan.FromSeconds(30));
        using var estimator = new PdfCompressionEstimator(client);
        var path = CreatePdf();
        var check = await new QpdfCliRunner().RunAsync(executable, ["--check", path], TimeSpan.FromSeconds(10));
        Assert.Equal(0, check.ExitCode);
        var output = Path.Combine(_root, "actual.pdf");
        var actual = await client.OptimizeAsync(new PdfOptimizeRequest(path, output));
        Assert.True(actual.IsSucceeded, actual.ErrorMessage);
        var estimate = await estimator.EstimateAsync(path, 0, null, default);
        Assert.True(estimate.IsExact);
        Assert.Equal(new FileInfo(actual.Artifacts[0]).Length, estimate.EstimatedBytes);
        var large = await estimator.EstimateAsync(CreatePdf(41), 0, null, default);
        Assert.False(large.IsExact);
        Assert.InRange(large.EstimatedBytes, 1, large.OriginalBytes);
    }

    [Fact]
    public async Task ProcessRunners_CancellationTerminatesRealProcessesOnMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (var ghostscript in new[] { false, true })
        {
            var pidPath = Path.Combine(_root, "pid");
            using var cancellation = new CancellationTokenSource();
            string[] arguments = ["-c", "echo $$ > \"$1\"; exec sleep 30", "estimate-test", pidPath];
            var task = ghostscript
                ? new GhostscriptProcessRunner().RunAsync("/bin/sh", arguments, TimeSpan.FromSeconds(10), cancellation.Token)
                : new QpdfCliRunner().RunAsync("/bin/sh", arguments, TimeSpan.FromSeconds(10), cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidPath) || new FileInfo(pidPath).Length == 0)
            {
                await Task.Delay(10, deadline.Token);
            }

            var pid = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
            File.Delete(pidPath);
        }
    }

    private sealed class InstalledQpdf(string executable) : IQpdfToolProvider
    {
        public Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(QpdfToolStatus.Ready(executable, "installed"));
    }

    private string CreatePdf(int pages = 1)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.pdf");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages} /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{5 + i} 0 R"))}] >>",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 25 >>\nstream\nFF000000FF000000FFFFFFFF>\nendstream",
            "<< /Length 29 >>\nstream\nq 200 0 0 200 0 0 cm /Im Do Q\nendstream",
        };
        objects.AddRange(Enumerable.Repeat(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im 3 0 R >> >> /Contents 4 0 R >>", pages));
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }

        var xref = text.Length;
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
        return path;
    }

    public void Dispose() => GhostscriptTestFiles.DeleteTestRoot(_root);

    private sealed class RecordingClient : IPdfOperationsClient
    {
        public int PageCount { get; init; } = 1;
        public int OutputSize { get; set; } = 100;
        public bool Block { get; set; }
        public int PreflightCalls { get; private set; }
        public int SplitCalls { get; private set; }
        public int OptimizeCalls { get; private set; }
        public PdfOptimizeRequest? LastOptimize { get; private set; }
        public PdfSplitRequest? LastSplit { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PdfPreflightResult> PreflightAsync(PdfPreflightRequest request, CancellationToken cancellationToken = default)
        {
            PreflightCalls++;
            return Task.FromResult(PdfPreflightResult.Succeeded(request.InputPath, PageCount, false));
        }

        public async Task<OperationExecutionResult> OptimizeAsync(PdfOptimizeRequest request, CancellationToken cancellationToken = default)
        {
            OptimizeCalls++;
            LastOptimize = request;
            Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await File.WriteAllBytesAsync(request.OutputPath, new byte[OutputSize], cancellationToken);
            return OperationExecutionResult.Succeeded([request.OutputPath]);
        }

        public async Task<OperationExecutionResult> SplitAsync(PdfSplitRequest request, CancellationToken cancellationToken = default)
        {
            SplitCalls++;
            LastSplit = request;
            await File.WriteAllBytesAsync(request.OutputPath, new byte[200], cancellationToken);
            return OperationExecutionResult.Succeeded([request.OutputPath]);
        }

        public Task<OperationExecutionResult> MergeAsync(PdfMergeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
