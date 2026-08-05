using System.Globalization;
using DocPivot.Core.Pdf;

namespace DocPivot.Infrastructure.Pdf;

internal sealed record PdfSplitArtifactPlan(
    string DestinationPath,
    string PageExpression,
    int ExpectedPageCount,
    string RangeLabel);

internal sealed record PdfSplitPlan(
    bool UsesNativeSplit,
    int NativePagesPerFile,
    IReadOnlyList<PdfSplitArtifactPlan> Artifacts);

internal static class PdfSplitPlanner
{
    public static PdfSplitPlan Create(
        string outputPath,
        PdfSplitSelection selection,
        int totalPages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalPages, 1);

        var normalizedOutput = Path.GetFullPath(outputPath);
        return selection.Mode switch
        {
            PdfSplitMode.CustomRange => CreateSingle(
                normalizedOutput,
                PdfPageRangeParser.Parse(
                    selection.PageRange ?? throw new ArgumentException(
                        "A custom page range is required.",
                        nameof(selection)),
                    totalPages)),
            PdfSplitMode.EveryNPages => CreatePartitions(
                normalizedOutput,
                totalPages,
                selection.PagesPerFile),
            PdfSplitMode.EveryPage => CreatePartitions(normalizedOutput, totalPages, 1),
            PdfSplitMode.OddPages => CreateAlternating(normalizedOutput, totalPages, startPage: 1),
            PdfSplitMode.EvenPages => CreateAlternating(normalizedOutput, totalPages, startPage: 2),
            PdfSplitMode.VisualCuts => CreateVisualPartitions(
                normalizedOutput,
                totalPages,
                selection.CutAfterPages ?? throw new ArgumentException(
                    "Visual split cut positions are required.",
                    nameof(selection))),
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection.Mode, "Unknown split mode."),
        };
    }

    private static PdfSplitPlan CreateSingle(string outputPath, PdfPageSelection selection) =>
        new(
            false,
            0,
            [new PdfSplitArtifactPlan(
                outputPath,
                selection.NormalizedExpression,
                selection.Count,
                selection.NormalizedExpression)]);

    private static PdfSplitPlan CreatePartitions(
        string outputPath,
        int totalPages,
        int pagesPerFile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerFile, 1);
        var width = totalPages.ToString(CultureInfo.InvariantCulture).Length;
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(outputPath));
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);
        var artifacts = new List<PdfSplitArtifactPlan>();

        for (var start = 1; start <= totalPages; start += pagesPerFile)
        {
            var end = Math.Min(start + pagesPerFile - 1, totalPages);
            var expression = start == end ? $"{start}" : $"{start}-{end}";
            var rangeLabel = start == end
                ? start.ToString($"D{width}", CultureInfo.InvariantCulture)
                : $"{start.ToString($"D{width}", CultureInfo.InvariantCulture)}-{end.ToString($"D{width}", CultureInfo.InvariantCulture)}";
            artifacts.Add(new PdfSplitArtifactPlan(
                Path.Combine(directory, $"{stem}-{rangeLabel}{extension}"),
                expression,
                end - start + 1,
                rangeLabel));
        }

        return new PdfSplitPlan(true, pagesPerFile, artifacts);
    }

    private static PdfSplitPlan CreateAlternating(
        string outputPath,
        int totalPages,
        int startPage)
    {
        var pages = Enumerable.Range(1, totalPages)
            .Where(page => page >= startPage && (page - startPage) % 2 == 0)
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("The selected odd/even page set is empty.");
        }

        var expression = string.Join(',', pages.Select(static page =>
            page.ToString(CultureInfo.InvariantCulture)));
        return new PdfSplitPlan(
            false,
            0,
            [new PdfSplitArtifactPlan(outputPath, expression, pages.Length, expression)]);
    }

    private static PdfSplitPlan CreateVisualPartitions(
        string outputPath,
        int totalPages,
        IReadOnlyList<int> cutAfterPages)
    {
        ArgumentNullException.ThrowIfNull(cutAfterPages);
        var cuts = cutAfterPages
            .Distinct()
            .Order()
            .ToArray();
        if (cuts.Length == 0)
        {
            throw new ArgumentException("At least one visual split cut is required.", nameof(cutAfterPages));
        }

        if (cuts[0] < 1 || cuts[^1] >= totalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cutAfterPages),
                "Visual split cuts must be placed between existing pages.");
        }

        var width = totalPages.ToString(CultureInfo.InvariantCulture).Length;
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(outputPath));
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);
        var artifacts = new List<PdfSplitArtifactPlan>(cuts.Length + 1);
        var start = 1;
        foreach (var end in cuts.Append(totalPages))
        {
            var expression = start == end ? $"{start}" : $"{start}-{end}";
            var rangeLabel = start == end
                ? start.ToString($"D{width}", CultureInfo.InvariantCulture)
                : $"{start.ToString($"D{width}", CultureInfo.InvariantCulture)}-{end.ToString($"D{width}", CultureInfo.InvariantCulture)}";
            artifacts.Add(new PdfSplitArtifactPlan(
                Path.Combine(directory, $"{stem}-{rangeLabel}{extension}"),
                expression,
                end - start + 1,
                rangeLabel));
            start = end + 1;
        }

        return new PdfSplitPlan(false, 0, artifacts);
    }
}
