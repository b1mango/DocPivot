using System.Diagnostics;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Runtime;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_PreCanceledTokenDoesNotStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"DocPivot-must-not-start-{Guid.NewGuid():N}.exe");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalProcessRunner.RunAsync(
            missingExecutable,
            [],
            "unused",
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_CapturesOutputAndExitCode()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = await ExternalProcessRunner.RunAsync(
            commandProcessor,
            ["/d", "/q"],
            "echo worker-ready & exit 0",
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("worker-ready", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    public async Task RunAsync_ReportsStandardOutputLinesAsTheyArrive()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var lines = new List<string>();

        var result = await ExternalProcessRunner.RunAsync(
            commandProcessor,
            ["/d", "/q"],
            "echo preflight & echo completed & exit 0",
            TimeSpan.FromSeconds(10),
            lines.Add);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, static line => line.Contains("preflight", StringComparison.Ordinal));
        Assert.Contains(lines, static line => line.Contains("completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_TerminatesProcessAfterTimeout()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => ExternalProcessRunner.RunAsync(
            commandProcessor,
            ["/d", "/q"],
            "ping -n 30 127.0.0.1 > nul",
            TimeSpan.FromMilliseconds(250)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_TerminatesProcessWhenCallerCancels()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalProcessRunner.RunAsync(
            commandProcessor,
            ["/d", "/q"],
            "ping -n 30 127.0.0.1 > nul",
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
