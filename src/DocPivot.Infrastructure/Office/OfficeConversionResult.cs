namespace DocPivot.Infrastructure.Office;

public sealed record OfficeConversionResult(
    bool IsSucceeded,
    IReadOnlyList<string> Artifacts,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable)
{
    public static OfficeConversionResult Succeeded(IReadOnlyList<string> artifacts) =>
        new(true, artifacts, null, null, false);

    public static OfficeConversionResult Failed(
        string errorCode,
        string errorMessage,
        bool isRetryable) =>
        new(false, [], errorCode, errorMessage, isRetryable);
}
