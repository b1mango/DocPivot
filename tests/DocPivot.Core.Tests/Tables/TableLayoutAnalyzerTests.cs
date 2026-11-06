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

    [Fact]
    public void AnalyzePage_RuledGridPreservesMergedCellsAndMultilineText()
    {
        var words = new[]
        {
            Word("Summary", 35, 14), Word("Amount", 212, 14),
            Word("Item", 12, 44), Word("Description", 105, 44), Word("Value", 212, 44),
            Word("Alpha", 12, 74), Word("first", 105, 74), Word("second", 105, 90), Word("12", 212, 74),
            Word("Beta", 12, 124), Word("other", 105, 124), Word("14", 212, 124),
        };
        var lines = new[]
        {
            new DocumentLine(5, 5, 300, 5), new DocumentLine(5, 35, 300, 35),
            new DocumentLine(5, 65, 300, 65), new DocumentLine(5, 115, 300, 115),
            new DocumentLine(5, 150, 300, 150),
            new DocumentLine(5, 5, 5, 150), new DocumentLine(100, 35, 100, 150),
            new DocumentLine(205, 5, 205, 150), new DocumentLine(300, 5, 300, 150),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 320, 200, DocumentTableSourceKind.DigitalText, words, lines).Tables);

        Assert.Equal(4, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(2, Cell(table, 0, 0).ColumnSpan);
        Assert.DoesNotContain(table.Cells, cell => cell.RowIndex == 0 && cell.ColumnIndex == 1);
        Assert.Equal("first\nsecond", Cell(table, 2, 1).Text);
        Assert.Equal(2, table.HeaderRowCount);
    }

    [Fact]
    public void AnalyzePage_RuledGridPreservesVerticalMergeAndEmptyCell()
    {
        var words = new[]
        {
            Word("Name", 15, 15), Word("Count", 115, 15),
            Word("Alpha", 15, 65), Word("12", 115, 55),
        };
        var lines = new[]
        {
            new DocumentLine(5, 5, 205, 5), new DocumentLine(5, 45, 205, 45),
            new DocumentLine(105, 85, 205, 85), new DocumentLine(5, 125, 205, 125),
            new DocumentLine(5, 5, 5, 125), new DocumentLine(105, 5, 105, 125),
            new DocumentLine(205, 5, 205, 125),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 240, 160, DocumentTableSourceKind.DigitalText, words, lines).Tables);

        Assert.Equal(2, Cell(table, 1, 0).RowSpan);
        Assert.Equal(string.Empty, Cell(table, 2, 1).Text);
        Assert.DoesNotContain(table.Cells, cell => cell.RowIndex == 2 && cell.ColumnIndex == 0);
    }

    [Fact]
    public void AnalyzePage_BorderlessMixedAlignmentPreservesColumns()
    {
        var words = new[]
        {
            Word("Description", 10, 10), Word("Amount", 220, 10),
            Word("A", 10, 35), Word("1", 260, 35),
            Word("Long product name", 10, 60), Word("1,234.50", 204, 60),
            Word("Item", 10, 85), Word("20.00", 228, 85),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 310, 140, DocumentTableSourceKind.DigitalText, words).Tables);

        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(4, table.RowCount);
        Assert.Equal("Long product name", Cell(table, 2, 0).Text);
        Assert.Equal("1,234.50", Cell(table, 2, 1).Text);
        Assert.Equal(1, table.HeaderRowCount);
    }

    [Fact]
    public void AnalyzePage_BorderlessWrappedCellMergesContinuationLine()
    {
        var words = new[]
        {
            Word("Description", 10, 10), Word("Amount", 210, 10),
            Word("First line", 10, 40), Word("12", 210, 40),
            Word("continued", 10, 56),
            Word("Next item", 10, 90), Word("24", 210, 90),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 300, 150, DocumentTableSourceKind.DigitalText, words).Tables);

        Assert.Equal(3, table.RowCount);
        Assert.Equal("First line\ncontinued", Cell(table, 1, 0).Text);
    }

    [Fact]
    public void AnalyzePage_IdentifierHeaderPreservesNumericCode()
    {
        var words = new[]
        {
            Word("Contract No", 10, 10), Word("Amount", 210, 10),
            Word("12345", 10, 35), Word("120", 210, 35),
            Word("23456", 10, 60), Word("240", 210, 60),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 300, 120, DocumentTableSourceKind.DigitalText, words).Tables);

        Assert.Equal(DocumentCellValueKind.Text, Cell(table, 1, 0).ValueKind);
        Assert.Equal(DocumentCellValueKind.WholeNumber, Cell(table, 1, 1).ValueKind);
    }

    [Fact]
    public void AnalyzePage_TallWordDoesNotBridgeAdjacentRows()
    {
        var words = new[]
        {
            Word("Name", 10, 10), Word("Amount", 210, 10),
            Word("First", 10, 35),
            new DocumentWord("12", new DocumentBounds(210, 25, 226, 51), 1),
            Word("Second", 10, 52), Word("24", 210, 52),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 300, 120, DocumentTableSourceKind.DigitalText, words).Tables);

        Assert.Equal(3, table.RowCount);
        Assert.Equal("Second", Cell(table, 2, 0).Text);
        Assert.Equal("24", Cell(table, 2, 1).Text);
    }

    [Fact]
    public void AnalyzePage_PureNumericTableDoesNotInventHeader()
    {
        var words = new[]
        {
            Word("12", 10, 10), Word("24", 110, 10),
            Word("36", 10, 35), Word("48", 110, 35),
        };

        var table = Assert.Single(TableLayoutAnalyzer.AnalyzePage(
            1, 220, 100, DocumentTableSourceKind.DigitalText, words).Tables);

        Assert.Equal(0, table.HeaderRowCount);
        Assert.Equal(DocumentCellValueKind.WholeNumber, Cell(table, 0, 0).ValueKind);
    }

    private static DocumentWord Word(string text, double left, double top, double confidence = 1) =>
        new(text, new DocumentBounds(left, top, left + Math.Max(8, text.Length * 8), top + 12), confidence);

    private static DocumentTableCell Cell(DocumentTable table, int row, int column) =>
        Assert.Single(table.Cells, cell => cell.RowIndex == row && cell.ColumnIndex == column);
}
