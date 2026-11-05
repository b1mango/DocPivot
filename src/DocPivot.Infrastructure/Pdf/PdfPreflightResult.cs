namespace DocPivot.Infrastructure.Pdf;

public sealed record PdfPreflightResult(
    bool IsSucceeded,
    string? InputPath,
    int? PageCount,
    bool IsEncrypted,
    bool HasSignatureFields,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable)
{
    public bool HasStructureWarnings { get; init; }

    public static PdfPreflightResult Succeeded(
        string inputPath,
        int pageCount,
        bool hasSignatureFields,
        bool isEncrypted = false,
        bool hasStructureWarnings = false) =>
        new(true, inputPath, pageCount, isEncrypted, hasSignatureFields, null, null, false)
        {
            HasStructureWarnings = hasStructureWarnings,
        };

    public static PdfPreflightResult Failed(
        string? inputPath,
        string errorCode,
        string errorMessage,
        bool isRetryable,
        bool isEncrypted = false) =>
        new(false, inputPath, null, isEncrypted, false, errorCode, errorMessage, isRetryable);
}
