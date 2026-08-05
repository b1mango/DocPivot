namespace DocPivot.Core.Jobs;

public sealed record JobOutcome(
    JobState State,
    IReadOnlyList<string> Artifacts,
    string? ErrorCode)
{
    public static JobOutcome Succeeded(IReadOnlyList<string> artifacts) =>
        new(JobState.Succeeded, artifacts, null);

    public static JobOutcome Failed(string errorCode) =>
        new(JobState.Failed, [], errorCode);

    public static JobOutcome Canceled() =>
        new(JobState.Canceled, [], null);
}

