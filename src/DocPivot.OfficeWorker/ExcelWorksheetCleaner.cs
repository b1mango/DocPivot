using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

internal static class ExcelWorksheetCleaner
{
    private const int ExcelFormulas = -4123;
    private const int ExcelPart = 2;
    private const int ExcelByRows = 1;
    private const int ExcelByColumns = 2;
    private const int ExcelPrevious = 2;
    private const int ExcelAllFormatConditions = -4172;
    private const int ExcelAllValidation = -4174;

    public static ExcelWorksheetCleanupResult Clean(object worksheetValue, bool skipUnsafeWorksheet)
    {
        dynamic worksheet = worksheetValue;
        dynamic? cells = null;
        dynamic? firstCell = null;
        dynamic? lastRowCell = null;
        dynamic? lastColumnCell = null;
        try
        {
            cells = worksheet.Cells;
            firstCell = cells[1, 1];
            lastRowCell = cells.Find(
                What: "*",
                After: firstCell,
                LookIn: ExcelFormulas,
                LookAt: ExcelPart,
                SearchOrder: ExcelByRows,
                SearchDirection: ExcelPrevious,
                MatchCase: false,
                MatchByte: false,
                SearchFormat: false);
            lastColumnCell = cells.Find(
                What: "*",
                After: firstCell,
                LookIn: ExcelFormulas,
                LookAt: ExcelPart,
                SearchOrder: ExcelByColumns,
                SearchDirection: ExcelPrevious,
                MatchCase: false,
                MatchByte: false,
                SearchFormat: false);

            var lastRow = lastRowCell is null ? 1 : (int)lastRowCell.Row;
            var lastColumn = lastColumnCell is null ? 1 : (int)lastColumnCell.Column;
            if (HasMeaningfulContentBeyond(worksheet, lastRow, lastColumn))
            {
                if (skipUnsafeWorksheet)
                {
                    return ExcelWorksheetCleanupResult.Skipped(
                        "有效数据边界外仍存在对象、规则、名称或打印范围");
                }

                throw new OfficeWorkerException(
                    "EXCEL_COMPRESSION_UNSAFE",
                    "A worksheet contains meaningful objects beyond its value and formula boundary.");
            }

            return DeleteTrailingRowsAndColumns(worksheet, lastRow, lastColumn)
                ? ExcelWorksheetCleanupResult.Cleaned
                : ExcelWorksheetCleanupResult.Unchanged;
        }
        finally
        {
            ComObject.FinalRelease(lastColumnCell);
            ComObject.FinalRelease(lastRowCell);
            ComObject.FinalRelease(firstCell);
            ComObject.FinalRelease(cells);
        }
    }

