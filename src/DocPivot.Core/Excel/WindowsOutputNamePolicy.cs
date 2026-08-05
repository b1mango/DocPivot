using System.Text;

namespace DocPivot.Core.Excel;

public static class WindowsOutputNamePolicy
{
    private const string FallbackStem = "工作表";
    private const string InvalidCharacters = "<>:\"/\\|?*";

    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();

    public static IReadOnlyList<string> Allocate(
        IEnumerable<string?> worksheetNames,
        string extension = ".xlsx")
    {
        ArgumentNullException.ThrowIfNull(worksheetNames);
        var normalizedExtension = NormalizeExtension(extension);
        var allocated = new List<string>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var worksheetName in worksheetNames)
        {
            var stem = SanitizeStem(worksheetName);
            var candidateStem = stem;
            var suffixNumber = 2;
            while (!usedFileNames.Add(candidateStem + normalizedExtension))
            {
                candidateStem = $"{stem} ({suffixNumber})";
                suffixNumber++;
            }

            allocated.Add(candidateStem + normalizedExtension);
        }

        return allocated;
    }

    public static string SanitizeStem(string? worksheetName)
    {
        if (string.IsNullOrWhiteSpace(worksheetName))
        {
            return FallbackStem;
        }

        var builder = new StringBuilder(worksheetName.Length);
        foreach (var character in worksheetName)
        {
            builder.Append(character < ' ' || InvalidCharacters.Contains(character, StringComparison.Ordinal)
                ? '_'
                : character);
        }

        var stem = builder.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(stem))
        {
            return FallbackStem;
        }

        var deviceName = stem.Split('.', 2)[0];
        if (ReservedDeviceNames.Contains(deviceName))
        {
            stem += '_';
        }

        return stem;
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        if (normalized.Length == 1 ||
            normalized.Any(character =>
                character < ' ' ||
                character is '/' or '\\' ||
                InvalidCharacters.Contains(character, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The output extension is not a valid Windows file extension.", nameof(extension));
        }

        return normalized;
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
        };

        for (var number = 1; number <= 9; number++)
        {
            names.Add($"COM{number}");
            names.Add($"LPT{number}");
        }

        return names;
    }
}
