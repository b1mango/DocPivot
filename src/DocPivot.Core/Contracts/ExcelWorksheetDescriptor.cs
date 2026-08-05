namespace DocPivot.Core.Contracts;

public sealed record ExcelWorksheetDescriptor(
    string Name,
    int Position,
    string Visibility,
    bool IsExportable);