    private static bool HasMeaningfulContentBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? usedRange = null;
        try
        {
            usedRange = worksheet.UsedRange;
            if (RangeExtendsBeyond(usedRange, lastRow, lastColumn))
            {
                object? mergeCells = usedRange.MergeCells;
                if (mergeCells is not bool hasMergedCells || hasMergedCells)
                {
                    return true;
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(usedRange);
        }

        return ShapesExtendBeyond(worksheet, lastRow, lastColumn) ||
            ListObjectsExtendBeyond(worksheet, lastRow, lastColumn) ||
            PivotTablesExtendBeyond(worksheet, lastRow, lastColumn) ||
            QueryTablesExtendBeyond(worksheet, lastRow, lastColumn) ||
            CommentsExtendBeyond(worksheet, lastRow, lastColumn) ||
            HyperlinksExtendBeyond(worksheet, lastRow, lastColumn) ||
            NamesExtendBeyond(worksheet, lastRow, lastColumn) ||
            WorkbookNamesExtendBeyond(worksheet, lastRow, lastColumn);
    }

    private static bool ShapesExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? shapes = null;
        try
        {
            shapes = worksheet.Shapes;
            for (var index = 1; index <= (int)shapes.Count; index++)
            {
                dynamic? shape = null;
                dynamic? topLeft = null;
                dynamic? bottomRight = null;
                try
                {
                    shape = shapes[index];
                    topLeft = shape.TopLeftCell;
                    bottomRight = shape.BottomRightCell;
                    if ((int)topLeft.Row > lastRow ||
                        (int)topLeft.Column > lastColumn ||
                        (int)bottomRight.Row > lastRow ||
                        (int)bottomRight.Column > lastColumn)
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(bottomRight);
                    ComObject.FinalRelease(topLeft);
                    ComObject.FinalRelease(shape);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(shapes);
        }
    }

    private static bool ListObjectsExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? listObjects = null;
        try
        {
            listObjects = worksheet.ListObjects;
            for (var index = 1; index <= (int)listObjects.Count; index++)
            {
                dynamic? listObject = null;
                dynamic? range = null;
                try
                {
                    listObject = listObjects[index];
                    range = listObject.Range;
                    if (RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(listObject);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(listObjects);
        }
    }

    private static bool PivotTablesExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? pivotTables = null;
        try
        {
            pivotTables = worksheet.PivotTables();
            for (var index = 1; index <= (int)pivotTables.Count; index++)
            {
                dynamic? pivotTable = null;
                dynamic? range = null;
                try
                {
                    pivotTable = pivotTables[index];
                    range = pivotTable.TableRange2;
                    if (RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(pivotTable);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(pivotTables);
        }
    }

    private static bool QueryTablesExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? queryTables = null;
        try
        {
            queryTables = worksheet.QueryTables;
            for (var index = 1; index <= (int)queryTables.Count; index++)
            {
                dynamic? queryTable = null;
                dynamic? range = null;
                try
                {
                    queryTable = queryTables[index];
                    range = queryTable.ResultRange;
                    if (RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(queryTable);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(queryTables);
        }
    }

    private static bool CommentsExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? comments = null;
        try
        {
            comments = worksheet.Comments;
            for (var index = 1; index <= (int)comments.Count; index++)
            {
                dynamic? comment = null;
                dynamic? parent = null;
                try
                {
                    comment = comments[index];
                    parent = comment.Parent;
                    if ((int)parent.Row > lastRow || (int)parent.Column > lastColumn)
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(parent);
                    ComObject.FinalRelease(comment);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(comments);
        }
    }

    private static bool HyperlinksExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? hyperlinks = null;
        try
        {
            hyperlinks = worksheet.Hyperlinks;
            for (var index = 1; index <= (int)hyperlinks.Count; index++)
            {
                dynamic? hyperlink = null;
                dynamic? range = null;
                try
                {
                    hyperlink = hyperlinks[index];
                    range = hyperlink.Range;
                    if (RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(hyperlink);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(hyperlinks);
        }
    }

    private static bool NamesExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? names = null;
        try
        {
            names = worksheet.Names;
            for (var index = 1; index <= (int)names.Count; index++)
            {
                dynamic? name = null;
                dynamic? range = null;
                try
                {
                    name = names[index];
                    if (IsLayoutName((string)name.Name))
                    {
                        continue;
                    }

                    range = name.RefersToRange;
                    if (RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                catch (COMException)
                {
                    return true;
                }
                finally
                {
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(name);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(names);
        }
    }

    private static bool WorkbookNamesExtendBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? workbook = null;
        dynamic? names = null;
        try
        {
            workbook = worksheet.Parent;
            names = workbook.Names;
            for (var index = 1; index <= (int)names.Count; index++)
            {
                dynamic? name = null;
                dynamic? range = null;
                dynamic? rangeWorksheet = null;
                try
                {
                    name = names[index];
                    if (IsLayoutName((string)name.Name))
                    {
                        continue;
                    }

                    range = name.RefersToRange;
                    rangeWorksheet = range.Worksheet;
                    if (string.Equals(
                            (string)rangeWorksheet.Name,
                            (string)worksheet.Name,
                            StringComparison.Ordinal) &&
                        RangeExtendsBeyond(range, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                catch (COMException)
                {
                    return true;
                }
                finally
                {
                    // Parent RCWs can alias caller-owned workbook and worksheet objects.
                    rangeWorksheet = null;
                    ComObject.FinalRelease(range);
                    ComObject.FinalRelease(name);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(names);
            workbook = null;
        }
    }

    private static bool PrintAreaExtendsBeyond(dynamic worksheet, int lastRow, int lastColumn)
    {
        dynamic? pageSetup = null;
        dynamic? printRange = null;
        try
        {
            pageSetup = worksheet.PageSetup;
            var printArea = (string)pageSetup.PrintArea;
            if (string.IsNullOrWhiteSpace(printArea))
            {
                return false;
            }

            printRange = worksheet.Range[printArea];
            return RangeExtendsBeyond(printRange, lastRow, lastColumn);
        }
        finally
        {
            ComObject.FinalRelease(printRange);
            ComObject.FinalRelease(pageSetup);
        }
    }

    private static bool IsLayoutName(string name) =>
        name.Contains("_xlnm.Print_Area", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("_xlnm.Print_Titles", StringComparison.OrdinalIgnoreCase);

    private static bool SpecialCellsExtendBeyond(
        dynamic worksheet,
        int cellType,
        int lastRow,
        int lastColumn)
    {
        dynamic? cells = null;
        dynamic? specialCells = null;
        dynamic? areas = null;
        try
        {
            cells = worksheet.Cells;
            try
            {
                specialCells = cells.SpecialCells(cellType);
            }
            catch (COMException)
            {
                return false;
            }

            areas = specialCells.Areas;
            for (var index = 1; index <= (int)areas.Count; index++)
            {
                dynamic? area = null;
                try
                {
                    area = areas[index];
                    if (RangeExtendsBeyond(area, lastRow, lastColumn))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObject.FinalRelease(area);
                }
            }

            return false;
        }
        finally
        {
            ComObject.FinalRelease(areas);
            ComObject.FinalRelease(specialCells);
            ComObject.FinalRelease(cells);
        }
    }

    private static bool RangeExtendsBeyond(dynamic range, int lastRow, int lastColumn)
    {
        dynamic? rows = null;
        dynamic? columns = null;
        try
        {
            rows = range.Rows;
            columns = range.Columns;
            var bottom = (int)range.Row + (int)rows.Count - 1;
            var right = (int)range.Column + (int)columns.Count - 1;
            return bottom > lastRow || right > lastColumn;
        }
        finally
        {
            ComObject.FinalRelease(columns);
            ComObject.FinalRelease(rows);
        }
    }

    private static bool DeleteTrailingRowsAndColumns(dynamic worksheet, int lastRow, int lastColumn)
    {
        var changed = false;
        dynamic? rows = null;
        dynamic? trailingRows = null;
        dynamic? columns = null;
        dynamic? firstTrailingColumn = null;
        dynamic? lastWorksheetColumn = null;
        dynamic? trailingColumns = null;
        try
        {
            rows = worksheet.Rows;
            var rowCount = (int)rows.Count;
            if (lastRow < rowCount)
            {
                trailingRows = rows[$"{lastRow + 1}:{rowCount}"];
                trailingRows.Delete();
                changed = true;
            }

            columns = worksheet.Columns;
            var columnCount = (int)columns.Count;
            if (lastColumn < columnCount)
            {
                firstTrailingColumn = columns[lastColumn + 1];
                lastWorksheetColumn = columns[columnCount];
                trailingColumns = worksheet.Range[firstTrailingColumn, lastWorksheetColumn];
                trailingColumns.Delete();
                changed = true;
            }

            return changed;
        }
        finally
        {
            ComObject.FinalRelease(trailingColumns);
            ComObject.FinalRelease(lastWorksheetColumn);
            ComObject.FinalRelease(firstTrailingColumn);
            ComObject.FinalRelease(columns);
            ComObject.FinalRelease(trailingRows);
            ComObject.FinalRelease(rows);
        }
    }
}
