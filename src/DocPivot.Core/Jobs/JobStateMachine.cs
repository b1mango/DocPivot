namespace DocPivot.Core.Jobs;

public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> AllowedTransitions =
        new Dictionary<JobState, JobState[]>
        {
            [JobState.Queued] = [JobState.Validating, JobState.Canceled],
            [JobState.Validating] = [JobState.Running, JobState.Failed, JobState.Canceled],
            [JobState.Running] =
                [JobState.ReviewRequired, JobState.Exporting, JobState.Succeeded, JobState.Failed, JobState.Canceled],
            [JobState.ReviewRequired] = [JobState.Exporting, JobState.Failed, JobState.Canceled],
            [JobState.Exporting] = [JobState.Succeeded, JobState.Failed, JobState.Canceled],
            [JobState.Succeeded] = [],
            [JobState.Failed] = [],
            [JobState.Canceled] = [],
        };

    public static bool CanTransition(JobState current, JobState next) =>
        AllowedTransitions[current].Contains(next);

    public static void EnsureTransition(JobState current, JobState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Invalid job state transition: {current} -> {next}.");
        }
    }
}
