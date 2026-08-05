using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Pdf;

public interface IPdfOperationsClient
{
    Task<PdfPreflightResult> PreflightAsync(
        PdfPreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> MergeAsync(
        PdfMergeRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> SplitAsync(
        PdfSplitRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> OptimizeAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken = default);
}
