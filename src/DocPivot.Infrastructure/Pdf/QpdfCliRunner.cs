using System.Diagnostics;
using System.Text;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Pdf;

internal interface IQpdfCliRunner
{
    Task<ExternalProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

internal sealed class QpdfCliRunner : IQpdfCliRunner
{
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    public async Task<ExternalProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var processStarted = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The qpdf process could not be started.");
            }

            processStarted = true;
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);

            return new ExternalProcessResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (processStarted)
            {
                await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            }

            throw new TimeoutException($"qpdf exceeded the {timeout.TotalSeconds:0.#} second timeout.");
        }
        catch (OperationCanceledException)
        {
            if (processStarted)
            {
                await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (processStarted)
            {
                await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using var terminationSource = new CancellationTokenSource(TerminationTimeout);
        try
        {
            await process.WaitForExitAsync(terminationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (terminationSource.IsCancellationRequested)
        {
            throw new TimeoutException("qpdf did not exit within 5 seconds after termination.");
        }
    }
}
