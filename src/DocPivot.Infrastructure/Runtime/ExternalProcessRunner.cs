using System.Diagnostics;
using System.Text;

namespace DocPivot.Infrastructure.Runtime;

public static class ExternalProcessRunner
{
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ExternalProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string standardInputLine,
        TimeSpan timeout,
        Action<string>? standardOutputLineReceived = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInputLine);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
                throw new InvalidOperationException("The worker process could not be started.");
            }

            processStarted = true;
            var standardOutputTask = ReadStandardOutputAsync(
                process.StandardOutput,
                standardOutputLineReceived);
            var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardInput.WriteLineAsync(
                    standardInputLine.AsMemory(),
                    linkedSource.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linkedSource.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);

            return new ExternalProcessResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (processStarted)
            {
                await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            }

            throw new TimeoutException($"Worker process exceeded the {timeout.TotalSeconds:0.#} second timeout.");
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

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader reader,
        Action<string>? lineReceived)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            output.AppendLine(line);
            lineReceived?.Invoke(line);
        }

        return output.ToString();
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
            throw new TimeoutException("Worker process did not exit within 5 seconds after termination.");
        }
    }
}
