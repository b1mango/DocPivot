using System.Text;

namespace DocPivot.Core.Excel;

public static class ExcelWorksheetNameAllocator
{
    public const int MaximumNameLength = 31;

    private const string FallbackName = "工作表";
    private const string InvalidCharacters = ":\\/?*[]";

    public static IReadOnlyList<string> Allocate(IEnumerable<string?> proposedNames)
    {
        ArgumentNullException.ThrowIfNull(proposedNames);

        var allocated = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proposedName in proposedNames)
        {
            var baseName = Sanitize(proposedName);
            var candidate = baseName;
            var suffixNumber = 2;
            while (!usedNames.Add(candidate))
            {
                var suffix = $" ({suffixNumber})";
                var prefix = TruncateWithoutSplittingSurrogate(baseName, MaximumNameLength - suffix.Length);
                candidate = EnsureValidEdges(prefix + suffix);
                suffixNumber++;
            }

            allocated.Add(candidate);
        }

        return allocated;
    }

    public static string Sanitize(string? proposedName)
    {
        if (string.IsNullOrWhiteSpace(proposedName))
        {
            return FallbackName;
        }

        var builder = new StringBuilder(proposedName.Length);
        foreach (var character in proposedName)
        {
            builder.Append(character < ' ' || InvalidCharacters.Contains(character, StringComparison.Ordinal)
                ? '_'
                : character);
        }

        var sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0)
        {
            return FallbackName;
        }

        sanitized = EnsureValidEdges(TruncateWithoutSplittingSurrogate(sanitized, MaximumNameLength));
        if (string.Equals(sanitized, "History", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = "History_";
        }

        return sanitized.Length == 0 ? FallbackName : sanitized;
    }

    private static string EnsureValidEdges(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var characters = value.ToCharArray();
        if (characters[0] == '\'')
        {
            characters[0] = '_';
        }

        if (characters[^1] == '\'')
        {
            characters[^1] = '_';
        }

        return new string(characters);
    }

    private static string TruncateWithoutSplittingSurrogate(string value, int maximumLength)
    {
        if (maximumLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maximumLength)
        {
            return value;
        }

        var length = maximumLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
