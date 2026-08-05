namespace DocPivot.Infrastructure.Office;

public sealed record OfficeEngineStatus(
    bool IsReady,
    string? Product,
    string? Version,
    string? Platform,
    string? ErrorMessage)
{
    public bool IsWordReady { get; init; } = IsReady;

    public bool IsExcelReady { get; init; } = IsReady;
}
