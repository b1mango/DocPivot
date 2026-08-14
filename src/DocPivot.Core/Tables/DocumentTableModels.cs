namespace DocPivot.Core.Tables;

public enum DocumentTableSourceKind
{
    DigitalText,
    OpticalCharacterRecognition,
}

public enum DocumentCellValueKind
{
    Text,
    WholeNumber,
    FractionalNumber,
    Percentage,
    Date,
}

public readonly record struct DocumentBounds(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public double Width => Math.Max(0, Right - Left);

    public double Height => Math.Max(0, Bottom - Top);

    public double CenterY => Top + (Height / 2);

    public double CenterX => Left + (Width / 2);

    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Right) &&
        double.IsFinite(Bottom) &&
        Right > Left &&
        Bottom > Top;

    public static DocumentBounds Union(IEnumerable<DocumentBounds> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var values = bounds.Where(static value => value.IsValid).ToArray();
        return values.Length == 0
            ? default
            : new DocumentBounds(
                values.Min(static value => value.Left),
                values.Min(static value => value.Top),
                values.Max(static value => value.Right),
                values.Max(static value => value.Bottom));
    }
}

public sealed record DocumentWord(
    string Text,
    DocumentBounds Bounds,
    double Confidence);

public sealed record DocumentTableCell(
    int RowIndex,
    int ColumnIndex,
    int RowSpan,
    int ColumnSpan,
    string Text,
    DocumentCellValueKind ValueKind,
    double Confidence,
    DocumentBounds Bounds);

public sealed record DocumentTable(
    int PageNumber,
    int TableIndex,
    int RowCount,
    int ColumnCount,
    DocumentBounds Bounds,
    DocumentTableSourceKind SourceKind,
    double Confidence,
    IReadOnlyList<DocumentTableCell> Cells,
    IReadOnlyList<string> Warnings);

public sealed record DocumentTablePage(
    int PageNumber,
    DocumentTableSourceKind SourceKind,
    double Width,
    double Height,
    IReadOnlyList<DocumentTable> Tables,
    IReadOnlyList<string> Warnings);

public sealed record DocumentTableExtraction(
    string SourceFileName,
    int PageCount,
    IReadOnlyList<DocumentTablePage> Pages)
{
    public IReadOnlyList<DocumentTable> Tables => Pages.SelectMany(static page => page.Tables).ToArray();

    public int DigitalPageCount => Pages.Count(static page => page.SourceKind == DocumentTableSourceKind.DigitalText);

    public int OcrPageCount => Pages.Count(
        static page => page.SourceKind == DocumentTableSourceKind.OpticalCharacterRecognition);

    public int LowConfidenceCellCount => Tables
        .SelectMany(static table => table.Cells)
        .Count(static cell => cell.Confidence < 0.75);
}
