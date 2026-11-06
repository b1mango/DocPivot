using DocPivot.Core.Tables;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;

namespace DocPivot.PdfWorker;

/// <summary>
/// Page content in top-left-origin PDF points: the text layer, image placements and table rules.
/// </summary>
public sealed record PdfPageContent(
    IReadOnlyList<DocumentWord> Words,
    IReadOnlyList<DocumentBounds> Images,
    IReadOnlyList<DocumentLine> Lines);

public static class PdfPageContentReader
{
    private const int MaximumLinesPerPage = 20_000;
    private const double AxisTolerance = 1;
    private const double MinimumRuleLength = 8;
    private const double MaximumFilledRuleThickness = 3;

    public static PdfPageContent Read(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var pageHeight = (double)page.Height;
        return new PdfPageContent(
            ReadWords(page, pageHeight),
            ReadImages(page, pageHeight),
            ReadLines(page, pageHeight));
    }

    private static DocumentWord[] ReadWords(Page page, double pageHeight)
    {
        var letters = ReadLetters(page, pageHeight)
            .OrderBy(static letter => letter.Bounds.CenterY)
            .ThenBy(static letter => letter.Bounds.Left)
            .ToArray();
        if (letters.Length == 0)
        {
            return [];
        }

        var medianHeight = Median(letters.Select(static letter => letter.Bounds.Height));
        var rows = new List<List<DigitalLetter>>();
        foreach (var letter in letters)
        {
            var row = rows.LastOrDefault(candidate =>
                Math.Abs(candidate[0].Bounds.CenterY - letter.Bounds.CenterY) <= medianHeight * 0.65);
            if (row is null)
            {
                rows.Add([letter]);
            }
            else
            {
                row.Add(letter);
            }
        }

        var words = new List<DocumentWord>();
        foreach (var row in rows.OrderBy(static row => row.Min(letter => letter.Bounds.Top)))
        {
            var ordered = row.OrderBy(static letter => letter.Bounds.Left).ToArray();
            var medianCharacterWidth = Median(ordered.Select(static letter => letter.Bounds.Width));
            var gapThreshold = Math.Max(medianHeight * 0.85, medianCharacterWidth * 1.55);
            var segment = new List<DigitalLetter> { ordered[0] };
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].Bounds.Left - ordered[index - 1].Bounds.Right > gapThreshold)
                {
                    words.Add(CreateWord(segment));
                    segment = [];
                }

