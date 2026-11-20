using System.Text.RegularExpressions;

namespace DocPivot.Core.Tables;

public sealed partial record InspectionReport(
    DocumentTable Body,
    IReadOnlyList<DocumentTable> OtherTables,
    IReadOnlyDictionary<string, string> Fields)
{
    public static IReadOnlyList<string> Labels { get; } = Array.AsReadOnly<string>(
        ["报告编号", "工作单号", "申请方名称", "申请方地址", "据称样品名称", "样品接收日期", "样品测试周期", "授权签字人"]);

    private static readonly Regex FieldPattern = new(string.Join('|', Labels.Append("备注").Select(
        label => string.Join(@"\s*", label.ToCharArray()))), RegexOptions.CultureInvariant);

    public static InspectionReport? Detect(IReadOnlyList<DocumentTablePage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, value) in pages.SelectMany(static page => page.ReportFields))
        {
            if (!fields.TryGetValue(label, out var existing) || existing.Length == 0)
            {
                fields[label] = value;
            }
        }

        if (Labels.Count(fields.ContainsKey) < 3)
        {
            return null;
        }

        var ruledPages = pages.Select(page => page with
        {
            Tables = page.Tables.Where(static table => table.IsRuled).ToArray(),
        }).ToArray();
        var ruledTables = TableContinuationMerger.Merge(ruledPages);
        var body = ruledTables
            .Where(static table => table.RowCount > table.HeaderRowCount && table.ColumnCount >= 2)
            .MaxBy(static table => table.Cells.Count);
        return body is null
            ? null
            : new InspectionReport(body, ruledTables.Where(table => !ReferenceEquals(table, body)).ToArray(), fields);
    }

    internal static Dictionary<string, string> ReadFields((string Text, DocumentBounds Bounds)[] rows)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Length; index++)
        {
            var (text, bounds) = rows[index];
            var matches = FieldPattern.Matches(text);
            if (matches.Count == 0 || matches[0].Index != 0)
            {
                continue;
            }

            for (var field = 0; field < matches.Count; field++)
            {
                var match = matches[field];
                var label = RemoveSpaces(match.Value);
                var end = field + 1 < matches.Count ? matches[field + 1].Index : text.Length;
                var value = text[(match.Index + match.Length)..end].TrimStart(' ', ':', '：').TrimEnd();
                if (label == "授权签字人")
                {
                    var date = ChineseDate().Match(RemoveSpaces(value));
                    if (!date.Success && index + 1 < rows.Length)
                    {
                        var next = rows[index + 1];
                        if (next.Bounds.Top - bounds.Bottom <= bounds.Height * 2 &&
                            next.Bounds.Left <= bounds.Right && next.Bounds.Right >= bounds.Left)
                        {
                            date = ChineseDate().Match(RemoveSpaces(next.Text));
                        }
                    }

                    value = date.Value;
                }
                else if (label is "样品接收日期" or "样品测试周期")
                {
                    value = RemoveSpaces(value).Replace('～', '~');
                }

                fields[label] = value;
            }
        }

        return fields;
    }

    private static string RemoveSpaces(string text) => string.Concat(text.Where(static ch => !char.IsWhiteSpace(ch)));

    [GeneratedRegex(@"[0-9]{4}年[0-9]{1,2}月[0-9]{1,2}日")]
    private static partial Regex ChineseDate();
}
