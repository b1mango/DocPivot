using DocPivot.Core.Tables;

namespace DocPivot.Core.Tests.Tables;

public sealed class TableLayoutAnalyzerTests
{
    [Fact]
    public void AnalyzePage_AlignedGridProducesTypedTable()
    {
        var words = new[]
        {
            Word("Name", 10, 10), Word("Count", 110, 10), Word("Rate", 210, 10),
            Word("Alpha", 10, 32), Word("12", 110, 32), Word("25%", 210, 32),
            Word("Beta", 10, 54), Word("8", 110, 54), Word("12.5%", 210, 54),
        };

        var page = TableLayoutAnalyzer.AnalyzePage(
            1,
            300,
            200,
            DocumentTableSourceKind.DigitalText,
            words);

        var table = Assert.Single(page.Tables);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.True(table.Confidence > 0.9);
        Assert.Equal("Alpha", Cell(table, 1, 0).Text);
        Assert.Equal(DocumentCellValueKind.WholeNumber, Cell(table, 1, 1).ValueKind);
        Assert.Equal(DocumentCellValueKind.Percentage, Cell(table, 1, 2).ValueKind);
        Assert.Empty(table.Warnings);
    }

    [Fact]
    public void AnalyzePage_LargeVerticalGapSeparatesTables()
    {
        var words = new[]
        {
            Word("A", 10, 10), Word("B", 110, 10),
            Word("1", 10, 32), Word("2", 110, 32),
            Word("C", 10, 130), Word("D", 110, 130),
            Word("3", 10, 152), Word("4", 110, 152),
        };

        var page = TableLayoutAnalyzer.AnalyzePage(
            1,
            300,
            240,
            DocumentTableSourceKind.DigitalText,
            words);

        Assert.Collection(
            page.Tables,
            first => Assert.Equal("A", Cell(first, 0, 0).Text),
            second => Assert.Equal("C", Cell(second, 0, 0).Text));
    }

    [Fact]
    public void AnalyzePage_SparseFourColumnFormPreservesGrid()
    {
        var words = new[]
        {
            Word("Label", 10, 10), Word("Value", 110, 10), Word("Kind", 210, 10), Word("Date", 310, 10),
            Word("Department", 10, 100), Word("Operations", 110, 100), Word("Amount", 210, 100), Word("3330.00", 310, 100),
            Word("Supplier", 10, 190), Word("Testing", 110, 190), Word("Code", 210, 190), Word("MNFTJ", 310, 190),
        };

        var page = TableLayoutAnalyzer.AnalyzePage(
            1,
            400,
            260,
            DocumentTableSourceKind.DigitalText,
            words);

        var table = Assert.Single(page.Tables);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(4, table.ColumnCount);
        Assert.Equal("Operations", Cell(table, 1, 1).Text);
        Assert.Equal("3330.00", Cell(table, 1, 3).Text);
    }

    [Fact]
    public void AnalyzePage_ParagraphFallsBackToAuditableSingleColumn()
    {
        var words = new[]
        {
            Word("This", 10, 10), Word("is", 42, 10), Word("text", 58, 10),
            Word("without", 10, 32), Word("stable", 62, 32), Word("columns", 104, 32),
        };

        var page = TableLayoutAnalyzer.AnalyzePage(
            1,
            300,
            200,
            DocumentTableSourceKind.DigitalText,
            words);

        var table = Assert.Single(page.Tables);
        Assert.Equal(1, table.ColumnCount);
        Assert.True(table.Confidence < 0.5);
        Assert.Contains("TABLE_STRUCTURE_UNCERTAIN", table.Warnings);
        Assert.Contains("TABLE_STRUCTURE_UNCERTAIN", page.Warnings);
    }

    [Fact]
    public void AnalyzePage_OcrCellsCarryReviewAndLowConfidenceWarnings()
    {
        var words = new[]
        {
            Word("Item", 10, 10, 0.92), Word("Value", 110, 10, 0.91),
            Word("A", 10, 32, 0.65), Word("9", 110, 32, 0.7),
        };

        var page = TableLayoutAnalyzer.AnalyzePage(
            1,
            300,
            200,
            DocumentTableSourceKind.OpticalCharacterRecognition,
            words);

        var table = Assert.Single(page.Tables);
        Assert.Contains("OCR_CONTENT_REQUIRES_REVIEW", table.Warnings);
        Assert.Contains("LOW_CONFIDENCE_CELLS", table.Warnings);
        Assert.Contains("OCR_CONTENT_REQUIRES_REVIEW", page.Warnings);
    }

    [Theory]
    [InlineData("1,024", DocumentCellValueKind.WholeNumber)]
    [InlineData("-12.50", DocumentCellValueKind.FractionalNumber)]
    [InlineData("25%", DocumentCellValueKind.Percentage)]
    [InlineData("2026-08-04", DocumentCellValueKind.Date)]
    [InlineData("=2+2", DocumentCellValueKind.Text)]
    [InlineData("00123", DocumentCellValueKind.Text)]
    public void Classify_PreservesAmbiguousAndFormulaLikeText(
        string input,
        DocumentCellValueKind expected)
    {
        Assert.Equal(expected, DocumentCellValueClassifier.Classify(input));
    }

    private static DocumentWord Word(string text, double left, double top, double confidence = 1) =>
        new(text, new DocumentBounds(left, top, left + Math.Max(8, text.Length * 8), top + 12), confidence);

    private static DocumentTableCell Cell(DocumentTable table, int row, int column) =>
        Assert.Single(table.Cells, cell => cell.RowIndex == row && cell.ColumnIndex == column);
}
