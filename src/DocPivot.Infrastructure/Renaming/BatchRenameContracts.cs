using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Renaming;

public sealed record BatchRenameExecutionItem(
    string SourcePath,
    string TargetPath,
    long ExpectedSizeBytes,
    DateTime ExpectedLastWriteTimeUtc);

public sealed record BatchRenameExecutionRequest(
    IReadOnlyList<BatchRenameExecutionItem> Items);

public interface IBatchRenameExecutor
{
    Task<OperationExecutionResult> ExecuteAsync(
        BatchRenameExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationExecutionResult> UndoAsync(
        string manifestPath,
        CancellationToken cancellationToken = default);
}
