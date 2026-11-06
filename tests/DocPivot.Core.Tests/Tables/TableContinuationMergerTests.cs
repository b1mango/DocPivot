using DocPivot.Core.Tables;

namespace DocPivot.Core.Tests.Tables;

public sealed class TableContinuationMergerTests
{
    [Fact]
    public void Merge_ThreeAdjacentPagesRemoveRepeatedHeadersAndRetainRowSpans()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta");
        var third = Page(3, "Gamma");
        var thirdTable = third.Tables[0];
        third = third with
        {
            SourceKind = DocumentTableSourceKind.OpticalCharacterRecognition,
            Tables = [thirdTable with
            {
                SourceKind = DocumentTableSourceKind.OpticalCharacterRecognition,
                Warnings = ["OCR_CONTENT_REQUIRES_REVIEW"],
            }],
        };

        var merged = Assert.Single(TableContinuationMerger.Merge([first, second, third]));

        Assert.Equal(4, merged.RowCount);
        Assert.Equal(1, merged.HeaderRowCount);
        Assert.Equal(1, merged.PageNumber);
        Assert.Equal(["Item", "Alpha", "Beta", "Gamma"], merged.Cells
            .Where(static cell => cell.ColumnIndex == 0).Select(static cell => cell.Text));
        Assert.Equal([0, 1, 2, 3], merged.Cells
            .Where(static cell => cell.ColumnIndex == 0).Select(static cell => cell.RowIndex));
        Assert.Equal(DocumentTableSourceKind.OpticalCharacterRecognition, merged.SourceKind);
        Assert.Contains("OCR_CONTENT_REQUIRES_REVIEW", merged.Warnings);
        Assert.Equal(2, second.Tables[0].RowCount);
    }

    [Fact]
    public void Merge_MultiRowHeadersAndMergedBodyKeepCorrectOffsets()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta");
        static DocumentTablePage AddHeaderBandAndBodySpan(DocumentTablePage page)
        {
            var table = page.Tables[0];
            var cells = table.Cells.Select(static cell => cell with
            {
                RowIndex = cell.RowIndex + 1,
                RowSpan = cell.RowIndex == 1 ? 2 : 1,
            }).Prepend(Cell(0, 0, "Summary") with { ColumnSpan = 2 }).ToArray();
            return page with { Tables = [table with { HeaderRowCount = 2, RowCount = 4, Cells = cells }] };
        }

        var merged = Assert.Single(TableContinuationMerger.Merge([
            AddHeaderBandAndBodySpan(first), AddHeaderBandAndBodySpan(second)]));

        Assert.Equal(6, merged.RowCount);
        Assert.Equal(2, merged.HeaderRowCount);
        var beta = Assert.Single(merged.Cells, static cell => cell.Text == "Beta");
        Assert.Equal(4, beta.RowIndex);
        Assert.Equal(2, beta.RowSpan);
        Assert.Single(merged.Cells, static cell => cell.Text == "Summary");
    }

    [Fact]
    public void Merge_DifferentHeadersDoNotJoinTables()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta", "Price");

        Assert.Equal(2, TableContinuationMerger.Merge([first, second]).Count);
    }

    [Fact]
    public void Merge_NoDetectedHeaderDoesNotDropFirstDataRow()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta");
        first = first with { Tables = [first.Tables[0] with { HeaderRowCount = 0 }] };
        second = second with { Tables = [second.Tables[0] with { HeaderRowCount = 0 }] };

        Assert.Equal(2, TableContinuationMerger.Merge([first, second]).Count);
    }

    [Fact]
    public void Merge_NonAdjacentOrEmptyPageBreaksContinuation()
    {
        var first = Page(1, "Alpha");
        var third = Page(3, "Gamma");
        var empty = Page(2, "Unused") with { Tables = [] };

        Assert.Equal(2, TableContinuationMerger.Merge([first, third]).Count);
        Assert.Equal(2, TableContinuationMerger.Merge([first, empty, third]).Count);
    }

    [Fact]
    public void Merge_ChangedColumnPositionDoesNotJoinTables()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta");
        second = second with
        {
            Tables = [second.Tables[0] with
            {
                Cells = second.Tables[0].Cells.Select(static cell => cell with
                {
                    Bounds = cell.Bounds with { Left = cell.Bounds.Left + 80, Right = cell.Bounds.Right + 80 },
                }).ToArray(),
            }],
        };

        Assert.Equal(2, TableContinuationMerger.Merge([first, second]).Count);
    }

    [Fact]
    public void Merge_OnlyLastAndFirstTablesAcrossPageBoundaryAreCandidates()
    {
        var first = Page(1, "Alpha");
        var second = Page(2, "Beta");
        var extra = first.Tables[0] with { TableIndex = 2 };
        first = first with { Tables = [first.Tables[0], extra] };

        var merged = TableContinuationMerger.Merge([first, second]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged[0].RowCount);
        Assert.Equal(3, merged[1].RowCount);
    }

    private static DocumentTablePage Page(int number, string item, string secondHeader = "Amount")
    {
        var table = new DocumentTable(
            number, 1, 2, 2, new DocumentBounds(20, 20, 300, 80),
            DocumentTableSourceKind.DigitalText, 1,
            [Cell(0, 0, "Item"), Cell(0, 1, secondHeader), Cell(1, 0, item), Cell(1, 1, "100")],
            [], HeaderRowCount: 1);
        return new DocumentTablePage(number, DocumentTableSourceKind.DigitalText, 600, 800, [table], []);
    }

    private static DocumentTableCell Cell(int row, int column, string text) =>
        new(row, column, 1, 1, text, DocumentCellValueClassifier.Classify(text), 1,
            new DocumentBounds(20 + column * 150, 20 + row * 30, 140 + column * 150, 40 + row * 30));
}
