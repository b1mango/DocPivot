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
    private const uint ReportTitleStyle = 12;
    private const uint ReportLabelStyle = 13;
    private const uint ReportValueStyle = 14;
    private const uint ReportDateStyle = 15;
    private const double LowConfidenceThreshold = 0.75;
    private const double MinimumColumnWidth = 6;
    private const double MaximumColumnWidth = 32;
    private const double DefaultColumnWidth = 8;
    private const double SingleLineRowHeight = 15;
    private const double MaximumRowHeight = 60;
    private const int MaximumWorksheetRows = 1_048_576;

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
        stylesPart.Stylesheet = CreateStylesheet(plans.Any(static plan => plan.Report is not null));
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
        WorkerPdfWorksheetMode worksheetMode) => BuildWorksheetPlans(extraction, worksheetMode).Count;

    private static Worksheet CreateTableWorksheet(WorksheetPlan plan)
    {
        var sheetData = new SheetData();
        var rows = new SortedDictionary<uint, Row>();
        var widths = CalculateColumnWidths(plan);
        var mergedCells = new MergeCells();
        var frozenRows = plan.Report is null && plan.Tables.Count == 1 ? plan.Tables[0].Table.HeaderRowCount : 0;
        if (plan.Report is { } report)
        {
            widths[0] = Math.Max(widths[0], InspectionReport.Labels.Max(GetTextDisplayWidth) + 2);
            AddReportRows(plan, report, widths, rows, mergedCells);
        }

        foreach (var placedTable in plan.Tables)
        {
            foreach (var cell in placedTable.Table.Cells.Where(static cell => cell.RowSpan > 1 || cell.ColumnSpan > 1))
            {
                var firstRow = placedTable.StartRow + cell.RowIndex;
                var lastRow = firstRow + cell.RowSpan - 1;
                mergedCells.Append(new MergeCell
                {
                    Reference = $"{GetColumnName(cell.ColumnIndex)}{firstRow.ToString(CultureInfo.InvariantCulture)}:" +
                        $"{GetColumnName(cell.ColumnIndex + cell.ColumnSpan - 1)}{lastRow.ToString(CultureInfo.InvariantCulture)}",
                });
            }

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

                ApplyContentRowHeight(row, placedTable.Table, rowIndex, widths);

                for (var columnIndex = 0; columnIndex < placedTable.Table.ColumnCount; columnIndex++)
                {
                    cellsByPosition.TryGetValue((rowIndex, columnIndex), out var sourceCell);
                    row.Append(CreateCell(
                        outputRowIndex,
                        columnIndex,
                        sourceCell,
                        rowIndex < placedTable.Table.HeaderRowCount));
                }
            }
        }

        foreach (var row in rows.Values)
        {
            sheetData.Append(row);
        }

        var worksheet = new Worksheet();
        if (plan.Report is not null)
        {
            worksheet.Append(new SheetProperties(new PageSetupProperties { FitToPage = true }));
        }
        var sheetView = new SheetView { WorkbookViewId = 0U, ShowGridLines = true };
        if (frozenRows > 0)
        {
            sheetView.Append(new Pane
            {
                VerticalSplit = frozenRows,
                TopLeftCell = $"A{(frozenRows + 1).ToString(CultureInfo.InvariantCulture)}",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            });
        }

        worksheet.Append(new SheetViews(sheetView));
        worksheet.Append(CreateColumns(widths));
        worksheet.Append(sheetData);
        if (mergedCells.HasChildren)
        {
            worksheet.Append(mergedCells);
        }

        if (plan.Report is not null)
        {
            worksheet.Append(new PageSetup { PaperSize = 9U, Orientation = OrientationValues.Portrait, FitToWidth = 1U, FitToHeight = 0U });
        }

        return worksheet;
    }

    private static void AddReportRows(WorksheetPlan plan, InspectionReport report, double[] widths, SortedDictionary<uint, Row> rows, MergeCells merges)
    {
        var lastColumn = GetColumnName(widths.Length - 1);
        var title = new Row { RowIndex = 1U, Height = 26D, CustomHeight = true };
        title.Append(ReportTextCell("A1", "检测报告", ReportTitleStyle));
        rows.Add(1U, title);
        merges.Append(new MergeCell { Reference = $"A1:{lastColumn}1" });
        var valueWidth = widths.Skip(1).Sum();
        for (var index = 0; index < InspectionReport.Labels.Count; index++)
        {
            var label = InspectionReport.Labels[index];
            AddField((uint)index + 2, label, report.Fields.GetValueOrDefault(label, string.Empty));
        }

        if (report.Fields.TryGetValue("备注", out var note) && note.Length > 0)
        {
            var last = plan.Tables[^1];
            AddField((uint)(last.StartRow + last.Table.RowCount + 1), "备注", note);
        }

        void AddField(uint index, string label, string text)
        {
            var rowNumber = index.ToString(CultureInfo.InvariantCulture);
            var row = new Row
            {
                RowIndex = index,
                Height = Math.Clamp(GetWrappedLineCount(text, valueWidth) * SingleLineRowHeight, SingleLineRowHeight, MaximumRowHeight),
                CustomHeight = true,
            };
            row.Append(ReportTextCell("A" + rowNumber, label, ReportLabelStyle));
            var reference = "B" + rowNumber;
            if (label is "授权签字人" or "样品接收日期" && DateTime.TryParseExact(text, "yyyy年M月d日",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1900)
            {
                row.Append(new Cell { CellReference = reference, StyleIndex = ReportDateStyle,
                    DataType = CellValues.Number, CellValue = new CellValue(ExcelDateSerial(date)) });
            }
            else
            {
                row.Append(ReportTextCell(reference, text, ReportValueStyle));
            }

            rows.Add(index, row);
            if (widths.Length > 2)
            {
                merges.Append(new MergeCell { Reference = $"{reference}:{lastColumn}{rowNumber}" });
            }
        }
    }

    private static Cell ReportTextCell(string reference, string text, uint style) => new()
    {
        CellReference = reference,
        StyleIndex = style,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(text)),
    };

    private static double[] CalculateColumnWidths(WorksheetPlan plan)
    {
        var widths = Enumerable.Repeat(DefaultColumnWidth, Math.Max(1, plan.ColumnCount)).ToArray();
        foreach (var placedTable in plan.Tables)
        {
            foreach (var cell in placedTable.Table.Cells)
            {
                var contentWidth = (GetMaximumLineDisplayWidth(cell.Text) + 2) / cell.ColumnSpan;
                for (var columnIndex = cell.ColumnIndex; columnIndex < cell.ColumnIndex + cell.ColumnSpan; columnIndex++)
                {
                    widths[columnIndex] = Math.Max(
                        widths[columnIndex],
                        Math.Clamp(contentWidth, MinimumColumnWidth, MaximumColumnWidth));
                }
            }
        }

        return widths;
    }

    private static void ApplyContentRowHeight(
        Row row,
        DocumentTable table,
        int rowIndex,
        double[] widths)
    {
        var lineCount = table.Cells
            .Where(cell => cell.RowIndex <= rowIndex && cell.RowIndex + cell.RowSpan > rowIndex)
            .Select(cell => (double)GetWrappedLineCount(
                cell.Text,
                widths.Skip(cell.ColumnIndex).Take(cell.ColumnSpan).Sum()) / cell.RowSpan)
            .DefaultIfEmpty(1)
            .Max();
        row.Height = Math.Clamp(
            lineCount * SingleLineRowHeight,
            SingleLineRowHeight,
            MaximumRowHeight);
        row.CustomHeight = true;
    }

    private static int GetWrappedLineCount(string text, double columnWidth)
    {
        var usableWidth = Math.Max(1, columnWidth - 2);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Sum(line => Math.Max(1, (int)Math.Ceiling(GetTextDisplayWidth(line) / usableWidth)));
    }

    private static double GetMaximumLineDisplayWidth(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(GetTextDisplayWidth)
            .DefaultIfEmpty(0)
            .Max();

    private static double GetTextDisplayWidth(string text)
    {
        double width = 0;
        foreach (var character in text)
        {
            width += character switch
            {
                '\t' => 4,
                <= '\u007f' => 1,
                _ => 2,
            };
        }

        return width;
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
            worksheetMode switch
            {
                WorkerPdfWorksheetMode.OneWorksheetPerDocument => "整份 PDF 汇总到一个工作表",
                WorkerPdfWorksheetMode.OneWorksheetPerPage => "每个 PDF 页面一个工作表",
                _ => "每个检测表格一个工作表",
            },
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
            new SheetViews(new SheetView { WorkbookViewId = 0U, ShowGridLines = true }),
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
        if (worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerDocument && InspectionReport.Detect(extraction.Pages) is { } report)
        {
            // Rows 1-10 are the report header; downstream formulas expect the body table at row 11.
            var placed = new List<PlacedTable> { new(report.Body, 11) };
            var nextRow = 11 + report.Body.RowCount + 1;
            foreach (var table in report.OtherTables)
            {
                placed.Add(new PlacedTable(table, nextRow));
                nextRow = checked(nextRow + table.RowCount + 1);
            }

            var trailingRows = report.Fields.TryGetValue("备注", out var note) && note.Length > 0 ? 1 : 0;
            if (nextRow - 1 + trailingRows > MaximumWorksheetRows)
            {
                throw new PdfTableWorkerException("PDF_TABLE_ROW_LIMIT_EXCEEDED", "The extracted report exceeds the Excel worksheet row limit.");
            }

            return [new WorksheetPlan(
                "汇总",
                placed,
                placed.Max(static table => table.Table.ColumnCount),
                report)];
        }

        var tables = worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerPage
            ? extraction.Tables
            : TableContinuationMerger.Merge(extraction.Pages);
        if (worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerDocument)
        {
            if (tables.Count > 0)
            {
                plans.Add(new WorksheetPlan(
                    "汇总",
                    PlaceTables(tables),
                    tables.Max(static table => table.ColumnCount)));
            }
        }
        else if (worksheetMode == WorkerPdfWorksheetMode.OneWorksheetPerPage)
        {
            foreach (var page in extraction.Pages.Where(static page => page.Tables.Count > 0))
            {
                plans.Add(new WorksheetPlan(
                    $"P{page.PageNumber}",
                    PlaceTables(page.Tables),
                    page.Tables.Max(static table => table.ColumnCount)));
            }
        }
        else
        {
            foreach (var table in tables)
            {
                plans.Add(new WorksheetPlan(
                    $"P{table.PageNumber}-T{table.TableIndex}",
                    PlaceTables([table]),
                    table.ColumnCount));
            }
        }

        return plans;
    }

    private static List<PlacedTable> PlaceTables(IReadOnlyList<DocumentTable> tables)
    {
        var startRow = 1;
        var placedTables = new List<PlacedTable>(tables.Count);
        foreach (var table in tables)
        {
            if (table.RowCount > MaximumWorksheetRows - startRow + 1)
            {
                throw new PdfTableWorkerException(
                    "PDF_TABLE_ROW_LIMIT_EXCEEDED",
                    "The extracted tables exceed the Excel worksheet row limit.");
            }

            placedTables.Add(new PlacedTable(table, startRow));
            startRow = checked(startRow + table.RowCount + 1);
        }

        return placedTables;
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
        if (source.ValueKind == DocumentCellValueKind.Text)
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(source.Text)
            {
                Space = SpaceProcessingModeValues.Preserve,
            });
        }
        else
        {
            cell.DataType = CellValues.Number;
            cell.CellValue = new CellValue(GetNumericValue(source));
        }

        return cell;
    }

    private static string GetNumericValue(DocumentTableCell source)
    {
        var text = source.Text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (source.ValueKind == DocumentCellValueKind.Percentage)
        {
            return (double.Parse(text.AsSpan(0, text.Length - 1), CultureInfo.InvariantCulture) / 100)
                .ToString("R", CultureInfo.InvariantCulture);
        }

        if (source.ValueKind == DocumentCellValueKind.Date)
        {
            var date = DateTime.ParseExact(
                text,
                ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            return ExcelDateSerial(date).ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static double ExcelDateSerial(DateTime date) =>
        date < new DateTime(1900, 3, 1) ? date.ToOADate() - 1 : date.ToOADate();

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

    private static Stylesheet CreateStylesheet(bool inspectionReport)
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
                    Color = new Color { Rgb = "FF000000" },
                },
                new RightBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FF000000" },
                },
                new TopBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FF000000" },
                },
                new BottomBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new Color { Rgb = "FF000000" },
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
            CreateCellFormat(0, 0, 1, 3),
            CreateCellFormat(0, 0, 1, 164),
            CreateCellFormat(0, 0, 1, 165),
            CreateCellFormat(0, 0, 1, 14),
            CreateCellFormat(0, 3, 1, 3),
            CreateCellFormat(0, 3, 1, 164),
            CreateCellFormat(0, 3, 1, 165),
            CreateCellFormat(0, 3, 1, 14))
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

        var formats = new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "#,##0.00#############" },
                new NumberingFormat { NumberFormatId = 165U, FormatCode = "0.###############%" }) { Count = 2U };
        if (inspectionReport)
        {
            fonts.Append(new Font(new Bold(), new FontSize { Val = 14D }, new FontName { Val = "Aptos" }),
                new Font(new Bold(), new FontSize { Val = 11D }, new Color { Rgb = "FF404040" }, new FontName { Val = "Aptos" }));
            fonts.Count = 4U;
            formats.Append(new NumberingFormat { NumberFormatId = 166U, FormatCode = "yyyy\"年\"m\"月\"d\"日\"" });
            formats.Count = 3U;
            cellFormats.Append(CreateCellFormat(2, 0, 0, 0, HorizontalAlignmentValues.Center),
                CreateCellFormat(3, 0, 0, 0), CreateCellFormat(0, 0, 0, 0), CreateCellFormat(0, 0, 0, 166));
            cellFormats.Count = 16U;
        }

        return new Stylesheet(formats,
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
        int ColumnCount,
        InspectionReport? Report = null);
}
