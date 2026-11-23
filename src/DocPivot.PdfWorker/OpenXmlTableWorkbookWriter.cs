using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DocPivot.Core.Contracts;
using DocPivot.Core.Tables;

namespace DocPivot.PdfWorker;

public static partial class OpenXmlTableWorkbookWriter
{
    private const uint BodyStyle = 1;
    private const uint HeaderStyle = 2;
    private const uint LowConfidenceTextStyle = 3;
    private const uint ReportDateStyle = 4;
    private const uint ReportTitleStyle = 5;
    private const double LowConfidenceThreshold = 0.75;
    private const double MinimumColumnWidth = 6;
    // Matches Excel auto-fit: columns grow to their content up to Excel's own 255-character limit.
    private const double MaximumColumnWidth = 255;
    // Columns are sized to their content plus one character so sheets stay as narrow as possible.
    private const double CellPadding = 1;
    // Auto-fit height of one line of 11pt 宋体.
    private const double SingleLineRowHeight = 14.25;
    private const double MaximumRowHeight = 409;
    private const int MaximumWorksheetRows = 1_048_576;
    // Excel's built-in 数值/百分比 formats; other decimal counts get an equivalent custom code.
    private static readonly Dictionary<string, uint> BuiltInNumberFormats = new(StringComparer.Ordinal)
    {
        ["0"] = 1U,
        ["0.00"] = 2U,
        ["0%"] = 9U,
        ["0.00%"] = 10U,
    };

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
        var formatStyles = new Dictionary<(string, bool), uint>();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;
        foreach (var plan in plans)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = CreateTableWorksheet(plan, GetFormatStyle);
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

        stylesPart.Stylesheet.Save();
        workbookPart.Workbook.Save();

        uint GetFormatStyle(string code, bool lowConfidence)
        {
            if (!formatStyles.TryGetValue((code, lowConfidence), out var style))
            {
                if (!BuiltInNumberFormats.TryGetValue(code, out var numberFormatId))
                {
                    var stylesheet = stylesPart.Stylesheet;
                    var formats = stylesheet.NumberingFormats ??= new NumberingFormats { Count = 0U };
                    var numberFormat = formats.Elements<NumberingFormat>().FirstOrDefault(format => format.FormatCode == code);
                    if (numberFormat is null)
                    {
                        numberFormat = new NumberingFormat { NumberFormatId = 164U + formats.Count!.Value, FormatCode = code };
                        formats.Append(numberFormat);
                        formats.Count++;
                    }

                    numberFormatId = numberFormat.NumberFormatId!.Value;
                }

                var cells = stylesPart.Stylesheet.CellFormats!;
                style = cells.Count!.Value;
                cells.Append(CreateCellFormat(0, lowConfidence ? 3U : 0U, 1, numberFormatId));
                cells.Count++;
                formatStyles.Add((code, lowConfidence), style);
            }

            return style;
        }
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

