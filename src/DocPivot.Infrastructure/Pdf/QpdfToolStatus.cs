namespace DocPivot.Infrastructure.Pdf;

public sealed record QpdfToolStatus(
    bool IsReady,
    string? ExecutablePath,
    string? Version,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable)
{
    public static QpdfToolStatus Ready(string executablePath, string version) =>
        new(true, executablePath, version, null, null, false);

    public static QpdfToolStatus Unavailable(
        string errorCode,
        string errorMessage,
        bool isRetryable = false) =>
        new(false, null, null, errorCode, errorMessage, isRetryable);
}
