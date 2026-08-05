using System.Text.Json.Serialization;

namespace DocPivot.Core.Contracts;

public sealed record WorkerResultMessage(
    int V,
    string Type,
    Guid JobId,
    string Status,
    IReadOnlyList<string> Artifacts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WorkerNotice>? Notices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Metrics = null)
{
    public static WorkerResultMessage Succeeded(
        Guid jobId,
        IReadOnlyList<string> artifacts,
        IReadOnlyList<WorkerNotice>? notices = null,
        IReadOnlyDictionary<string, string>? metrics = null) =>
        new(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.Result,
            jobId,
            "succeeded",
            artifacts,
            notices,
            metrics);
}
