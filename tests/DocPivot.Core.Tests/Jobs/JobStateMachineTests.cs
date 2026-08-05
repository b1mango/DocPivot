using DocPivot.Core.Jobs;

namespace DocPivot.Core.Tests.Jobs;

public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobState.Queued, JobState.Validating)]
    [InlineData(JobState.Validating, JobState.Running)]
    [InlineData(JobState.Running, JobState.ReviewRequired)]
    [InlineData(JobState.ReviewRequired, JobState.Exporting)]
    [InlineData(JobState.Exporting, JobState.Succeeded)]
    public void CanTransition_AllowsExpectedWorkflow(JobState current, JobState next)
    {
        Assert.True(JobStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(JobState.Succeeded, JobState.Running)]
    [InlineData(JobState.Failed, JobState.Validating)]
    [InlineData(JobState.Canceled, JobState.Queued)]
    public void CanTransition_RejectsTerminalStateTransitions(JobState current, JobState next)
    {
        Assert.False(JobStateMachine.CanTransition(current, next));
    }
}

