using DocPivot.Infrastructure.Pdf;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class PdfSplitPlannerTests
{
    [Fact]
    public void Create_CustomRangeUsesCoreParserAndExactDestination()
    {
        var output = Path.GetFullPath(Path.Combine("output", "selected.pdf"));

        var plan = PdfSplitPlanner.Create(
            output,
            PdfSplitSelection.CustomRange("3, 1-2, 2"),
            totalPages: 5);

        var artifact = Assert.Single(plan.Artifacts);
        Assert.False(plan.UsesNativeSplit);
        Assert.Equal(output, artifact.DestinationPath);
        Assert.Equal("3,1-2", artifact.PageExpression);
        Assert.Equal(3, artifact.ExpectedPageCount);
    }

    [Fact]
    public void Create_EveryNPagesBuildsPaddedOrderedPartitions()
    {
        var output = Path.GetFullPath(Path.Combine("output", "report.pdf"));

        var plan = PdfSplitPlanner.Create(
            output,
            PdfSplitSelection.EveryNPages(5),
            totalPages: 12);

        Assert.True(plan.UsesNativeSplit);
        Assert.Equal(5, plan.NativePagesPerFile);
        Assert.Collection(
            plan.Artifacts,
            first => AssertArtifact(first, "report-01-05.pdf", "1-5", 5, "01-05"),
            second => AssertArtifact(second, "report-06-10.pdf", "6-10", 5, "06-10"),
            third => AssertArtifact(third, "report-11-12.pdf", "11-12", 2, "11-12"));
    }

    [Fact]
    public void Create_EveryPageUsesNativeOnePagePartitions()
    {
        var plan = PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "page.pdf")),
            PdfSplitSelection.EveryPage(),
            totalPages: 3);

        Assert.True(plan.UsesNativeSplit);
        Assert.Equal(1, plan.NativePagesPerFile);
        Assert.Equal(["1", "2", "3"], plan.Artifacts.Select(static item => item.PageExpression));
        Assert.All(plan.Artifacts, static item => Assert.Equal(1, item.ExpectedPageCount));
    }

    [Fact]
    public void Create_VisualCutsBuildsOrderedIndependentPartitions()
    {
        var plan = PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "report.pdf")),
            PdfSplitSelection.VisualCuts([5, 2, 5]),
            totalPages: 8);

        Assert.False(plan.UsesNativeSplit);
        Assert.Collection(
            plan.Artifacts,
            first => AssertArtifact(first, "report-1-2.pdf", "1-2", 2, "1-2"),
            second => AssertArtifact(second, "report-3-5.pdf", "3-5", 3, "3-5"),
            third => AssertArtifact(third, "report-6-8.pdf", "6-8", 3, "6-8"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Create_VisualCutsRejectsPositionsOutsidePageGaps(int cutAfterPage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "report.pdf")),
            PdfSplitSelection.VisualCuts([cutAfterPage]),
            totalPages: 8));
    }

    [Theory]
    [InlineData(PdfSplitMode.OddPages, "1,3,5", 3)]
    [InlineData(PdfSplitMode.EvenPages, "2,4", 2)]
    public void Create_OddEvenBuildsOneStructuredSelection(
        PdfSplitMode mode,
        string expectedExpression,
        int expectedCount)
    {
        var selection = new PdfSplitSelection(mode);

        var plan = PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "selected.pdf")),
            selection,
            totalPages: 5);

        var artifact = Assert.Single(plan.Artifacts);
        Assert.False(plan.UsesNativeSplit);
        Assert.Equal(expectedExpression, artifact.PageExpression);
        Assert.Equal(expectedCount, artifact.ExpectedPageCount);
    }

    [Fact]
    public void Create_EvenPagesRejectsSinglePageInput()
    {
        Assert.Throws<InvalidOperationException>(() => PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "even.pdf")),
            PdfSplitSelection.EvenPages(),
            totalPages: 1));
    }

    [Fact]
    public void Create_EveryNPagesRejectsNonPositiveGroupSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfSplitPlanner.Create(
            Path.GetFullPath(Path.Combine("output", "group.pdf")),
            PdfSplitSelection.EveryNPages(0),
            totalPages: 10));
    }

    private static void AssertArtifact(
        PdfSplitArtifactPlan artifact,
        string expectedFileName,
        string expectedExpression,
        int expectedPageCount,
        string expectedRangeLabel)
    {
        Assert.Equal(expectedFileName, Path.GetFileName(artifact.DestinationPath));
        Assert.Equal(expectedExpression, artifact.PageExpression);
        Assert.Equal(expectedPageCount, artifact.ExpectedPageCount);
        Assert.Equal(expectedRangeLabel, artifact.RangeLabel);
    }
}
