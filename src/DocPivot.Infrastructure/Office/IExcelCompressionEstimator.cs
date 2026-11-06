namespace DocPivot.Infrastructure.Office;

public interface IExcelCompressionEstimator
{
    Task<ExcelCompressionEstimate> EstimateAsync(
        string workbookPath,
        int strength,
        CancellationToken cancellationToken);
}
