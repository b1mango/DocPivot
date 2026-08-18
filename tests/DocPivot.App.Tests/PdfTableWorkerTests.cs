using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;
using DocPivot.Infrastructure.Tables;
using DocPivot.PdfWorker;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocPivot.App.Tests;

public sealed class PdfTableWorkerTests
{
    [Fact]
    public void WorkbookWriter_WritesTypedCellsAndLowConfidenceStyle()
    {
        using var workspace = new TemporaryDirectory();
        var outputPath = Path.Combine(workspace.Path, "typed.xlsx");
        var table = new DocumentTable(
            1,
            1,
            3,
            4,
            new DocumentBounds(0, 0, 400, 100),
            DocumentTableSourceKind.DigitalText,
            0.91,
            [
                Cell(0, 0, "Item", DocumentCellValueKind.Text),
                Cell(0, 1, "Count", DocumentCellValueKind.Text),
                Cell(0, 2, "Rate", DocumentCellValueKind.Text),
                Cell(0, 3, "Date", DocumentCellValueKind.Text),
                Cell(1, 0, "Alpha", DocumentCellValueKind.Text),
                Cell(1, 1, "42", DocumentCellValueKind.WholeNumber),
                Cell(1, 2, "12.5%", DocumentCellValueKind.Percentage),
                Cell(1, 3, "2026-08-04", DocumentCellValueKind.Date),
                Cell(2, 0, "007", DocumentCellValueKind.Text, confidence: 0.4),
                Cell(2, 1, "-3.5", DocumentCellValueKind.FractionalNumber),
            ],
            ["LOW_CONFIDENCE_CELLS"]);
        var extraction = new DocumentTableExtraction(
            "typed.pdf",
            1,
            [new DocumentTablePage(
                1,
                DocumentTableSourceKind.DigitalText,
                500,
                700,
                [table],
                [])]);

        OpenXmlTableWorkbookWriter.Write(
            outputPath,
            extraction,
            WorkerPdfWorksheetMode.OneWorksheetPerTable);
        OpenXmlTableWorkbookWriter.Validate(outputPath, expectedTableSheetCount: 1);

        using var document = SpreadsheetDocument.Open(outputPath, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
        var sheets = Assert.IsType<Sheets>(workbook.Sheets);
        var sheet = Assert.IsType<Sheet>(sheets.Elements<Sheet>().First());
        var relationshipId = Assert.IsType<string>(sheet.Id?.Value);
        var worksheetPart = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(relationshipId));
        var worksheet = Assert.IsType<Worksheet>(worksheetPart.Worksheet);
        var stylesheet = Assert.IsType<Stylesheet>(workbookPart.WorkbookStylesPart?.Stylesheet);
        var tableBorder = Assert.Single(
            stylesheet.Borders!.Elements<Border>().Skip(1));
        Assert.Equal(BorderStyleValues.Thin, tableBorder.LeftBorder!.Style!.Value);
        Assert.Equal(BorderStyleValues.Thin, tableBorder.RightBorder!.Style!.Value);
        Assert.Equal(BorderStyleValues.Thin, tableBorder.TopBorder!.Style!.Value);
        Assert.Equal(BorderStyleValues.Thin, tableBorder.BottomBorder!.Style!.Value);
        Assert.Equal("FF000000", tableBorder.LeftBorder.Color!.Rgb!.Value);
        Assert.Equal("FF000000", tableBorder.RightBorder.Color!.Rgb!.Value);
        Assert.Equal("FF000000", tableBorder.TopBorder.Color!.Rgb!.Value);
        Assert.Equal("FF000000", tableBorder.BottomBorder.Color!.Rgb!.Value);
        var sheetView = Assert.Single(worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>());
        Assert.True(sheetView.ShowGridLines?.Value);
        var columns = Assert.IsType<Columns>(worksheet.GetFirstChild<Columns>());
        Assert.All(columns.Elements<Column>(), column =>
        {
            Assert.True(column.Width?.Value > 0);
            Assert.True(column.Width?.Value <= 32);
        });
        var firstBodyRow = Assert.Single(
            worksheet.GetFirstChild<SheetData>()!.Elements<Row>(),
            row => row.RowIndex?.Value == 1U);
        Assert.Equal(15, firstBodyRow.Height?.Value);
        Assert.True(firstBodyRow.CustomHeight?.Value);
        var cells = worksheet.Descendants<Cell>()
            .ToDictionary(cell => cell.CellReference!.Value!);
        Assert.Equal(CellValues.InlineString, cells["B2"].DataType!.Value);
        Assert.Equal("42", cells["B2"].InlineString!.Text!.Text);
        Assert.Equal(CellValues.InlineString, cells["C2"].DataType!.Value);
        Assert.Equal(CellValues.InlineString, cells["D2"].DataType!.Value);
        Assert.Equal(CellValues.InlineString, cells["A3"].DataType!.Value);
        Assert.Equal("007", cells["A3"].InlineString!.Text!.Text);
        Assert.Equal(3U, cells["A3"].StyleIndex!.Value);
    }

