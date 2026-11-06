namespace DocPivot.Infrastructure.Pdf;

public interface IPdfCompressionEstimator
{
    Task<PdfCompressionEstimate> EstimateAsync(
        string pdfPath,
        int strength,
        string? password,
        CancellationToken cancellationToken);
}

public sealed record PdfCompressionEstimate(long OriginalBytes, long EstimatedBytes, bool IsExact);
