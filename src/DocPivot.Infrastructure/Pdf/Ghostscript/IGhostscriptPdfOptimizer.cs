using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Pdf.Ghostscript;

public interface IGhostscriptPdfOptimizer
{
    Task<GhostscriptEngineStatus> ProbeAsync(CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> OptimizeAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GhostscriptEngineStatus(
    bool IsReady,
    string? ExecutablePath,
    string? Version,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable)
{
    public static GhostscriptEngineStatus Ready(string executablePath, string version) =>
        new(true, executablePath, version, null, null, false);

    public static GhostscriptEngineStatus Unavailable(
        string errorCode,
        string errorMessage,
        bool isRetryable = false) =>
        new(false, null, null, errorCode, errorMessage, isRetryable);
}
