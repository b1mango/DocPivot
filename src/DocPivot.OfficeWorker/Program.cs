using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DocPivot.Core.Contracts;

namespace DocPivot.OfficeWorker;

[SupportedOSPlatform("windows")]
public static class Program
{
    private const int MaximumMessageCharacters = 64 * 1024;
    private const int DefaultTimeoutSeconds = 60;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [STAThread]
    public static int Main(string[] args)
    {
        return Run(args);
    }

    public static int Run(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            if (args.Length == 1 && string.Equals(args[0], "--probe", StringComparison.Ordinal))
            {
                return WriteProbeResult();
            }

            if (args.Length >= 1 && string.Equals(args[0], "--execute", StringComparison.Ordinal))
            {
                return ExecuteSingleJob(ParseTimeout(args));
            }

            Console.Error.WriteLine("Usage: DocPivot.OfficeWorker --probe | --execute");
            return 64;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The Office worker terminated unexpectedly.");
            return 70;
        }
    }

    private static int WriteProbeResult()
    {
        var (capabilities, metadata) = OfficeInstallationProbe.Probe();
        var status = capabilities.Values.All(static available => available) ? "ready" : "unavailable";
        var result = new WorkerProbeResult(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.ProbeResult,
            "office",
            status,
            capabilities,
            metadata);

        WriteMessage(result);
        return status == "ready" ? 0 : 2;
    }

    private static int ExecuteSingleJob(TimeSpan timeout)
    {
        var line = Console.In.ReadLine()?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumMessageCharacters)
        {
            WriteMessage(WorkerErrorMessage.Create(
                Guid.Empty,
                "START_MESSAGE_INVALID",
                "A single bounded start message is required.",
                false));
            return 65;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<WorkerStartEnvelope>(line, JsonOptions)
                ?? throw new JsonException("Start message was empty.");
            if (OfficeWorkerOperations.IsExcelToolOperation(envelope.Operation))
            {
                var request = JsonSerializer.Deserialize<WorkerExcelToolStartMessage>(line, JsonOptions)
                    ?? throw new JsonException("Excel tool start message was empty.");
                return ExecuteWithTimeout(
                    request.JobId,
                    request.Operation,
                    (writeMessage, reportProcessId) => OfficeConversionRunner.Run(
                        request,
                        writeMessage,
                        reportProcessId),
                    timeout);
            }

            var conversionRequest = JsonSerializer.Deserialize<WorkerStartMessage>(line, JsonOptions)
                ?? throw new JsonException("Start message was empty.");
            return ExecuteWithTimeout(
                conversionRequest.JobId,
                conversionRequest.Operation,
                (writeMessage, reportProcessId) => OfficeConversionRunner.Run(
                    conversionRequest,
                    writeMessage,
                    reportProcessId),
                timeout);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteMessage(WorkerErrorMessage.Create(
                Guid.Empty,
                "START_MESSAGE_JSON_INVALID",
                "The start message is not valid JSON.",
                false));
            return 65;
        }
    }

    private static int ExecuteWithTimeout(
        Guid jobId,
        string operation,
        Func<Action<object>, Action<int>, int> execute,
        TimeSpan timeout)
    {
        var resultCode = 70;
        var timedOut = 0;
        var officeProcessId = 0;
        var workerStartedAt = DateTimeOffset.UtcNow;
        var expectedProcessName = operation == OfficeWorkerOperations.WordToPdf ? "WINWORD" : "EXCEL";
        var thread = new Thread(() =>
        {
            try
            {
                resultCode = execute(
                    message =>
                    {
                        if (Volatile.Read(ref timedOut) == 0)
                        {
                            WriteMessage(message);
                        }
                    },
                    processId => Interlocked.Exchange(ref officeProcessId, processId));
            }
            catch (Exception)
            {
                resultCode = 70;
                if (Volatile.Read(ref timedOut) == 0)
                {
                    WriteMessage(WorkerErrorMessage.Create(
                        jobId,
                        "WORKER_UNEXPECTED_FAILURE",
                        "The Office worker encountered an unexpected failure.",
                        true));
                }
            }
        })
        {
            IsBackground = true,
            Name = "DocPivot Office COM",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (thread.Join(timeout))
        {
            OfficeProcessIdentity.TryTerminate(
                Volatile.Read(ref officeProcessId),
                expectedProcessName,
                workerStartedAt);
            return resultCode;
        }

        Interlocked.Exchange(ref timedOut, 1);
        OfficeProcessIdentity.TryTerminate(
            Volatile.Read(ref officeProcessId),
            expectedProcessName,
            workerStartedAt);
        _ = thread.Join(TimeSpan.FromSeconds(5));
        WriteMessage(WorkerErrorMessage.Create(
            jobId,
            "OFFICE_TIMEOUT",
            "Office conversion exceeded the configured timeout.",
            true));
        return 124;
    }

    private static TimeSpan ParseTimeout(string[] args)
    {
        if (args.Length == 1)
        {
            return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        }

        if (args.Length == 3 &&
            string.Equals(args[1], "--timeout-seconds", StringComparison.Ordinal) &&
            int.TryParse(args[2], out var seconds) &&
            seconds is >= 10 and <= 300)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        throw new ArgumentException("--timeout-seconds must be between 10 and 300.", nameof(args));
    }

    private static void WriteMessage(object message)
    {
        Console.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
        Console.Out.Flush();
    }

    private sealed record WorkerStartEnvelope(
        int V,
        string Type,
        Guid JobId,
        string Operation);
}
