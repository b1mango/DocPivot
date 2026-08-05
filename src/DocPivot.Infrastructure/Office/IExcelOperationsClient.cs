using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Office;

public interface IExcelOperationsClient
{
    Task<OperationExecutionResult> ExecuteAsync(
        ExcelOperationRequest request,
        IProgress<WorkerProgressMessage>? progress = null,
        CancellationToken cancellationToken = default);
}
