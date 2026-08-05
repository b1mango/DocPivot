namespace DocPivot.Core.Jobs;

public interface IJobHandler<in TCommand>
    where TCommand : notnull
{
    Task<JobOutcome> ExecuteAsync(
        TCommand command,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken);
}

