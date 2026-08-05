using DocPivot.Core.Pdf;

namespace DocPivot.Core.Tests.Pdf;

public sealed class PdfPageRangeParserTests
{
    [Fact]
    public void Parse_ExpandsClosedSingleAndOpenEndedRanges()
    {
        var result = PdfPageRangeParser.Parse("1-3,5,8-", 10);

        Assert.Equal([1, 2, 3, 5, 8, 9, 10], result.Pages);
        Assert.Equal("1-3,5,8-10", result.NormalizedExpression);
        Assert.Equal(result.NormalizedExpression, result.ToString());
    }

    [Fact]
    public void Parse_DeduplicatesWhilePreservingFirstInputOrder()
    {
        var result = PdfPageRangeParser.Parse("3,1-2,2-4,6,5", 6);

        Assert.Equal([3, 1, 2, 4, 6, 5], result.Pages);
        Assert.Equal("3,1-2,4,6,5", result.NormalizedExpression);
    }

    [Fact]
    public void Parse_AllowsWhitespaceAndNormalizesSinglePageRange()
    {
        var result = PdfPageRangeParser.Parse(" 2 - 2 , 4 - ", 5);

        Assert.Equal([2, 4, 5], result.Pages);
        Assert.Equal("2,4-5", result.NormalizedExpression);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_RejectsEmptyExpression(string expression)
    {
        Assert.Throws<ArgumentException>(() => PdfPageRangeParser.Parse(expression, 10));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("3-2")]
    [InlineData("1--3")]
    [InlineData("1,,2")]
    [InlineData("1-2-3")]
    public void Parse_RejectsInvalidSyntax(string expression)
    {
        Assert.Throws<FormatException>(() => PdfPageRangeParser.Parse(expression, 10));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("8-11")]
    public void Parse_RejectsPagesOutsideDocument(string expression)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRangeParser.Parse(expression, 10));
    }

    [Fact]
    public void Parse_RejectsNonPositiveTotalPageCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRangeParser.Parse("1", 0));
    }
}