                segment.Add(ordered[index]);
            }

            words.Add(CreateWord(segment));
        }

        return words.ToArray();

        static DocumentWord CreateWord(IReadOnlyList<DigitalLetter> segment)
        {
            var text = new System.Text.StringBuilder();
            foreach (var letter in segment)
            {
                if (letter.SpaceBefore && text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(letter.Text);
            }

            return new DocumentWord(
                text.ToString(),
                DocumentBounds.Union(segment.Select(static letter => letter.Bounds)),
                1);
        }
    }

    /// <summary>
    /// Keeps the word spaces of the content stream: a space glyph, or a positioning gap between
    /// Latin glyphs on one baseline, marks the next glyph as starting a new word inside a cell.
    /// </summary>
    private static List<DigitalLetter> ReadLetters(Page page, double pageHeight)
    {
        var letters = new List<DigitalLetter>();
        Letter? previous = null;
        var pendingSpace = false;
        foreach (var letter in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                pendingSpace = true;
                continue;
            }

            var bounds = ToDocumentBounds(letter.BoundingBox, pageHeight);
            if (!bounds.IsValid)
            {
                continue;
            }

            var spaceBefore = previous is not null && SameBaseline(previous, letter) &&
                (pendingSpace || HasWordGap(previous, letter));
            letters.Add(new DigitalLetter(letter.Value, bounds, spaceBefore));
            previous = letter;
            pendingSpace = false;
        }

        return letters;
    }

    private static bool SameBaseline(Letter previous, Letter current)
    {
        // Stream order only describes reading order when the next glyph continues to the right.
        var size = Math.Max(previous.BoundingBox.Height, current.BoundingBox.Height);
        return Math.Abs(previous.StartBaseLine.Y - current.StartBaseLine.Y) <= Math.Max(0.5, size * 0.3) &&
            current.StartBaseLine.X >= previous.EndBaseLine.X - Math.Max(0.5, size * 0.2);
    }

    private static bool HasWordGap(Letter previous, Letter current)
    {
        if (!IsLatin(previous.Value[^1]) || !IsLatin(current.Value[0]))
        {
            return false;
        }

        var gap = current.StartBaseLine.X - previous.EndBaseLine.X;
        var size = Math.Max(previous.BoundingBox.Height, current.BoundingBox.Height);
        return gap > Math.Max(0.5, size * 0.25);

        static bool IsLatin(char value) => value < 0x2E80;
    }

    private static DocumentBounds[] ReadImages(Page page, double pageHeight)
    {
        if (page.NumberOfImages == 0)
        {
            return [];
        }

        try
        {
            return page.GetImages()
                .Select(image => ToDocumentBounds(image.BoundingBox, pageHeight))
                .Where(static bounds => bounds.IsValid)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An image we cannot place is treated as a full-page image, so the OCR decision stays conservative.
            return [new DocumentBounds(0, 0, (double)page.Width, pageHeight)];
        }
    }

    private static DocumentLine[] ReadLines(Page page, double pageHeight)
    {
        var lines = new List<DocumentLine>();
        try
        {
            foreach (var path in page.Paths)
            {
                if (path.IsClipping || (!path.IsStroked && !path.IsFilled))
                {
                    continue;
                }

                var stroked = path.IsStroked && !IsWhite(path.StrokeColor);
                var filled = path.IsFilled && !IsWhite(path.FillColor);
                foreach (var subpath in path)
                {
                    if (stroked)
                    {
                        AddStrokedSegments(subpath, pageHeight, lines);
                    }
                    else if (filled)
                    {
                        AddFilledRule(subpath, pageHeight, lines);
                    }

                    if (lines.Count > MaximumLinesPerPage)
                    {
                        // Dense vector art is not a table grid; borderless analysis is the safer fallback.
                        return [];
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }

        return lines.ToArray();
    }

    private static void AddStrokedSegments(PdfSubpath subpath, double pageHeight, List<DocumentLine> lines)
    {
        PdfPoint? current = null;
        PdfPoint? start = null;
        foreach (var command in subpath.Commands)
        {
            switch (command)
            {
                case PdfSubpath.Move move:
                    current = move.Location;
                    start = move.Location;
                    break;
                case PdfSubpath.Line line:
                    AddAxisLine(line.From, line.To, pageHeight, lines);
                    current = line.To;
                    break;
                case PdfSubpath.BezierCurve curve:
                    current = curve.EndPoint;
                    break;
                case PdfSubpath.Close when current is { } from && start is { } to:
                    AddAxisLine(from, to, pageHeight, lines);
                    current = start;
                    break;
            }
        }
    }

    private static void AddFilledRule(PdfSubpath subpath, double pageHeight, List<DocumentLine> lines)
    {
        // Many generators draw table rules as thin filled rectangles instead of stroked lines.
        if (subpath.Commands.Any(static command => command is PdfSubpath.BezierCurve))
        {
            return;
        }

        var rectangle = subpath.GetBoundingRectangle();
        if (rectangle is not { } box)
        {
            return;
        }

        var width = box.Right - box.Left;
        var height = box.Top - box.Bottom;
        var centerY = pageHeight - ((box.Top + box.Bottom) / 2);
        var centerX = (box.Left + box.Right) / 2;
        if (height <= MaximumFilledRuleThickness && width >= MinimumRuleLength && width > height * 4)
        {
            lines.Add(new DocumentLine(box.Left, centerY, box.Right, centerY));
        }
        else if (width <= MaximumFilledRuleThickness && height >= MinimumRuleLength && height > width * 4)
        {
            lines.Add(new DocumentLine(centerX, pageHeight - box.Top, centerX, pageHeight - box.Bottom));
        }
    }

    private static void AddAxisLine(PdfPoint from, PdfPoint to, double pageHeight, List<DocumentLine> lines)
    {
        var deltaX = Math.Abs(to.X - from.X);
        var deltaY = Math.Abs(to.Y - from.Y);
        if (deltaY <= AxisTolerance && deltaX >= MinimumRuleLength)
        {
            var y = pageHeight - ((from.Y + to.Y) / 2);
            lines.Add(new DocumentLine(Math.Min(from.X, to.X), y, Math.Max(from.X, to.X), y));
        }
        else if (deltaX <= AxisTolerance && deltaY >= MinimumRuleLength)
        {
            var x = (from.X + to.X) / 2;
            lines.Add(new DocumentLine(x, pageHeight - Math.Max(from.Y, to.Y), x, pageHeight - Math.Min(from.Y, to.Y)));
        }
    }

    private static bool IsWhite(IColor? color)
    {
        if (color is null)
        {
            return false;
        }

        var (red, green, blue) = color.ToRGBValues();
        return red > 0.95 && green > 0.95 && blue > 0.95;
    }

    private static DocumentBounds ToDocumentBounds(PdfRectangle bounds, double pageHeight) =>
        new(bounds.Left, pageHeight - bounds.Top, bounds.Right, pageHeight - bounds.Bottom);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(static value => double.IsFinite(value) && value > 0).Order().ToArray();
        if (ordered.Length == 0)
        {
            return 1;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private sealed record DigitalLetter(string Text, DocumentBounds Bounds, bool SpaceBefore);
}
