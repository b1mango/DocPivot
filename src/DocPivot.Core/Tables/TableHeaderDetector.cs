namespace DocPivot.Core.Tables;

internal static class TableHeaderDetector
{
    private static readonly HashSet<string> HeaderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "item", "description", "quantity", "qty", "count", "amount", "total", "price", "rate",
        "date", "code", "id", "no", "no.", "number", "unit", "value", "status", "category", "contract no",
        "名称", "项目", "品名", "规格", "数量", "单位", "单价", "金额", "税率", "日期", "编号", "合同号",
        "序号", "代码", "编码", "内容", "备注", "合计", "检测项目", "检测依据", "结果",
    };

    public static DocumentTable Apply(DocumentTable table)
    {
        if (table.ColumnCount < 2 || table.RowCount < 2)
        {
            return table;
        }

        var headerRows = DetectHeaderRows(table);
        if (headerRows == 0)
        {
            return table;
        }

        var headers = table.Cells.Where(cell => cell.RowIndex < headerRows).ToArray();
        var cells = table.Cells.Select(cell => cell.RowIndex < headerRows
            ? cell with { ValueKind = DocumentCellValueKind.Text }
            : cell with
            {
                ValueKind = DocumentCellValueClassifier.Classify(cell.Text, string.Join(' ',
                    headers.Where(header => header.ColumnIndex <= cell.ColumnIndex &&
                            header.ColumnIndex + header.ColumnSpan > cell.ColumnIndex)
                        .OrderBy(static header => header.RowIndex).Select(static header => header.Text))),
            }).ToArray();
        return table with { HeaderRowCount = headerRows, Cells = cells };
    }

    private static int DetectHeaderRows(DocumentTable table)
    {
        var headerRows = 0;
        for (var row = 0; row < Math.Min(3, table.RowCount - 1); row++)
        {
            var cells = table.Cells.Where(cell => cell.RowIndex == row && cell.Text.Length > 0).ToArray();
            if (cells.Length == 0 || cells.Any(static cell => cell.ValueKind != DocumentCellValueKind.Text))
            {
                break;
            }

            var knownLabels = cells.Count(static cell => HeaderLabels.Contains(cell.Text.Trim()));
            var nextRow = table.Cells.Where(cell => cell.RowIndex == row + 1 && cell.Text.Length > 0).ToArray();
            var typedColumns = cells.Count(cell => nextRow.Any(next =>
                next.ColumnIndex >= cell.ColumnIndex && next.ColumnIndex < cell.ColumnIndex + cell.ColumnSpan &&
                next.ValueKind != DocumentCellValueKind.Text));
            var mergedHeading = cells.Any(static cell => cell.ColumnSpan > 1 || cell.RowSpan > 1) &&
                nextRow.Any(static cell => HeaderLabels.Contains(cell.Text.Trim()));
            if (knownLabels < Math.Min(2, cells.Length) && typedColumns == 0 && !mergedHeading)
            {
                break;
            }

            headerRows = Math.Max(row + 1, cells.Max(static cell => cell.RowIndex + cell.RowSpan));
            if (!mergedHeading)
            {
                break;
            }
        }

        return Math.Min(headerRows, table.RowCount - 1);
    }
}
