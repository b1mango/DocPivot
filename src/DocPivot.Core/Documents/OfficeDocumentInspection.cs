namespace DocPivot.Core.Documents;

public sealed record OfficeDocumentInspection(
    bool IsValid,
    OfficeDocumentKind? Kind,
    string? ErrorCode)
{
    public static OfficeDocumentInspection Valid(OfficeDocumentKind kind) => new(true, kind, null);

    public static OfficeDocumentInspection Invalid(string errorCode) => new(false, null, errorCode);
}
