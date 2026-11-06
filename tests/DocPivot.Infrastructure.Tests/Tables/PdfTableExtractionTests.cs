using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;
using DocPivot.PdfWorker;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocPivot.Infrastructure.Tests.Tables;

public sealed class PdfTableExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", "Tables", Guid.NewGuid().ToString("N"));

    public PdfTableExtractionTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ExtractAsync_RuledPdfPreservesGridAndTypedValues()
    {
        using var builder = new PdfDocumentBuilder();
        AddStandardTable(builder, "Alpha", "1,234.50", "12.5%", ruled: true);

        var extraction = await ExtractAsync(builder);

        var table = Assert.Single(extraction.Tables);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal("Alpha", Cell(table, 1, 0).Text);
        Assert.Equal(DocumentCellValueKind.FractionalNumber, Cell(table, 1, 1).ValueKind);
        Assert.Equal(DocumentCellValueKind.Percentage, Cell(table, 1, 2).ValueKind);
        Assert.Equal(1, extraction.DigitalPageCount);
        Assert.Equal(0, extraction.OcrPageCount);
    }

    [Fact]
    public async Task ExtractAsync_BorderlessPdfKeepsRightAlignedNumbersInTheirColumns()
    {
        using var builder = new PdfDocumentBuilder();
        AddStandardTable(builder, "Alpha", "1,234.50", "12.5%", ruled: false);

        var table = Assert.Single((await ExtractAsync(builder)).Tables);

        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal("1,234.50", Cell(table, 1, 1).Text);
        Assert.Equal("8.00", Cell(table, 2, 1).Text);
        Assert.Equal("5%", Cell(table, 2, 2).Text);
    }

    [Fact]
    public async Task ExtractAsync_RuledPdfPreservesHorizontalMergeAndMultilineCell()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(600, 800);
        double[] xs = [40, 220, 400, 560];
        double[] ys = [740, 710, 680, 630, 600];
        foreach (var y in ys)
        {
            page.DrawLine(new PdfPoint(xs[0], y), new PdfPoint(xs[^1], y));
        }

        page.DrawLine(new PdfPoint(xs[0], ys[0]), new PdfPoint(xs[0], ys[^1]));
        page.DrawLine(new PdfPoint(xs[^1], ys[0]), new PdfPoint(xs[^1], ys[^1]));
        foreach (var x in xs.Skip(1).SkipLast(1))
        {
            page.DrawLine(new PdfPoint(x, ys[1]), new PdfPoint(x, ys[^1]));
        }

        Text(page, font, "Quarterly totals", 48, 722);
        Text(page, font, "Name", 48, 692);
        Text(page, font, "Amount", 228, 692);
        Text(page, font, "Rate", 408, 692);
        Text(page, font, "Alpha", 48, 660);
        Text(page, font, "second line", 48, 646);
        Text(page, font, "12.50", 228, 660);
        Text(page, font, "25%", 408, 660);
        Text(page, font, "Beta", 48, 612);
        Text(page, font, "8.00", 228, 612);
        Text(page, font, "10%", 408, 612);

        var extraction = await ExtractAsync(builder);
        var table = Assert.Single(extraction.Tables);
        Assert.Equal(3, Cell(table, 0, 0).ColumnSpan);
        Assert.Equal("Alpha\nsecond line", Cell(table, 2, 0).Text);
        var workbookPath = Path.Combine(_root, "merged.xlsx");
        OpenXmlTableWorkbookWriter.Write(workbookPath, extraction, WorkerPdfWorksheetMode.OneWorksheetPerTable);
        OpenXmlTableWorkbookWriter.Validate(workbookPath, 1);
        using var workbook = SpreadsheetDocument.Open(workbookPath, false);
        var worksheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!;
        Assert.Contains(worksheet.Descendants<MergeCell>(), cell => cell.Reference?.Value == "A1:C1");
        Assert.Equal("Alpha\nsecond line", WorksheetCell(worksheet, "A3").InnerText);
    }

    [Fact]
    public async Task ExtractAsync_RuledPdfPreservesVerticalMerge()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(600, 800);
        foreach (var x in new[] { 40, 220, 400 })
        {
            page.DrawLine(new PdfPoint(x, 740), new PdfPoint(x, 650));
        }

        foreach (var y in new[] { 740, 710, 650 })
        {
            page.DrawLine(new PdfPoint(40, y), new PdfPoint(400, y));
        }

        page.DrawLine(new PdfPoint(220, 680), new PdfPoint(400, 680));
        Text(page, font, "Group", 48, 722);
        Text(page, font, "Amount", 228, 722);
        Text(page, font, "Alpha", 48, 692);
        Text(page, font, "12", 228, 692);
        Text(page, font, "8", 228, 662);

        var extraction = await ExtractAsync(builder);
        var table = Assert.Single(extraction.Tables);
        Assert.Equal(2, Cell(table, 1, 0).RowSpan);
        var workbookPath = Path.Combine(_root, "vertical.xlsx");
        OpenXmlTableWorkbookWriter.Write(workbookPath, extraction, WorkerPdfWorksheetMode.OneWorksheetPerTable);
        OpenXmlTableWorkbookWriter.Validate(workbookPath, 1);
        using var workbook = SpreadsheetDocument.Open(workbookPath, false);
        Assert.Contains(Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<MergeCell>(),
            cell => cell.Reference?.Value == "A2:A3");
    }

    [Theory]
    [InlineData(WorkerPdfWorksheetMode.OneWorksheetPerTable, 1, 1)]
    [InlineData(WorkerPdfWorksheetMode.OneWorksheetPerDocument, 1, 1)]
    [InlineData(WorkerPdfWorksheetMode.OneWorksheetPerPage, 2, 2)]
    public async Task Write_ContinuationDeduplicatesHeaderExceptInPageMode(
        WorkerPdfWorksheetMode worksheetMode,
        int expectedSheetCount,
        int expectedHeaderCount)
    {
        using var builder = new PdfDocumentBuilder();
        AddStandardTable(builder, "Alpha", "12.50", "25%", ruled: true);
        AddStandardTable(builder, "Gamma", "7.50", "10%", ruled: true);
        var extraction = await ExtractAsync(builder);
        Assert.Equal(2, extraction.Pages.Count);
        Assert.Equal(2, extraction.Tables.Count);
        var workbookPath = Path.Combine(_root, "continued.xlsx");

        OpenXmlTableWorkbookWriter.Write(workbookPath, extraction, worksheetMode);
        Assert.Equal(expectedSheetCount, OpenXmlTableWorkbookWriter.GetTableWorksheetCount(extraction, worksheetMode));
        OpenXmlTableWorkbookWriter.Validate(workbookPath, expectedSheetCount);

        using var workbook = SpreadsheetDocument.Open(workbookPath, false);
        var worksheets = workbook.WorkbookPart!.WorksheetParts.Select(part => part.Worksheet!).ToArray();
        Assert.Equal(expectedSheetCount, worksheets.Length);
        Assert.Equal(expectedHeaderCount, worksheets.Sum(worksheet => worksheet.Descendants<Cell>().Count(cell => cell.InnerText == "Name")));
        Assert.Equal(4 + expectedHeaderCount, worksheets.Sum(worksheet => worksheet.Descendants<Row>().Count()));
    }

    [Fact]
    public async Task ExtractAsync_ThinFilledRectanglesActAsTableRules()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(600, 800);
        double[] xs = [40, 220, 400, 560];
        double[] ys = [740, 710, 680, 650];
        foreach (var x in xs)
        {
            page.DrawRectangle(new PdfPoint(x - 0.4, ys[^1]), 0.8, ys[0] - ys[^1], 0, fill: true);
        }

        foreach (var y in ys)
        {
            page.DrawRectangle(new PdfPoint(xs[0], y - 0.4), xs[^1] - xs[0], 0.8, 0, fill: true);
        }

        Text(page, font, "Name", 48, 722);
        Text(page, font, "Amount", 228, 722);
        Text(page, font, "Rate", 408, 722);
        Text(page, font, "Alpha", 48, 692);
        Text(page, font, "12.50", 228, 692);
        Text(page, font, "25%", 408, 692);
        Text(page, font, "Beta", 48, 662);
        Text(page, font, "8.00", 228, 662);
        Text(page, font, "5%", 408, 662);

        var table = Assert.Single((await ExtractAsync(builder)).Tables);

        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.All(table.Cells, static cell => Assert.Equal(1, cell.ColumnSpan * cell.RowSpan));
        Assert.Equal("12.50", Cell(table, 1, 1).Text);
    }

    [Fact]
    public async Task Write_PreservesIdentifiersAndWritesNativeNumbersDatesAndWidths()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(800, 800);
        double[] xs = [20, 150, 280, 430, 540, 690];
        double[] ys = [740, 710, 680];
        DrawGrid(page, xs, ys);
        string[] headers = ["Code", "Contract No", "Amount", "Rate", "Date"];
        string[] values = ["001234", "123456789", "1,234.50", "12.5%", "2026-08-04"];
        for (var column = 0; column < headers.Length; column++)
        {
            Text(page, font, headers[column], xs[column] + 8, 722);
            Text(page, font, values[column], xs[column] + 8, 692);
        }

        var extraction = await ExtractAsync(builder);
        var workbookPath = Path.Combine(_root, "values.xlsx");
        OpenXmlTableWorkbookWriter.Write(workbookPath, extraction, WorkerPdfWorksheetMode.OneWorksheetPerTable);
        OpenXmlTableWorkbookWriter.Validate(workbookPath, 1);
        using var workbook = SpreadsheetDocument.Open(workbookPath, false);
        var worksheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!;
        Assert.Equal(CellValues.InlineString, WorksheetCell(worksheet, "A2").DataType!.Value);
        Assert.Equal("001234", WorksheetCell(worksheet, "A2").InnerText);
        Assert.Equal(CellValues.InlineString, WorksheetCell(worksheet, "B2").DataType!.Value);
        Assert.Equal("123456789", WorksheetCell(worksheet, "B2").InnerText);
        Assert.Equal(CellValues.Number, WorksheetCell(worksheet, "C2").DataType!.Value);
        Assert.Equal("1234.50", WorksheetCell(worksheet, "C2").CellValue!.Text);
        Assert.Equal(CellValues.Number, WorksheetCell(worksheet, "D2").DataType!.Value);
        Assert.Equal("0.125", WorksheetCell(worksheet, "D2").CellValue!.Text);
        Assert.Equal(CellValues.Number, WorksheetCell(worksheet, "E2").DataType!.Value);
        Assert.Equal(new DateTime(2026, 8, 4).ToOADate(), double.Parse(WorksheetCell(worksheet, "E2").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture));
        var columns = worksheet.GetFirstChild<Columns>()!.Elements<Column>().ToArray();
        Assert.True(columns[1].Width!.Value > columns[0].Width!.Value);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private async Task<DocumentTableExtraction> ExtractAsync(PdfDocumentBuilder builder)
    {
        var path = Path.Combine(_root, "synthetic.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        var extractor = new PdfTableExtractor(
            new GhostscriptPageRenderer(_root),
            _ => throw new InvalidOperationException("Synthetic digital PDFs must not invoke OCR."));
        return await extractor.ExtractAsync(path, _root, new WorkerPdfToExcelOptions(
            WorkerPdfOcrMode.Automatic,
            WorkerPdfWorksheetMode.OneWorksheetPerTable));
    }

    private static void AddStandardTable(PdfDocumentBuilder builder, string name, string amount, string rate, bool ruled)
    {
        var font = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(600, 800);
        double[] xs = [40, 220, 400, 560];
        if (ruled)
        {
            DrawGrid(page, xs, [740, 710, 680, 650]);
        }

        Text(page, font, "Name", 48, 722);
        Text(page, font, "Amount", 348, 722);
        Text(page, font, "Rate", 528, 722);
        Text(page, font, name, 48, 692);
        Text(page, font, amount, 384 - (amount.Length * 6), 692);
        Text(page, font, rate, 552 - (rate.Length * 6), 692);
        Text(page, font, "Beta", 48, 662);
        Text(page, font, "8.00", 360, 662);
        Text(page, font, "5%", 540, 662);
    }

    private static void DrawGrid(PdfPageBuilder page, double[] xs, double[] ys)
    {
        foreach (var x in xs)
        {
            page.DrawLine(new PdfPoint(x, ys[0]), new PdfPoint(x, ys[^1]));
        }

        foreach (var y in ys)
        {
            page.DrawLine(new PdfPoint(xs[0], y), new PdfPoint(xs[^1], y));
        }
    }

    private static void Text(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, string text, double x, double y) =>
        page.AddText(text, 10, new PdfPoint(x, y), font);

    private static DocumentTableCell Cell(DocumentTable table, int row, int column) =>
        Assert.Single(table.Cells, cell => cell.RowIndex == row && cell.ColumnIndex == column);

    private static Cell WorksheetCell(Worksheet worksheet, string reference) =>
        Assert.Single(worksheet.Descendants<Cell>(), cell => cell.CellReference?.Value == reference);
}
