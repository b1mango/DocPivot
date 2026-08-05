using System.Globalization;

namespace DocPivot.Core.Pdf;

public static class PdfPageRangeParser
{
    public static PdfPageSelection Parse(string expression, int totalPages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalPages, 1);

        var pages = new List<int>();
        var seenPages = new HashSet<int>();
        foreach (var rawToken in expression.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                throw new FormatException("Page selections cannot contain an empty segment.");
            }

            var separatorIndex = token.IndexOf('-');
            if (separatorIndex < 0)
            {
                AddPage(ParsePageNumber(token, totalPages), pages, seenPages);
                continue;
            }

            if (separatorIndex == 0 || token.IndexOf('-', separatorIndex + 1) >= 0)
            {
                throw new FormatException($"'{token}' is not a valid page range.");
            }

            var start = ParsePageNumber(token[..separatorIndex].Trim(), totalPages);
            var endText = token[(separatorIndex + 1)..].Trim();
            var end = endText.Length == 0 ? totalPages : ParsePageNumber(endText, totalPages);
            if (end < start)
            {
                throw new FormatException($"The page range '{token}' is descending.");
            }

            for (var page = start; page <= end; page++)
            {
                AddPage(page, pages, seenPages);
            }
        }

        return new PdfPageSelection(pages, Normalize(pages));
    }

    private static int ParsePageNumber(string value, int totalPages)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
        {
            throw new FormatException($"'{value}' is not a valid page number.");
        }

        if (page < 1 || page > totalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                page,
                $"Page numbers must be between 1 and {totalPages}.");
        }

        return page;
    }

    private static void AddPage(int page, List<int> pages, HashSet<int> seenPages)
    {
        if (seenPages.Add(page))
        {
            pages.Add(page);
        }
    }

    private static string Normalize(List<int> pages)
    {
        var segments = new List<string>();
        for (var index = 0; index < pages.Count;)
        {
            var start = pages[index];
            var end = start;
            var nextIndex = index + 1;
            while (nextIndex < pages.Count && pages[nextIndex] == end + 1)
            {
                end = pages[nextIndex];
                nextIndex++;
            }

            segments.Add(start == end
                ? start.ToString(CultureInfo.InvariantCulture)
                : $"{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}");
            index = nextIndex;
        }

        return string.Join(',', segments);
    }
}
