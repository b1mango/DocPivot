using DocPivot.Core.Jobs;

namespace DocPivot.Core.Tests.Jobs;

public sealed class JobProgressTests
{
    [Fact]
    public void Create_ComputesFraction()
    {
        var progress = JobProgress.Create("rendering", 3, 4);

        Assert.Equal("rendering", progress.Stage);
        Assert.Equal(0.75, progress.Fraction);
    }

    [Fact]
    public void Create_RejectsCurrentGreaterThanTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobProgress.Create("rendering", 5, 4));
    }

    [Fact]
    public void Create_AllowsIndeterminateStartingValue()
    {
        var progress = JobProgress.Create("starting", 0, 0);

        Assert.Equal(0, progress.Fraction);
    }
}

