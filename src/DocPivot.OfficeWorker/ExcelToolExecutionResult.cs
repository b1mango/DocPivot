using DocPivot.Core.Contracts;

namespace DocPivot.OfficeWorker;

internal sealed record ExcelToolExecutionResult(
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<WorkerNotice> Notices,
    IReadOnlyDictionary<string, string> Metrics);
