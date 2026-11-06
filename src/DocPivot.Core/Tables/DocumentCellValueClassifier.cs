using System.Globalization;
using System.Text.RegularExpressions;

namespace DocPivot.Core.Tables;

public static partial class DocumentCellValueClassifier
{
    private const int MaximumSignificantDigits = 15;

    public static DocumentCellValueKind Classify(string text, string? columnHeader = null)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            columnHeader is not null && IdentifierHeaderPattern().IsMatch(columnHeader))
        {
            return DocumentCellValueKind.Text;
        }

        var value = text.Trim();
        if (IntegerPattern().IsMatch(value) && HasSupportedPrecision(value))
        {
            return DocumentCellValueKind.WholeNumber;
        }

        if (DecimalPattern().IsMatch(value) && HasSupportedPrecision(value))
        {
            return DocumentCellValueKind.FractionalNumber;
        }

        if (PercentagePattern().IsMatch(value) && HasSupportedPrecision(value))
        {
            return DocumentCellValueKind.Percentage;
        }

        return DateTime.TryParseExact(
            value,
            ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date) && date.Year >= 1900
                ? DocumentCellValueKind.Date
                : DocumentCellValueKind.Text;
    }

    private static bool HasSupportedPrecision(string value)
    {
        // Excel stores only fifteen significant digits; longer values must remain lossless text.
        var digits = string.Concat(value.Where(char.IsAsciiDigit)).TrimStart('0');
        return digits.Length <= MaximumSignificantDigits;
    }

    [GeneratedRegex(@"^[+-]?(?:0|[1-9][0-9]*|[1-9][0-9]{0,2}(?:,[0-9]{3})+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9][0-9]*|[1-9][0-9]{0,2}(?:,[0-9]{3})+)\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9][0-9]*|[1-9][0-9]{0,2}(?:,[0-9]{3})+)(?:\.[0-9]+)?%$", RegexOptions.CultureInvariant)]
    private static partial Regex PercentagePattern();

    [GeneratedRegex(@"编号|编码|代码|合同号|单号|账号|账户|身份证|电话|邮编|税号|\b(?:id|identifier|code|no\.?|phone|telephone|zip)\b|\b(?:contract|account|invoice|order|serial|reference)\s*(?:number|no\.?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierHeaderPattern();
}
