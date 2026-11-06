namespace DocPivot.Infrastructure.Office;

public sealed record ExcelWorksheetPreview(string Name, int Position, string? ImagePath, int? PageCount);

public interface IExcelWorksheetPreviewService
{
    Task<IReadOnlyList<ExcelWorksheetPreview>> RenderPreviewsAsync(
        string workbookPath,
        string outputDirectory,
        int pixelWidth,
        CancellationToken cancellationToken);
}
