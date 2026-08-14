using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;

namespace DocPivot.PdfWorker;

public static class OpenXmlTableWorkbookWriter
{
    private const uint BodyStyle = 1;
    private const uint HeaderStyle = 2;
    private const uint LowConfidenceTextStyle = 3;
    private const uint IntegerStyle = 4;
    private const uint DecimalStyle = 5;
    private const uint PercentageStyle = 6;
    private const uint DateStyle = 7;
    private const uint LowConfidenceIntegerStyle = 8;
    private const uint LowConfidenceDecimalStyle = 9;
    private const uint LowConfidencePercentageStyle = 10;
    private const uint LowConfidenceDateStyle = 11;
    private const double LowConfidenceThreshold = 0.75;
    private const double PdfPointsPerExcelWidthUnit = 5.25;

    public static void Write(
        string outputPath,
        DocumentTableExtraction extraction,
        WorkerPdfWorksheetMode worksheetMode,
        bool includeReportWorksheet = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(extraction);

        var plans = BuildWorksheetPlans(extraction, worksheetMode);
        using var document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new BookViews(new WorkbookView()));
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;
        foreach (var plan in plans)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = CreateTableWorksheet(plan);
            worksheetPart.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = plan.Name,
            });
        }

        if (includeReportWorksheet)
        {
            var reportPart = workbookPart.AddNewPart<WorksheetPart>();
            reportPart.Worksheet = CreateReportWorksheet(extraction, worksheetMode);
            reportPart.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(reportPart),
                SheetId = sheetId,
                Name = "提取报告",
            });
        }

        workbookPart.Workbook.Save();
    }

    public static void Validate(string path, int expectedTableSheetCount, bool includeReportWorksheet = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedTableSheetCount);

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw InvalidWorkbook("The workbook part is missing.");
        var workbook = workbookPart.Workbook
            ?? throw InvalidWorkbook("The workbook content is missing.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var expectedSheetCount = includeReportWorksheet ? expectedTableSheetCount + 1 : expectedTableSheetCount;
        if (sheets.Length != expectedSheetCount ||
            sheets.Select(static sheet => sheet.Name?.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != sheets.Length)
        {
            throw InvalidWorkbook("The workbook sheet inventory is incomplete or ambiguous.");
        }

        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { Length: > 0 } relationshipId)
            {
                throw InvalidWorkbook("A workbook sheet relationship is missing.");
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(relationshipId);
            if (worksheetPart.Worksheet?.GetFirstChild<SheetData>() is null)
            {
                throw InvalidWorkbook("A workbook sheet has no readable cell data.");
            }
        }

        var validationErrors = new OpenXmlValidator(FileFormatVersions.Office2016)
            .Validate(document)
            .Take(5)
            .ToArray();
        if (validationErrors.Length > 0)
        {
            throw InvalidWorkbook(validationErrors[0].Description);
        }
    }

    public static int GetTableWorksheetCount(
        DocumentTableExtraction extraction,
        WorkerPdfWorksheetMode worksheetMode) =>
        worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerPage
            ? extraction.Pages.Count(static page => page.Tables.Count > 0)
            : extraction.Tables.Count;

    private static Worksheet CreateTableWorksheet(WorksheetPlan plan)
    {
        var sheetData = new SheetData();
        var rows = new SortedDictionary<uint, Row>();
        var widths = new double[Math.Max(1, plan.ColumnCount)];
        var freezeFirstRow = plan.Tables.Count == 1 && LooksLikeHeader(plan.Tables[0].Table);

        foreach (var placedTable in plan.Tables)
        {
            var isHeader = LooksLikeHeader(placedTable.Table);
            ApplyPdfColumnWidths(widths, placedTable.Table);
            var cellsByPosition = placedTable.Table.Cells.ToDictionary(
                static cell => (cell.RowIndex, cell.ColumnIndex));
            for (var rowIndex = 0; rowIndex < placedTable.Table.RowCount; rowIndex++)
            {
                var outputRowIndex = checked((uint)(placedTable.StartRow + rowIndex));
                if (!rows.TryGetValue(outputRowIndex, out var row))
                {
                    row = new Row { RowIndex = outputRowIndex };
                    rows.Add(outputRowIndex, row);
                }

                ApplyPdfRowHeight(row, placedTable.Table, rowIndex);

                for (var columnIndex = 0; columnIndex < placedTable.Table.ColumnCount; columnIndex++)
                {
                    cellsByPosition.TryGetValue((rowIndex, columnIndex), out var sourceCell);
                    var text = sourceCell?.Text ?? string.Empty;
                    widths[columnIndex] = Math.Max(
                        widths[columnIndex],
                        Math.Min(40, Math.Max(8, text.Length + 2)));
                    row.Append(CreateCell(
                        outputRowIndex,
                        columnIndex,
                        sourceCell,
                        isHeader && rowIndex == 0));
                }
            }
        }

        foreach (var row in rows.Values)
        {
            sheetData.Append(row);
        }

        var worksheet = new Worksheet();
        var sheetView = new SheetView { WorkbookViewId = 0U, ShowGridLines = false };
        if (freezeFirstRow)
        {
            sheetView.Append(new Pane
            {
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            });
        }

        worksheet.Append(new SheetViews(sheetView));
        worksheet.Append(CreateColumns(widths));
        worksheet.Append(sheetData);
        return worksheet;
    }

    private static void ApplyPdfColumnWidths(double[] widths, DocumentTable table)
    {
        var columnStarts = new double?[table.ColumnCount];
        for (var columnIndex = 0; columnIndex < table.ColumnCount; columnIndex++)
        {
            var starts = table.Cells
                .Where(cell => cell.ColumnIndex == columnIndex && cell.Bounds.IsValid)
                .Select(static cell => cell.Bounds.CenterX)
                .Order()
                .ToArray();
            if (starts.Length > 0)
            {
                columnStarts[columnIndex] = Median(starts);
            }
        }

        for (var columnIndex = 0; columnIndex < table.ColumnCount; columnIndex++)
        {
            if (columnStarts[columnIndex] is not { } start)
            {
                continue;
            }

            var nextStart = Enumerable.Range(columnIndex + 1, table.ColumnCount - columnIndex - 1)
                .Select(index => columnStarts[index])
                .FirstOrDefault(static value => value.HasValue);
            var widthInPoints = nextStart is { } next
                ? next - start
                : columnIndex > 0 && columnStarts[columnIndex - 1] is { } previous
                    ? start - previous
                    : table.Bounds.Right - start;
            if (!double.IsFinite(widthInPoints) || widthInPoints <= 0)
            {
                continue;
            }

            var excelWidth = Math.Clamp(
                (widthInPoints / PdfPointsPerExcelWidthUnit) + 1.5,
                6,
                60);
            widths[columnIndex] = Math.Max(widths[columnIndex], excelWidth);
        }
    }

    private static void ApplyPdfRowHeight(Row row, DocumentTable table, int rowIndex)
    {
        var rowCells = table.Cells
            .Where(cell => cell.RowIndex == rowIndex && cell.Bounds.IsValid)
            .Select(static cell => cell.Bounds.Height)
            .Order()
            .ToArray();
        if (rowCells.Length == 0)
        {
            return;
        }

        var currentTop = table.Cells
            .Where(cell => cell.RowIndex == rowIndex && cell.Bounds.IsValid)
            .Select(static cell => cell.Bounds.Top)
            .Min();
        var nextTop = table.Cells
            .Where(cell => cell.RowIndex > rowIndex && cell.Bounds.IsValid)
            .Select(static cell => cell.Bounds.Top)
            .DefaultIfEmpty(currentTop)
            .Min();
        var rowHeight = nextTop > currentTop
            ? (nextTop - currentTop) * 0.72
            : Median(rowCells) * 1.35;
        row.Height = Math.Clamp(rowHeight, 15, 72);
        row.CustomHeight = true;
    }

    private static double Median(double[] orderedValues)
    {
        var middle = orderedValues.Length / 2;
        return orderedValues.Length % 2 == 0
            ? (orderedValues[middle - 1] + orderedValues[middle]) / 2
            : orderedValues[middle];
    }

    private static Worksheet CreateReportWorksheet(
        DocumentTableExtraction extraction,
        WorkerPdfWorksheetMode worksheetMode)
    {
        var sheetData = new SheetData();
        AppendReportRow(sheetData, 1, ["PDF 转 Excel 提取报告"], header: true);
        AppendReportRow(sheetData, 2, ["源文件", extraction.SourceFileName]);
        AppendReportRow(sheetData, 3, [
            "摘要",
            $"{extraction.PageCount} 页 · {extraction.Tables.Count} 个表格 · " +
            $"数字页 {extraction.DigitalPageCount} · OCR 页 {extraction.OcrPageCount} · " +
            $"低置信度单元格 {extraction.LowConfidenceCellCount}",
        ]);
        AppendReportRow(sheetData, 4, [
            "工作表组织",
            worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerPage
                ? "每个 PDF 页面一个工作表"
                : "每个检测表格一个工作表",
        ]);
        AppendReportRow(sheetData, 6, ["页码", "表格", "来源", "尺寸", "置信度", "告警"], header: true);

        uint rowIndex = 7;
        foreach (var page in extraction.Pages)
        {
            if (page.Tables.Count == 0)
            {
                AppendReportRow(sheetData, rowIndex++, [
                    page.PageNumber.ToString(CultureInfo.InvariantCulture),
                    "-",
                    GetSourceText(page.SourceKind),
                    "-",
                    "-",
                    string.Join(", ", page.Warnings),
                ]);
                continue;
            }

            foreach (var table in page.Tables)
            {
                var warnings = page.Warnings.Concat(table.Warnings).Distinct(StringComparer.Ordinal);
                AppendReportRow(sheetData, rowIndex++, [
                    table.PageNumber.ToString(CultureInfo.InvariantCulture),
                    table.TableIndex.ToString(CultureInfo.InvariantCulture),
                    GetSourceText(table.SourceKind),
                    $"{table.RowCount} × {table.ColumnCount}",
                    table.Confidence.ToString("P1", CultureInfo.InvariantCulture),
                    string.Join(", ", warnings),
                ]);
            }
        }

        var worksheet = new Worksheet(
            new SheetViews(new SheetView { WorkbookViewId = 0U, ShowGridLines = false }),
            new Columns(
                CreateColumn(1, 1, 12),
                CreateColumn(2, 2, 18),
                CreateColumn(3, 3, 16),
                CreateColumn(4, 4, 14),
                CreateColumn(5, 5, 14),
                CreateColumn(6, 6, 48)),
            sheetData);
        return worksheet;
    }

    private static List<WorksheetPlan> BuildWorksheetPlans(
        DocumentTableExtraction extraction,
        WorkerPdfWorksheetMode worksheetMode)
    {
        var plans = new List<WorksheetPlan>();
        if (worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerPage)
        {
            foreach (var page in extraction.Pages.Where(static page => page.Tables.Count > 0))
            {
                var startRow = 1;
                var placedTables = new List<PlacedTable>();
                foreach (var table in page.Tables)
                {
                    placedTables.Add(new PlacedTable(table, startRow));
                    startRow += table.RowCount + 2;
                }

                plans.Add(new WorksheetPlan(
                    $"P{page.PageNumber}",
                    placedTables,
                    page.Tables.Max(static table => table.ColumnCount)));
            }
        }
        else
        {
            foreach (var table in extraction.Tables)
            {
                plans.Add(new WorksheetPlan(
                    $"P{table.PageNumber}-T{table.TableIndex}",
                    [new PlacedTable(table, 1)],
                    table.ColumnCount));
            }
        }

        return plans;
    }

    private static Cell CreateCell(
        uint rowIndex,
        int columnIndex,
        DocumentTableCell? source,
        bool isHeader)
    {
        var reference = $"{GetColumnName(columnIndex)}{rowIndex.ToString(CultureInfo.InvariantCulture)}";
        if (source is null || string.IsNullOrEmpty(source.Text))
        {
            return new Cell { CellReference = reference, StyleIndex = isHeader ? HeaderStyle : BodyStyle };
        }

        var lowConfidence = source.Confidence < LowConfidenceThreshold;
        var cell = new Cell
        {
            CellReference = reference,
            StyleIndex = GetStyleIndex(source.ValueKind, lowConfidence, isHeader),
        };
        // All extracted values are stored as text so identifiers that start
        // with zero (for example "007" or "20260311-1799") are never coerced
        // into numbers and lose their leading digits.
        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(new Text(source.Text)
        {
            Space = SpaceProcessingModeValues.Preserve,
        });

        return cell;
    }

    private static uint GetStyleIndex(
        DocumentCellValueKind kind,
        bool lowConfidence,
        bool isHeader)
    {
        if (isHeader && !lowConfidence)
        {
            return HeaderStyle;
        }

        return (kind, lowConfidence) switch
        {
            (DocumentCellValueKind.WholeNumber, false) => IntegerStyle,
            (DocumentCellValueKind.FractionalNumber, false) => DecimalStyle,
            (DocumentCellValueKind.Percentage, false) => PercentageStyle,
            (DocumentCellValueKind.Date, false) => DateStyle,
            (DocumentCellValueKind.WholeNumber, true) => LowConfidenceIntegerStyle,
            (DocumentCellValueKind.FractionalNumber, true) => LowConfidenceDecimalStyle,
            (DocumentCellValueKind.Percentage, true) => LowConfidencePercentageStyle,
            (DocumentCellValueKind.Date, true) => LowConfidenceDateStyle,
            (_, true) => LowConfidenceTextStyle,
            _ => BodyStyle,
        };
    }

    private static bool LooksLikeHeader(DocumentTable table)
    {
        if (table.RowCount < 2)
        {
            return false;
        }

        var firstRow = table.Cells.Where(static cell => cell.RowIndex == 0).ToArray();
        var remaining = table.Cells.Where(static cell => cell.RowIndex > 0).ToArray();
        return firstRow.Length >= 2 &&
            firstRow.All(static cell => cell.ValueKind == DocumentCellValueKind.Text) &&
            remaining.Any(static cell => cell.ValueKind != DocumentCellValueKind.Text);
    }

    private static Columns CreateColumns(double[] widths)
    {
        var columns = new Columns();
        for (var index = 0; index < widths.Length; index++)
        {
            columns.Append(CreateColumn(
                checked((uint)index + 1),
                checked((uint)index + 1),
                widths[index] <= 0 ? 12 : widths[index]));
        }

        return columns;
    }

    private static Column CreateColumn(uint minimum, uint maximum, double width) =>
        new()
        {
            Min = minimum,
            Max = maximum,
            Width = width,
            CustomWidth = true,
        };

    private static void AppendReportRow(
        SheetData sheetData,
        uint rowIndex,
        IReadOnlyList<string> values,
        bool header = false)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var index = 0; index < values.Count; index++)
        {
            row.Append(new Cell
            {
                CellReference = $"{GetColumnName(index)}{rowIndex.ToString(CultureInfo.InvariantCulture)}",
                DataType = CellValues.InlineString,
                StyleIndex = header ? HeaderStyle : BodyStyle,
                InlineString = new InlineString(new Text(values[index])
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            });
        }

        sheetData.Append(row);
    }

    private static string GetColumnName(int zeroBasedIndex)
    {
        var value = zeroBasedIndex + 1;
        Span<char> buffer = stackalloc char[8];
        var position = buffer.Length;
        while (value > 0)
        {
            value--;
            buffer[--position] = (char)('A' + (value % 26));
            value /= 26;
        }

        return new string(buffer[position..]);
    }

    private static string GetSourceText(DocumentTableSourceKind sourceKind) =>
        sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition ? "本地 OCR" : "PDF 文字层";

    private static Stylesheet CreateStylesheet()
    {
        var fonts = new Fonts(
            new Font(
                new FontSize { Val = 11D },
                new FontName { Val = "Aptos" }),
            new Font(
                new Bold(),
                new FontSize { Val = 11D },
                new Color { Rgb = "FFFFFFFF" },
                new FontName { Val = "Aptos" }))
        {
            Count = 2U,
        };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF2F5D50" },
                new BackgroundColor { Indexed = 64U })
            {
                PatternType = PatternValues.Solid,
            }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FFFFE4A3" },
                new BackgroundColor { Indexed = 64U })
            {
                PatternType = PatternValues.Solid,
            }))
        {
            Count = 4U,
        };
        var borders = new Borders(
            new Border(),
            new Border(
                new LeftBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FFD7DDD9" },
                },
                new RightBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FFD7DDD9" },
                },
                new TopBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FFD7DDD9" },
                },
                new BottomBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FFD7DDD9" },
                },
                new DiagonalBorder()))
        {
            Count = 2U,
        };
        var cellStyleFormats = new CellStyleFormats(new CellFormat()) { Count = 1U };
        var cellFormats = new CellFormats(
            new CellFormat(),
            CreateCellFormat(0, 0, 1, 0),
            CreateCellFormat(1, 2, 1, 0, horizontal: HorizontalAlignmentValues.Center),
            CreateCellFormat(0, 3, 1, 0),
            CreateCellFormat(0, 0, 1, 0),
            CreateCellFormat(0, 0, 1, 0),
            CreateCellFormat(0, 0, 1, 0),
            CreateCellFormat(0, 0, 1, 0),
            CreateCellFormat(0, 3, 1, 0),
            CreateCellFormat(0, 3, 1, 0),
            CreateCellFormat(0, 3, 1, 0),
            CreateCellFormat(0, 3, 1, 0))
        {
            Count = 12U,
        };
        var cellStyles = new CellStyles(new CellStyle
        {
            Name = "Normal",
            FormatId = 0U,
            BuiltinId = 0U,
        })
        {
            Count = 1U,
        };

        return new Stylesheet(
            fonts,
            fills,
            borders,
            cellStyleFormats,
            cellFormats,
            cellStyles);
    }

    private static CellFormat CreateCellFormat(
        uint fontId,
        uint fillId,
        uint borderId,
        uint numberFormatId) =>
        CreateCellFormat(
            fontId,
            fillId,
            borderId,
            numberFormatId,
            HorizontalAlignmentValues.Left);

    private static CellFormat CreateCellFormat(
        uint fontId,
        uint fillId,
        uint borderId,
        uint numberFormatId,
        HorizontalAlignmentValues horizontal) =>
        new()
        {
            FontId = fontId,
            FillId = fillId,
            BorderId = borderId,
            NumberFormatId = numberFormatId,
            ApplyFont = fontId > 0,
            ApplyFill = fillId > 0,
            ApplyBorder = true,
            ApplyNumberFormat = numberFormatId > 0,
            ApplyAlignment = true,
            Alignment = new Alignment
            {
                Horizontal = horizontal,
                Vertical = VerticalAlignmentValues.Center,
                WrapText = true,
            },
        };

    private static PdfTableWorkerException InvalidWorkbook(string message) =>
        new("XLSX_VALIDATION_FAILED", message);

    private sealed record PlacedTable(DocumentTable Table, int StartRow);

    private sealed record WorksheetPlan(
        string Name,
        IReadOnlyList<PlacedTable> Tables,
        int ColumnCount);
}
