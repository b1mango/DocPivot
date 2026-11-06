namespace DocPivot.Core.Tables;

public static class TableContinuationMerger
{
    private const double ColumnAlignmentTolerance = 0.035;

    public static IReadOnlyList<DocumentTable> Merge(IReadOnlyList<DocumentTablePage> pages)
    {
        var tables = new List<DocumentTable>();
        DocumentTablePage? previousPage = null;
        foreach (var page in pages)
        {
            for (var index = 0; index < page.Tables.Count; index++)
            {
                var table = page.Tables[index];
                if (index == 0 && previousPage is { Tables.Count: > 0 } &&
                    page.PageNumber == previousPage.PageNumber + 1 &&
                    IsContinuation(previousPage.Tables[^1], previousPage.Width, table, page.Width))
                {
                    tables[^1] = Append(tables[^1], table);
                }
                else
                {
                    tables.Add(table);
                }
            }

            previousPage = page;
        }

        return tables;
    }

    private static bool IsContinuation(
        DocumentTable previous,
        double previousPageWidth,
        DocumentTable current,
        double currentPageWidth)
    {
        if (previous.HeaderRowCount == 0 ||
            previous.HeaderRowCount != current.HeaderRowCount ||
            previous.ColumnCount != current.ColumnCount ||
            previous.RowCount <= previous.HeaderRowCount ||
            current.RowCount <= current.HeaderRowCount)
        {
            return false;
        }

        var previousHeader = HeaderCells(previous);
        var currentHeader = HeaderCells(current);
        if (previousHeader.Length < 2 || previousHeader.Length != currentHeader.Length)
        {
            return false;
        }

        for (var index = 0; index < previousHeader.Length; index++)
        {
            var left = previousHeader[index];
            var right = currentHeader[index];
            if (left.RowIndex != right.RowIndex || left.ColumnIndex != right.ColumnIndex ||
                left.RowSpan != right.RowSpan || left.ColumnSpan != right.ColumnSpan ||
                left.RowIndex + left.RowSpan > previous.HeaderRowCount ||
                !string.Equals(NormalizeHeader(left.Text), NormalizeHeader(right.Text), StringComparison.Ordinal) ||
                Math.Abs(left.Bounds.Left / previousPageWidth - right.Bounds.Left / currentPageWidth) >
                    ColumnAlignmentTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static DocumentTableCell[] HeaderCells(DocumentTable table) =>
        table.Cells
            .Where(cell => cell.RowIndex < table.HeaderRowCount)
            .OrderBy(static cell => cell.RowIndex)
            .ThenBy(static cell => cell.ColumnIndex)
            .ToArray();

    private static string NormalizeHeader(string text) =>
        string.Concat(text.Where(static character => !char.IsWhiteSpace(character)));

    private static DocumentTable Append(DocumentTable previous, DocumentTable current)
    {
        var rowOffset = previous.RowCount - current.HeaderRowCount;
        var cells = previous.Cells.Concat(current.Cells
            .Where(cell => cell.RowIndex >= current.HeaderRowCount)
            .Select(cell => cell with { RowIndex = checked(cell.RowIndex + rowOffset) }))
            .ToArray();
        return previous with
        {
            RowCount = checked(previous.RowCount + current.RowCount - current.HeaderRowCount),
            SourceKind = previous.SourceKind == DocumentTableSourceKind.OpticalCharacterRecognition ||
                current.SourceKind == DocumentTableSourceKind.OpticalCharacterRecognition
                    ? DocumentTableSourceKind.OpticalCharacterRecognition
                    : DocumentTableSourceKind.DigitalText,
            Confidence = Math.Min(previous.Confidence, current.Confidence),
            Cells = cells,
            Warnings = previous.Warnings.Concat(current.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }
}
