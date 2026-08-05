using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocPivot.Infrastructure.Diagnostics;

public sealed class StructuredDiagnosticLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The semaphore is never disposed so writes admitted before logger shutdown can finish safely.")]
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public StructuredDiagnosticLog(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        LogFilePath = Path.GetFullPath(logFilePath);
        var directory = Path.GetDirectoryName(LogFilePath)
            ?? throw new ArgumentException("Log file must have a parent directory.", nameof(logFilePath));
        Directory.CreateDirectory(directory);
    }

    public string LogFilePath { get; }

    public async Task WriteAsync(
        DiagnosticLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(message);

        var diagnosticEvent = new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            level,
            eventName,
            DiagnosticRedactor.RedactMessage(message),
            DiagnosticRedactor.RedactProperties(properties));

        var json = JsonSerializer.Serialize(diagnosticEvent, JsonOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                LogFilePath,
                json + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
    }
}
