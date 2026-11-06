using DocPivot.Core.Tables;

namespace DocPivot.Core.Tests.Tables;

public sealed class PdfPageTextQualityTests
{
    [Theory]
    [InlineData("1234567", true)]
    [InlineData("12345678", false)]
    [InlineData("abcdefghi?", true)]
    [InlineData("abcdefghij?", false)]
    [InlineData("abc\uFFFD\uE000\0defgh", true)]
    [InlineData("(cid:123)(cid:124)(cid:125)abcdefghi", true)]
    [InlineData("姓名金额合同日期税率", false)]
    public void Evaluate_UsesCharacterAndMappingThresholds(string text, bool requiresOcr)
    {
        var assessment = PdfPageTextQuality.Evaluate(600, 800,
            [new(text, new DocumentBounds(30, 30, 180, 42), 1)], []);

        Assert.Equal(requiresOcr, assessment.RequiresOcr);
    }

    [Fact]
    public void Evaluate_ScanWithLongDigitalHeaderStillRequiresOcr()
    {
        var assessment = PdfPageTextQuality.Evaluate(600, 800,
            [new(new string('A', 100), new DocumentBounds(30, 15, 570, 30), 1)],
            [new(20, 50, 580, 790)]);

        Assert.True(assessment.RequiresOcr);
        Assert.Equal("IMAGE_BODY_WITH_SPARSE_TEXT", assessment.Reason);
    }

    [Theory]
    [InlineData(439, false)]
    [InlineData(440, true)]
    public void Evaluate_UsesDominantImageThreshold(double imageHeight, bool requiresOcr)
    {
        var assessment = PdfPageTextQuality.Evaluate(600, 800,
            [new("Readable heading", new DocumentBounds(30, 10, 150, 25), 1)],
            [new(0, 100, 600, 100 + imageHeight)]);

        Assert.Equal(requiresOcr, assessment.RequiresOcr);
    }

    [Fact]
    public void Evaluate_SearchableScanWithDistributedTextKeepsDigitalLayer()
    {
        var words = Enumerable.Range(0, 20).Select(index =>
            new DocumentWord("Readable row content", new(40, 50 + index * 30, 400, 63 + index * 30), 1)).ToArray();
        var assessment = PdfPageTextQuality.Evaluate(600, 800, words, [new(0, 0, 600, 800)]);

        Assert.False(assessment.RequiresOcr);
        Assert.True(assessment.TextCoverageWithinImages > PdfPageTextQuality.MinimumImageTextCoverage);
    }

    [Fact]
    public void Evaluate_OverlappingLogosDoNotCountAsFullPageImage()
    {
        var assessment = PdfPageTextQuality.Evaluate(600, 800,
            [new("Readable heading", new DocumentBounds(30, 10, 150, 25), 1)],
            [new(0, 100, 600, 300), new(0, 100, 600, 300), new(0, 100, 600, 300)]);

        Assert.False(assessment.RequiresOcr);
        Assert.Equal(0.25, assessment.LargeImageCoverage);
    }

    [Fact]
    public void Evaluate_NoTextRequiresOcr()
    {
        Assert.True(PdfPageTextQuality.Evaluate(600, 800, [], [new(0, 0, 600, 800)]).RequiresOcr);
    }
}
