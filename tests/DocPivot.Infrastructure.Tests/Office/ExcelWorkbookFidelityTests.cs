using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Xml.Linq;
using DocPivot.Core.Excel;
using DocPivot.Infrastructure.Office;

namespace DocPivot.Infrastructure.Tests.Office;

public sealed class ExcelWorkbookFidelityTests
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Merge_PreservesDifferentNormalFontsColumnWidthsAndMergedCells()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("Excel.Application") is null)
        {
            return;
        }

        var root = CreateTestRoot();
        try
        {
            var first = Path.Combine(root, "first.xlsx");
            var second = Path.Combine(root, "second.xlsx");
            var output = Path.Combine(root, "merged.xlsx");
            CreateWorkbook(first, "等线", 23, ["Data"], 0x0000FF);
            CreateWorkbook(second, "宋体", 31, ["Data"], 0xFF0000);
            var firstHash = SHA256.HashData(File.ReadAllBytes(first));
            var secondHash = SHA256.HashData(File.ReadAllBytes(second));
            var client = CreateClient();

            var result = await client.ExecuteAsync(ExcelOperationRequest.Merge([first, second], output));

            Assert.True(result.IsSucceeded, result.ErrorCode + ": " + result.ErrorMessage);
            Assert.Equal(["Data", "Data (2)"], WorksheetNames(output));
            AssertSheetFormatting(output, 1, "等线", ReadColumnWidth(first), "FFFF0000");
            AssertSheetFormatting(output, 2, "宋体", ReadColumnWidth(second), "FF0000FF");
            Assert.Equal(firstHash, SHA256.HashData(File.ReadAllBytes(first)));
            Assert.Equal(secondHash, SHA256.HashData(File.ReadAllBytes(second)));
            Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp.xlsx"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Split_SelectedSheetsKeepWorkbookOrderAndReferencesToOmittedSheets()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("Excel.Application") is null)
        {
            return;
        }

        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "source.xlsx");
            CreateWorkbook(source, "宋体", 27, ["First", "Middle", "Third"], 0x0000FF);
            var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
            var client = CreateClient();
            var options = ExcelOperationOptions.SafeDefaults with
            {
                SelectedWorksheets = ["Third", "First"],
                SplitOutputMode = ExcelSplitOutputMode.SingleWorkbook,
            };

            var combined = await client.ExecuteAsync(ExcelOperationRequest.Split(source, root, options));
            var repeated = await client.ExecuteAsync(ExcelOperationRequest.Split(source, root, options));
            var separate = await client.ExecuteAsync(ExcelOperationRequest.Split(
                source, root, options with { SplitOutputMode = ExcelSplitOutputMode.SeparateFiles }));

            Assert.True(combined.IsSucceeded, combined.ErrorCode + ": " + combined.ErrorMessage);
            Assert.True(repeated.IsSucceeded, repeated.ErrorCode + ": " + repeated.ErrorMessage);
            Assert.True(separate.IsSucceeded, separate.ErrorCode + ": " + separate.ErrorMessage);
            Assert.Equal("source-选中工作表.xlsx", Path.GetFileName(Assert.Single(combined.Artifacts)));
            Assert.NotEqual(combined.Artifacts[0], Assert.Single(repeated.Artifacts));
            Assert.Equal(["First", "Third"], WorksheetNames(combined.Artifacts[0]));
            AssertSheetFormatting(combined.Artifacts[0], 1, "宋体", ReadColumnWidth(source), "FFFF0000");
            AssertSheetFormatting(combined.Artifacts[0], 2, "宋体", ReadColumnWidth(source), "FFFF0000");
            Assert.Equal(2, separate.Artifacts.Count);
            Assert.Equal(["First"], WorksheetNames(separate.Artifacts[0]));
            Assert.Equal(["Third"], WorksheetNames(separate.Artifacts[1]));
            using var package = ZipFile.OpenRead(combined.Artifacts[0]);
            var formula = ReadXml(package, "xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "f").Single().Value;
            Assert.Contains("Middle!", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("#REF!", formula, StringComparison.Ordinal);
            Assert.Contains(package.Entries, entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.Ordinal));
            Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWorkbook(string path, string fontName, double columnWidth, string[] names, int accentColor)
    {
        dynamic application = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
        dynamic? workbooks = null;
        dynamic? workbook = null;
        dynamic? worksheets = null;
        dynamic? styles = null;
        dynamic? normal = null;
        dynamic? font = null;
        dynamic? theme = null;
        dynamic? colorScheme = null;
        dynamic? accent = null;
        try
        {
            application.Visible = false;
            application.DisplayAlerts = false;
            application.SheetsInNewWorkbook = names.Length;
            workbooks = application.Workbooks;
            workbook = workbooks.Add();
            styles = workbook.Styles;
            normal = styles["Normal"];
            font = normal.Font;
            font.ThemeFont = 0;
            font.Name = fontName;
            theme = workbook.Theme;
            colorScheme = theme.ThemeColorScheme;
            accent = colorScheme.Colors(5);
            accent.RGB = accentColor;
            worksheets = workbook.Worksheets;
            for (var index = 0; index < names.Length; index++)
            {
                dynamic worksheet = worksheets[index + 1];
                dynamic? cells = null;
                dynamic? merged = null;
                dynamic? column = null;
                dynamic? colored = null;
                dynamic? interior = null;
                try
                {
                    worksheet.Name = names[index];
                    cells = worksheet.Range["A1"];
                    cells.Value2 = "Synthetic fixture";
                    merged = worksheet.Range["A1:B2"];
                    merged.Merge();
                    column = worksheet.Range["A:A"];
                    column.ColumnWidth = columnWidth;
                    colored = worksheet.Range["B3"];
                    colored.Value2 = "Accent";
                    interior = colored.Interior;
                    interior.ThemeColor = 5;
                    if (names.Length > 1 && index == 0)
                    {
                        dynamic formula = worksheet.Range["A4"];
                        try
                        {
                            formula.Formula = "=Middle!A1";
                        }
                        finally
                        {
                            Release(formula);
                        }
                    }
                }
                finally
                {
                    Release(interior);
                    Release(colored);
                    Release(column);
                    Release(merged);
                    Release(cells);
                    Release(worksheet);
                }
            }

            workbook.SaveAs(Filename: path, FileFormat: 51);
        }
        finally
        {
            workbook?.Close(SaveChanges: false);
            application.Quit();
            Release(accent);
            Release(colorScheme);
            Release(theme);
            Release(font);
            Release(normal);
            Release(styles);
            Release(worksheets);
            Release(workbook);
            Release(workbooks);
            Release(application);
        }
    }

    private static void AssertSheetFormatting(string path, int position, string fontName, double width, string accent)
    {
        using var package = ZipFile.OpenRead(path);
        var worksheet = ReadXml(package, $"xl/worksheets/sheet{position}.xml");
        var styles = ReadXml(package, "xl/styles.xml");
        var cellFormats = styles.Root!.Element(Spreadsheet + "cellXfs")!.Elements().ToArray();
        var fonts = styles.Root.Element(Spreadsheet + "fonts")!.Elements().ToArray();
        var cell = worksheet.Descendants(Spreadsheet + "c").Single(cell => (string?)cell.Attribute("r") == "A1");
        var format = cellFormats[(int?)cell.Attribute("s") ?? 0];
        Assert.Equal(fontName, (string?)fonts[(int)format.Attribute("fontId")!].Element(Spreadsheet + "name")?.Attribute("val"));
        Assert.Equal(width, (double)worksheet.Descendants(Spreadsheet + "col").First().Attribute("width")!);
        Assert.Contains(worksheet.Descendants(Spreadsheet + "mergeCell"), cell => (string?)cell.Attribute("ref") == "A1:B2");
        var colored = worksheet.Descendants(Spreadsheet + "c").Single(cell => (string?)cell.Attribute("r") == "B3");
        var fillId = (int)cellFormats[(int)colored.Attribute("s")!].Attribute("fillId")!;
        var fill = styles.Root.Element(Spreadsheet + "fills")!.Elements().ElementAt(fillId);
        Assert.Equal(accent, (string?)fill.Descendants(Spreadsheet + "fgColor").Single().Attribute("rgb"));
    }

    private static double ReadColumnWidth(string path)
    {
        using var package = ZipFile.OpenRead(path);
        return (double)ReadXml(package, "xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "col").First().Attribute("width")!;
    }

    private static string[] WorksheetNames(string path)
    {
        using var package = ZipFile.OpenRead(path);
        return ReadXml(package, "xl/workbook.xml").Descendants(Spreadsheet + "sheet").Select(sheet => (string)sheet.Attribute("name")!).ToArray();
    }

    private static XDocument ReadXml(ZipArchive package, string name)
    {
        using var stream = package.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static OfficeWorkerClient CreateClient()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "Release" : "Debug";
        var worker = Path.Combine(directory.FullName, "src", "DocPivot.OfficeWorker", "bin", configuration, "net10.0-windows", "DocPivot.OfficeWorker.exe");
        Assert.True(File.Exists(worker), "Build the solution before running Excel integration tests: " + worker);
        return new OfficeWorkerClient(worker);
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DocPivot", "excel-fidelity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? value)
    {
        if (value is not null)
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
