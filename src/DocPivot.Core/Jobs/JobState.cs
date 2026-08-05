namespace DocPivot.Core.Jobs;

public enum JobState
{
    Queued,
    Validating,
    Running,
    ReviewRequired,
    Exporting,
    Succeeded,
    Failed,
    Canceled,
}

