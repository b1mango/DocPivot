namespace DocPivot.Core.Documents;

public sealed record PdfFileInspection(bool IsValid, string? ErrorCode)
{
    public static PdfFileInspection Valid() => new(true, null);

    public static PdfFileInspection Invalid(string errorCode) => new(false, errorCode);
}
