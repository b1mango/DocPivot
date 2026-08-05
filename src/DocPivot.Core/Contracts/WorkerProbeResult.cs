namespace DocPivot.Core.Contracts;

public sealed record WorkerProbeResult(
    int V,
    string Type,
    string Worker,
    string Status,
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlyDictionary<string, string>? Metadata = null);
