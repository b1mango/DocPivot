using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfRotationCleanupTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("later-preflight")]
    public async Task MergeAsync_RotationUsesTaskDirectoryAndCleansFailures(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocPivot-rotation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.pdf");
            var second = Path.Combine(root, "second.pdf");
            File.WriteAllText(first, "%PDF-1.7\n%%EOF");
            File.WriteAllText(second, "%PDF-1.7\n%%EOF");
            var runner = new Runner(second, failure);
            var client = new QpdfOperationsClient(new ReadyProvider(), runner, TimeSpan.FromSeconds(5));
            var request = new PdfMergeRequest([first, second], Path.Combine(root, "out.pdf"), Rotations: [90, 0]);
            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.MergeAsync(request));
            else Assert.False((await client.MergeAsync(request)).IsSucceeded);
            Assert.NotNull(runner.RotationPath);
            Assert.NotEqual(root, Path.GetDirectoryName(runner.RotationPath));
            Assert.False(File.Exists(runner.RotationPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(runner.RotationPath)));
            Assert.Equal("%PDF-1.7\n%%EOF", File.ReadAllText(first));
            Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ReadyProvider : IQpdfToolProvider
    {
        public Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(QpdfToolStatus.Ready("fake-qpdf", "12.2.0"));
    }

    private sealed class Runner(string second, string failure) : IQpdfCliRunner
    {
        public string? RotationPath { get; private set; }
        public Task<ExternalProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.Any(argument => argument.StartsWith("--rotate=", StringComparison.Ordinal)))
            {
                RotationPath = arguments[^1];
                File.WriteAllText(RotationPath, "%PDF-1.7\n%%EOF");
                if (failure == "cancel") throw new OperationCanceledException();
                if (failure == "timeout") throw new TimeoutException();
            }
            if (failure == "later-preflight" && arguments.Contains(second)) throw new IOException();
            return Task.FromResult(arguments.Contains("--is-encrypted")
                ? new ExternalProcessResult(2, "", "")
                : new ExternalProcessResult(0, arguments.Contains("--json")
                    ? "{\"pages\":[{}],\"encrypt\":{\"encrypted\":false}}" : "", ""));
        }
    }
}
