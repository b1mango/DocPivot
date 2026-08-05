using DocPivot.Infrastructure.Pdf;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfToolProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReportsMissingPinnedRuntime()
    {
        var root = CreateTestRoot();
        try
        {
            var probe = new QpdfToolProbe(root);

            var result = await probe.ProbeAsync();

            Assert.False(result.IsReady);
            Assert.Equal("QPDF_RUNTIME_MISSING", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_RejectsAlteredPinnedRuntimeFileBeforeExecution()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = Path.Combine(root, "vendor", "qpdf", QpdfToolProbe.PinnedVersion, "bin");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "concrt140.dll"), "altered");
            var runner = new UnexpectedRunner();
            var probe = new QpdfToolProbe(root, runner, TimeSpan.FromSeconds(1));

            var result = await probe.ProbeAsync();

            Assert.False(result.IsReady);
            Assert.Equal("QPDF_RUNTIME_HASH_MISMATCH", result.ErrorCode);
            Assert.False(runner.WasCalled);
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

    private sealed class UnexpectedRunner : IQpdfCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<DocPivot.Infrastructure.Runtime.ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The runner must not be called after a hash mismatch.");
        }
    }
}
