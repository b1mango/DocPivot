using System.Globalization;
using System.Text.RegularExpressions;

namespace DocPivot.Core.Tables;

public static partial class DocumentCellValueClassifier
{
    public static DocumentCellValueKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DocumentCellValueKind.Text;
        }

        var value = text.Trim();
        if (IntegerPattern().IsMatch(value))
        {
            return DocumentCellValueKind.WholeNumber;
        }

        if (DecimalPattern().IsMatch(value))
        {
            return DocumentCellValueKind.FractionalNumber;
        }

        if (PercentagePattern().IsMatch(value))
        {
            return DocumentCellValueKind.Percentage;
        }

        return DateTime.TryParseExact(
            value,
            ["yyyy-M-d", "yyyy/M/d", "yyyy.MM.dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _)
                ? DocumentCellValueKind.Date
                : DocumentCellValueKind.Text;
    }

    [GeneratedRegex(@"^[+-]?(?:0|[1-9]\d{0,14}|[1-9]\d{0,2}(?:,\d{3})+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9]\d{0,14}|[1-9]\d{0,2}(?:,\d{3})+)\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9]\d{0,14})(?:\.\d+)?%$", RegexOptions.CultureInvariant)]
    private static partial Regex PercentagePattern();
}