    [Fact]
    public void WorkbookWriter_DocumentMode_CombinesAllPagesIntoOneWorksheet()
    {
        using var workspace = new TemporaryDirectory();
        var outputPath = Path.Combine(workspace.Path, "combined.xlsx");
        var firstTable = new DocumentTable(
            1,
            1,
            2,
            2,
            new DocumentBounds(0, 0, 200, 60),
            DocumentTableSourceKind.DigitalText,
            1,
            [
                Cell(0, 0, "Item", DocumentCellValueKind.Text),
                Cell(0, 1, "Count", DocumentCellValueKind.Text),
                Cell(1, 0, "Alpha", DocumentCellValueKind.Text),
                Cell(1, 1, "1", DocumentCellValueKind.WholeNumber),
            ],
            []);
        var secondTable = new DocumentTable(
            2,
            1,
            2,
            2,
            new DocumentBounds(0, 0, 200, 60),
            DocumentTableSourceKind.DigitalText,
            1,
            [
                Cell(0, 0, "Item", DocumentCellValueKind.Text),
                Cell(0, 1, "Count", DocumentCellValueKind.Text),
                Cell(1, 0, "Beta", DocumentCellValueKind.Text),
                Cell(1, 1, "2", DocumentCellValueKind.WholeNumber),
            ],
            []);
        var extraction = new DocumentTableExtraction(
            "multi-page.pdf",
            2,
            [
                new DocumentTablePage(
                    1,
                    DocumentTableSourceKind.DigitalText,
                    500,
                    700,
                    [firstTable],
                    []),
                new DocumentTablePage(
                    2,
                    DocumentTableSourceKind.DigitalText,
                    500,
                    700,
                    [secondTable],
                    []),
            ]);

        OpenXmlTableWorkbookWriter.Write(
            outputPath,
            extraction,
            WorkerPdfWorksheetMode.OneWorksheetPerDocument);
        OpenXmlTableWorkbookWriter.Validate(outputPath, expectedTableSheetCount: 1);

        using var document = SpreadsheetDocument.Open(outputPath, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
        var sheets = Assert.IsType<Sheets>(workbook.Sheets);
        var sheet = Assert.Single(sheets.Elements<Sheet>());
        Assert.Equal("汇总", sheet.Name!.Value);
        var worksheetPart = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
        var worksheet = Assert.IsType<Worksheet>(worksheetPart.Worksheet);
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        Assert.Equal([1U, 2U, 4U, 5U], rows.Select(static row => row.RowIndex!.Value).ToArray());
        var cells = worksheet.Descendants<Cell>()
            .ToDictionary(cell => cell.CellReference!.Value!);
        Assert.Equal("Alpha", cells["A2"].InlineString!.Text!.Text);
        Assert.Equal("Item", cells["A4"].InlineString!.Text!.Text);
        Assert.Equal("Beta", cells["A5"].InlineString!.Text!.Text);
        Assert.Equal(
            1,
            OpenXmlTableWorkbookWriter.GetTableWorksheetCount(
                extraction,
                WorkerPdfWorksheetMode.OneWorksheetPerDocument));
    }

    [Fact]
    public async Task Runner_DigitalTable_PreservesSourceAndCreatesValidatedWorkbook()
    {
        using var workspace = new TemporaryDirectory();
        var inputPath = Path.Combine(workspace.Path, "digital-table.pdf");
        var outputPath = Path.Combine(workspace.Path, "digital-table.xlsx");
        CreateDigitalTablePdf(inputPath, fontSize: 14);
        var sourceHash = GetSha256(inputPath);
        var request = WorkerPdfToExcelStartMessage.Create(
            Guid.NewGuid(),
            inputPath,
            outputPath,
            new WorkerPdfToExcelOptions(
                WorkerPdfOcrMode.DigitalTextOnly,
                WorkerPdfWorksheetMode.OneWorksheetPerTable));

        var result = await new PdfTableWorkerRunner(FindRepositoryRoot()).RunAsync(request);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("1", result.Metrics!["digitalPageCount"]);
        Assert.Equal("0", result.Metrics["ocrPageCount"]);
        Assert.True(int.Parse(result.Metrics["tableCount"], System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceHash, GetSha256(inputPath));
        OpenXmlTableWorkbookWriter.Validate(
            outputPath,
            int.Parse(result.Metrics["worksheetCount"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Runner_ForceOcr_UsesPinnedOfflineEngine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryDirectory();
        var inputPath = Path.Combine(workspace.Path, "ocr-table.pdf");
        var outputPath = Path.Combine(workspace.Path, "ocr-table.xlsx");
        CreateDigitalTablePdf(inputPath, fontSize: 24);
        var sourceHash = GetSha256(inputPath);
        var request = WorkerPdfToExcelStartMessage.Create(
            Guid.NewGuid(),
            inputPath,
            outputPath,
            new WorkerPdfToExcelOptions(
                WorkerPdfOcrMode.ForceOcr,
                WorkerPdfWorksheetMode.OneWorksheetPerPage,
                "eng"));
        var modelCache = Path.Combine(workspace.Path, "models");

        var result = await new PdfTableWorkerRunner(
            FindRepositoryRoot(),
            new EmbeddedTessdataStore(modelCache)).RunAsync(request);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("1", result.Metrics!["ocrPageCount"]);
        Assert.True(
            int.Parse(result.Metrics["outputCellCount"], System.Globalization.CultureInfo.InvariantCulture) >= 6,
            string.Join(", ", result.Metrics.Select(static item => $"{item.Key}={item.Value}")));
        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceHash, GetSha256(inputPath));
        Assert.Contains(
            result.Notices!,
            static notice => notice.Code == "OCR_CONTENT_REQUIRES_REVIEW");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Client_ThroughAppHost_ConvertsDigitalTableAndReportsProgress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryDirectory();
        var inputPath = Path.Combine(workspace.Path, "host-input.pdf");
        var outputPath = Path.Combine(workspace.Path, "host-output.xlsx");
        CreateDigitalTablePdf(inputPath, fontSize: 14);
        var sourceHash = GetSha256(inputPath);
        var appPath = Path.Combine(AppContext.BaseDirectory, "DocPivot.exe");
        var stages = new List<string>();
        var client = new PdfTableWorkerClient(
            appPath,
            FindRepositoryRoot(),
            ["--pdf-table-worker"]);

        var result = await client.ConvertAsync(
            new PdfToExcelRequest(
                inputPath,
                outputPath,
                WorkerPdfOcrMode.DigitalTextOnly,
                WorkerPdfWorksheetMode.OneWorksheetPerTable),
            new InlineProgress<WorkerProgressMessage>(message => stages.Add(message.Stage)));

        Assert.True(result.IsSucceeded, result.ErrorMessage);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceHash, GetSha256(inputPath));
        Assert.Contains("page-extracting", stages);
        Assert.Contains("workbook-validating", stages);
        Assert.Equal("0", result.Metrics["ocrPageCount"]);
    }

    private static DocumentTableCell Cell(
        int row,
        int column,
        string text,
        DocumentCellValueKind kind,
        double confidence = 1) =>
        new(
            row,
            column,
            1,
            1,
            text,
            kind,
            confidence,
            new DocumentBounds(column * 220, row * 100, column * 220 + 80, row * 100 + 20));

    private static void CreateDigitalTablePdf(string path, double fontSize)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        string[][] values =
        [
            ["ITEM", "COUNT", "RATE"],
            ["ALPHA", "42", "12.5%"],
            ["BETA", "7", "8.0%"],
        ];
        int[] xPositions = [55, 240, 405];
        for (var row = 0; row < values.Length; row++)
        {
            var y = 740 - row * 55;
            for (var column = 0; column < values[row].Length; column++)
            {
                page.AddText(
                    values[row][column],
                    fontSize,
                    new PdfPoint(xPositions[column], y),
                    font);
            }
        }

        File.WriteAllBytes(path, builder.Build());
    }

    private static string GetSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DocPivot repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DocPivot.Tests",
                "PdfTableWorker",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var expectedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "DocPivot.Tests",
                    "PdfTableWorker"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var normalizedPath = System.IO.Path.GetFullPath(Path);
            if (normalizedPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(normalizedPath))
            {
                Directory.Delete(normalizedPath, recursive: true);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
