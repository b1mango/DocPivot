using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocPivot.Infrastructure.Diagnostics;

internal static partial class DiagnosticRedactor
{
    private const int MaximumValueLength = 1_024;

    public static string RedactMessage(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var redacted = WindowsPathPattern().Replace(value, "<path:redacted>");
        return LimitLength(redacted);
    }

    public static IReadOnlyDictionary<string, string> RedactProperties(
        IReadOnlyDictionary<string, string?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var redacted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            redacted[pair.Key] = RedactProperty(pair.Key, pair.Value);
        }

        return redacted;
    }

    private static string RedactProperty(string key, string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (SensitiveKeyPattern().IsMatch(key))
        {
            return "<redacted>";
        }

        if (DocumentIdentityKeyPattern().IsMatch(key))
        {
            return $"sha256:{Hash(value)}";
        }

        return RedactMessage(value);
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string LimitLength(string value) => value.Length <= MaximumValueLength
        ? value
        : value[..MaximumValueLength];

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\s\""']+")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex("(?i)(?:password|secret|token|credential|api[-_]?key)")]
    private static partial Regex SensitiveKeyPattern();

    [GeneratedRegex("(?i)(?:path|file|directory|source|destination|input|output)")]
    private static partial Regex DocumentIdentityKeyPattern();
}
