using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocPivot.Core.Tables;

public sealed record PdfPageTextAssessment(
    bool RequiresOcr,
    string Reason,
    int CharacterCount,
    double InvalidCharacterRatio,
    double TextCoverage,
    double LargeImageCoverage,
    double TextCoverageWithinImages);

public static partial class PdfPageTextQuality
{
    public const int MinimumCharacters = 8;
    public const double MaximumInvalidCharacterRatio = 0.1;
    public const double MinimumLargeImageAreaRatio = 0.2;
    public const double DominantImageCoverage = 0.55;
    public const double MinimumImageTextCoverage = 0.035;
    public const int MinimumImageCharacters = 80;
    public const double MinimumImageTextVerticalSpan = 0.35;

    public static PdfPageTextAssessment Evaluate(
        double pageWidth,
        double pageHeight,
        IReadOnlyList<DocumentWord> words,
        IReadOnlyList<DocumentBounds> images)
    {
        var pageArea = pageWidth * pageHeight;
        var pageBounds = new DocumentBounds(0, 0, pageWidth, pageHeight);
        var characters = 0;
        var invalidCharacters = 0;
        foreach (var word in words)
        {
            // PdfPig can expose missing mappings as CID labels instead of Unicode glyphs.
            var text = UnmappedCidPattern().Replace(word.Text, "\uFFFD");
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    continue;
                }

                characters++;
                var category = Rune.GetUnicodeCategory(rune);
                if (rune.Value is 0xFFFD or 0xFFFE or 0xFFFF or '?' ||
                    category is UnicodeCategory.Control or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                {
                    invalidCharacters++;
                }
            }
        }

        var invalidRatio = characters == 0 ? 0 : (double)invalidCharacters / characters;
        var textArea = words.Select(word => Intersect(word.Bounds, pageBounds))
            .Distinct().Sum(static bounds => bounds.Width * bounds.Height);
        var largeImages = images.Select(image => Intersect(image, pageBounds))
            .Where(image => image.Width * image.Height / pageArea >= MinimumLargeImageAreaRatio)
            .Distinct().ToArray();
        var imageArea = UnionArea(largeImages);
        var imageCoverage = imageArea / pageArea;
        var imageTextArea = 0d;
        var imageCharacters = 0;
        var imageTextBounds = new List<DocumentBounds>();
        foreach (var word in words)
        {
            var intersections = largeImages.Select(image => Intersect(word.Bounds, image))
                .Where(static bounds => bounds.IsValid).ToArray();
            if (intersections.Length == 0)
            {
                continue;
            }

            imageTextArea += UnionArea(intersections);
            imageCharacters += word.Text.Count(static character => !char.IsWhiteSpace(character));
            imageTextBounds.AddRange(intersections);
        }

        var imageTextCoverage = imageArea == 0 ? 0 : Math.Min(1, imageTextArea / imageArea);
        var imageHeight = DocumentBounds.Union(largeImages).Height;
        var imageTextSpan = imageHeight == 0 ? 0 : DocumentBounds.Union(imageTextBounds).Height / imageHeight;
        var reason = characters < MinimumCharacters ? "INSUFFICIENT_TEXT" :
            invalidRatio >= MaximumInvalidCharacterRatio ? "UNMAPPED_OR_INVALID_TEXT" :
            imageCoverage >= DominantImageCoverage &&
            (imageTextCoverage < MinimumImageTextCoverage || imageCharacters < MinimumImageCharacters ||
             imageTextSpan < MinimumImageTextVerticalSpan) ? "IMAGE_BODY_WITH_SPARSE_TEXT" :
            "RELIABLE_TEXT";

        return new PdfPageTextAssessment(
            reason != "RELIABLE_TEXT", reason, characters, invalidRatio,
            Math.Min(1, textArea / pageArea), imageCoverage, imageTextCoverage);
    }

    private static DocumentBounds Intersect(DocumentBounds left, DocumentBounds right) => new(
        Math.Max(left.Left, right.Left), Math.Max(left.Top, right.Top),
        Math.Min(left.Right, right.Right), Math.Min(left.Bottom, right.Bottom));

    private static double UnionArea(IReadOnlyList<DocumentBounds> bounds)
    {
        var edges = bounds.SelectMany(static rectangle => new[] { rectangle.Left, rectangle.Right })
            .Distinct().Order().ToArray();
        var area = 0d;
        for (var index = 1; index < edges.Length; index++)
        {
            var intervals = bounds.Where(rectangle => rectangle.Left < edges[index] && rectangle.Right > edges[index - 1])
                .OrderBy(static rectangle => rectangle.Top).ToArray();
            var bottom = double.NegativeInfinity;
            var height = 0d;
            foreach (var interval in intervals)
            {
                height += Math.Max(0, interval.Bottom - Math.Max(bottom, interval.Top));
                bottom = Math.Max(bottom, interval.Bottom);
            }

            area += height * (edges[index] - edges[index - 1]);
        }

        return area;
    }

    [GeneratedRegex(@"(?:\(cid:\d+\)|\bcid\d+\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnmappedCidPattern();
}
