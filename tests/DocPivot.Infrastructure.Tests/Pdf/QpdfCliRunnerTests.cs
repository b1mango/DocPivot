using System.Diagnostics;
using DocPivot.Infrastructure.Pdf;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfCliRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesOutputWithoutStandardInput()
    {
        var runner = new QpdfCliRunner();
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = await runner.RunAsync(
            commandProcessor,
            ["/d", "/s", "/c", "echo qpdf-runner-ready"],
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("qpdf-runner-ready", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TerminatesProcessTreeAfterTimeout()
    {
        var runner = new QpdfCliRunner();
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
            commandProcessor,
            ["/d", "/s", "/c", "ping -n 30 127.0.0.1 > nul"],
            TimeSpan.FromMilliseconds(250)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_TerminatesProcessTreeWhenCallerCancels()
    {
        var runner = new QpdfCliRunner();
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            commandProcessor,
            ["/d", "/s", "/c", "ping -n 30 127.0.0.1 > nul"],
            TimeSpan.FromSeconds(10),
            cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