    private static Worksheet CreateTableWorksheet(WorksheetPlan plan, Func<string, bool, uint> getFormatStyle)
    {
        var numericText = new List<string>();
        var sheetData = new SheetData();
        var rows = new SortedDictionary<uint, Row>();
        var layout = new WorksheetLayout(plan.ColumnCount);
        var frozenRows = plan.Report is null && plan.Tables.Count == 1 ? plan.Tables[0].Table.HeaderRowCount : 0;
        if (plan.Report is { } report)
        {
            AddReportRows(plan, report, rows, layout);
        }

        foreach (var placedTable in plan.Tables)
        {
            var firstSampleRow = plan.Report?.FirstSampleRow(placedTable.Table);
            var cellsByPosition = placedTable.Table.Cells.ToDictionary(
                static cell => (cell.RowIndex, cell.ColumnIndex));
            for (var rowIndex = 0; rowIndex < placedTable.Table.RowCount; rowIndex++)
            {
                var outputRowIndex = checked((uint)(placedTable.StartRow + rowIndex));
                if (!rows.TryGetValue(outputRowIndex, out var row))
                {
                    row = new Row { RowIndex = outputRowIndex, Height = SingleLineRowHeight, CustomHeight = true };
                    rows.Add(outputRowIndex, row);
                }

                for (var columnIndex = 0; columnIndex < placedTable.Table.ColumnCount; columnIndex++)
                {
                    cellsByPosition.TryGetValue((rowIndex, columnIndex), out var sourceCell);
                    var cell = CreateCell(
                        outputRowIndex,
                        columnIndex,
                        sourceCell,
                        rowIndex < placedTable.Table.HeaderRowCount,
                        columnIndex == 0 && rowIndex >= firstSampleRow, getFormatStyle);
                    if (cell.InlineString is { } inline && NumericTextPattern().IsMatch(inline.InnerText))
                    {
                        numericText.Add(cell.CellReference!.Value!);
                    }

                    layout.Append(row, columnIndex, cell, sourceCell?.ColumnSpan ?? 1, sourceCell?.RowSpan ?? 1,
                        cell.CellValue is not null ? GetDisplayText(sourceCell!) : null);
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
        worksheet.Append(layout.AutoFit(rows));
        worksheet.Append(sheetData);
        if (layout.Merges.HasChildren)
        {
            worksheet.Append(layout.Merges);
        }

        if (plan.Report is not null)
        {
            worksheet.Append(new PageSetup { PaperSize = 9U, Orientation = OrientationValues.Portrait, FitToWidth = 1U, FitToHeight = 0U });
        }

        if (numericText.Count > 0)
        {
            // Sample numbers such as 0926001 stay text in General format without Excel's "number stored as text" flag.
            worksheet.Append(new IgnoredErrors(new IgnoredError
            {
                NumberStoredAsText = true,
                SequenceOfReferences = new ListValue<StringValue>(numericText.Select(static reference => new StringValue(reference))),
            }));
        }

        return worksheet;
    }

    private static void AddReportRows(WorksheetPlan plan, InspectionReport report, SortedDictionary<uint, Row> rows, WorksheetLayout layout)
    {
        // Header rows reuse the plain body cell style so the sheet keeps the original minimal look.
        var title = new Row { RowIndex = 1U, Height = SingleLineRowHeight, CustomHeight = true };
        layout.Append(title, 0, ReportTextCell("A1", "检测报告", ReportTitleStyle), plan.ColumnCount);
        AppendMergeFillers(title, 1U, 1);
        rows.Add(1U, title);
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
                Height = SingleLineRowHeight,
                CustomHeight = true,
            };
            layout.Append(row, 0, ReportTextCell("A" + rowNumber, label, BodyStyle));
            var reference = "B" + rowNumber;
            Cell value;
            if (label is "授权签字人" or "样品接收日期" && DateTime.TryParseExact(text, "yyyy年M月d日",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1900)
            {
                value = new Cell { CellReference = reference, StyleIndex = ReportDateStyle,
                    DataType = CellValues.Number, CellValue = new CellValue(ExcelDateSerial(date)) };
            }
            else
            {
                value = ReportTextCell(reference, text, BodyStyle);
            }

            layout.Append(row, 1, value, plan.ColumnCount - 1);
            AppendMergeFillers(row, index, 2);
            rows.Add(index, row);
        }

        // Styled empty cells inside a merged range so its borders render on every edge.
        void AppendMergeFillers(Row row, uint rowIndex, int firstColumn)
        {
            for (var columnIndex = firstColumn; columnIndex < plan.ColumnCount; columnIndex++)
            {
                row.Append(new Cell
                {
                    CellReference = GetColumnName(columnIndex) + rowIndex.ToString(CultureInfo.InvariantCulture),
                    StyleIndex = BodyStyle,
                });
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

    private sealed class WorksheetLayout(int columnCount)
    {
        private readonly double[] _widths = Enumerable.Repeat(MinimumColumnWidth, columnCount).ToArray();
        private readonly List<(uint Row, int Column, int RowSpan, int ColumnSpan, string Text)> _contents = [];
        public MergeCells Merges { get; } = new();

        public void Append(Row row, int column, Cell cell, int columnSpan = 1, int rowSpan = 1, string? displayText = null)
        {
            row.Append(cell);
            var rowIndex = row.RowIndex!.Value;
            var text = displayText ?? GetDisplayText(cell);
            if (rowSpan > 1 || columnSpan > 1)
            {
                var lastRow = rowIndex + (uint)rowSpan - 1;
                Merges.Append(new MergeCell { Reference = $"{cell.CellReference}:{GetColumnName(column + columnSpan - 1)}{lastRow.ToString(CultureInfo.InvariantCulture)}" });
            }
            else if (text.Length > 0)
            {
                _widths[column] = Math.Clamp(GetMaximumLineDisplayWidth(text) + CellPadding, _widths[column], MaximumColumnWidth);
            }

            if (text.Length > 0)
            {
                _contents.Add((rowIndex, column, rowSpan, columnSpan, text));
            }
        }

        public Columns AutoFit(SortedDictionary<uint, Row> rows)
        {
            foreach (var (firstRow, column, rowSpan, columnSpan, text) in _contents)
            {
                var width = _widths.Skip(column).Take(columnSpan).Sum();
                var height = Math.Min(MaximumRowHeight, GetWrappedLineCount(text, width) * SingleLineRowHeight / rowSpan);
                for (uint offset = 0; offset < rowSpan; offset++)
                {
                    var row = rows[firstRow + offset];
                    row.Height = Math.Max(row.Height!.Value, height);
                }
            }

            return CreateColumns(_widths);
        }
    }

    private static string GetDisplayText(Cell cell)
    {
        if (cell.CellValue is not { } number)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        var culture = CultureInfo.InvariantCulture;
        if (cell.StyleIndex?.Value == ReportDateStyle)
        {
            var serial = double.Parse(number.Text, culture);
            var date = DateTime.FromOADate(serial < 60 ? serial + 1 : serial);
            return date.ToString("yyyy年M月d日", culture);
        }

        return number.Text;
    }

    // Number formats mirror the source text, so the displayed value is the source without thousands separators.
    private static string GetDisplayText(DocumentTableCell source) =>
        source.ValueKind == DocumentCellValueKind.Date
            ? source.Text.Trim()
            : source.Text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);

    private static int GetWrappedLineCount(string text, double columnWidth)
    {
        var usableWidth = Math.Max(1, columnWidth - CellPadding);
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
                // CJK glyphs are a little wider than two SimSun digit widths.
                _ => 2.2,
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
            if (placedTables.Count > 0 && IsAdjacentFieldBlock(placedTables[^1].Table, table))
            {
                startRow--;
            }

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

    private static bool IsAdjacentFieldBlock(DocumentTable fields, DocumentTable grid)
    {
        var gap = grid.Bounds.Top - fields.Bounds.Bottom;
        return !fields.IsRuled && grid.IsRuled && fields.PageNumber == grid.PageNumber &&
            fields.ColumnCount == grid.ColumnCount && gap >= 0 && gap <= fields.Bounds.Height / fields.RowCount &&
            fields.Bounds.Left < grid.Bounds.Right && fields.Bounds.Right > grid.Bounds.Left &&
            fields.Cells.Count(cell => cell.ColumnIndex == 0 &&
                (cell.Text.EndsWith(':') || cell.Text.EndsWith('：'))) == fields.RowCount;
    }

    private static Cell CreateCell(
        uint rowIndex,
        int columnIndex,
        DocumentTableCell? source,
        bool isHeader,
        bool isSampleNumber,
        Func<string, bool, uint> getFormatStyle)
    {
        var reference = $"{GetColumnName(columnIndex)}{rowIndex.ToString(CultureInfo.InvariantCulture)}";
        if (source is null || string.IsNullOrEmpty(source.Text))
        {
            return new Cell { CellReference = reference, StyleIndex = isHeader ? HeaderStyle : BodyStyle };
        }

        var lowConfidence = source.Confidence < LowConfidenceThreshold;
        var isText = isSampleNumber || source.ValueKind == DocumentCellValueKind.Text;
        var style = isSampleNumber ? BodyStyle
            : isHeader && !lowConfidence ? HeaderStyle
            : isText ? (lowConfidence ? LowConfidenceTextStyle : BodyStyle)
            : getFormatStyle(GetFormatCode(source), lowConfidence);
        var cell = new Cell
        {
            CellReference = reference,
            StyleIndex = style,
        };
        if (isText)
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
            // Decimal arithmetic keeps 17.38% as exactly 0.1738 instead of a binary-float tail.
            return (decimal.Parse(text.AsSpan(0, text.Length - 1), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture) / 100m).ToString(CultureInfo.InvariantCulture);
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

    // Dates keep the source separators and padding; numbers keep exactly the source's decimal places,
    // as a plain 数值/百分比 format without thousands separators.
    private static string GetFormatCode(DocumentTableCell source)
    {
        var text = source.Text.Trim();
        if (source.ValueKind == DocumentCellValueKind.Date)
        {
            var separator = text[4];
            var lastSeparator = text.LastIndexOf(separator);
            var month = new string('m', lastSeparator - 5);
            var day = new string('d', text.Length - lastSeparator - 1);
            return $"yyyy{separator}{month}{separator}{day}";
        }

        var number = text.TrimEnd('%');
        var point = number.IndexOf('.', StringComparison.Ordinal);
        var code = point < 0 ? "0" : "0." + new string('0', number.Length - point - 1);
        return source.ValueKind == DocumentCellValueKind.Percentage ? code + "%" : code;
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
                new FontName { Val = "宋体" }),
            new Font(
                new Bold(),
                new FontSize { Val = 11D },
                new Color { Rgb = "FFFFFFFF" },
                new FontName { Val = "宋体" }))
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
            CreateCellFormat(0, 3, 1, 0))
        {
            Count = 4U,
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

        var stylesheet = new Stylesheet(
            fonts,
            fills,
            borders,
            cellStyleFormats,
            cellFormats,
            cellStyles);
        if (inspectionReport)
        {
            stylesheet.NumberingFormats = new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "yyyy\"年\"m\"月\"d\"日\"" }) { Count = 1U };
            cellFormats.Append(
                CreateCellFormat(0, 0, 1, 164),
                CreateCellFormat(0, 0, 1, 0, HorizontalAlignmentValues.Center));
            cellFormats.Count = 6U;
        }

        return stylesheet;
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

    [GeneratedRegex(@"^[+-]?[0-9]+(?:\.[0-9]+)?%?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTextPattern();

    private static PdfTableWorkerException InvalidWorkbook(string message) =>
        new("XLSX_VALIDATION_FAILED", message);

    private sealed record PlacedTable(DocumentTable Table, int StartRow);

    private sealed record WorksheetPlan(
        string Name,
        IReadOnlyList<PlacedTable> Tables,
        int ColumnCount,
        InspectionReport? Report = null);
}
