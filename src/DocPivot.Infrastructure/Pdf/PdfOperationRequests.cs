namespace DocPivot.Infrastructure.Pdf;

public sealed record PdfPreflightRequest(
    string InputPath,
    string? Password = null);

public sealed record PdfMergeRequest(
    IReadOnlyList<string> InputPaths,
    string OutputPath,
    string? Password = null);

public sealed record PdfSplitRequest(
    string InputPath,
    string OutputPath,
    PdfSplitSelection Selection,
    string? Password = null);

public sealed record PdfOptimizeRequest(
    string InputPath,
    string OutputPath,
    int CompressionStrength = 0,
    string? Password = null);

public enum PdfSplitMode
{
    CustomRange,
    EveryNPages,
    EveryPage,
    OddPages,
    EvenPages,
    VisualCuts,
}

public sealed record PdfSplitSelection(
    PdfSplitMode Mode,
    string? PageRange = null,
    int PagesPerFile = 0,
    IReadOnlyList<int>? CutAfterPages = null)
{
    public static PdfSplitSelection CustomRange(string pageRange) =>
        new(PdfSplitMode.CustomRange, pageRange);

    public static PdfSplitSelection EveryNPages(int pagesPerFile) =>
        new(PdfSplitMode.EveryNPages, PagesPerFile: pagesPerFile);

    public static PdfSplitSelection EveryPage() =>
        new(PdfSplitMode.EveryPage);

    public static PdfSplitSelection OddPages() =>
        new(PdfSplitMode.OddPages);

    public static PdfSplitSelection EvenPages() =>
        new(PdfSplitMode.EvenPages);

    public static PdfSplitSelection VisualCuts(IReadOnlyList<int> cutAfterPages) =>
        new(PdfSplitMode.VisualCuts, CutAfterPages: cutAfterPages);
}
