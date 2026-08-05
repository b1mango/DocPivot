using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocPivot.Core.Documents;

namespace DocPivot.Core.Renaming;

public static partial class BatchRenamePlanner
{
    private const int MaximumFileNameLength = 255;
    private const int MaximumWindowsPathLength = 32_760;
    private const int MaximumPatternLength = 512;
    private const int MaximumReplacementLength = 1_024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly char[] InvalidFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static BatchRenamePlan Create(
        IReadOnlyList<RenameSourceFile> sources,
        BatchRenameRuleSet rules,
        IReadOnlyDictionary<string, string>? manualOverrides = null,
        Func<string, bool>? pathExists = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rules);

        manualOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        pathExists ??= static _ => false;
        ValidateSources(sources);

        var orderedSources = OrderSources(sources, rules.SortMode);
        var candidates = new List<Candidate>(orderedSources.Length);
        for (var index = 0; index < orderedSources.Length; index++)
        {
            var source = orderedSources[index];
            var hasManualOverride = manualOverrides.TryGetValue(source.FullPath, out var manualName);
            candidates.Add(CreateCandidate(
                source,
                rules,
                index,
                orderedSources.Length,
                hasManualOverride,
                manualName));
        }

        ApplyBatchValidation(candidates, pathExists);
        return new BatchRenamePlan(candidates
            .Select(static candidate => candidate.ToPlanItem())
            .ToArray());
    }

    private static Candidate CreateCandidate(
        RenameSourceFile source,
        BatchRenameRuleSet rules,
        int orderedIndex,
        int sourceCount,
        bool hasManualOverride,
        string? manualName)
    {
        var originalExtension = Path.GetExtension(source.FileName);
        var originalStem = Path.GetFileNameWithoutExtension(source.FileName);
        var workingName = rules.IncludeExtension ? source.FileName : originalStem;
        string calculatedFileName;
        try
        {
            workingName = ApplySearchReplace(workingName, rules.SearchReplace);
            workingName = ApplyInsertion(
                workingName,
                rules.Insertion,
                source,
                orderedIndex,
                sourceCount);
            workingName = ApplyNumbering(
                workingName,
                rules.Numbering,
                source,
                orderedIndex,
                sourceCount);
            calculatedFileName = rules.IncludeExtension
                ? workingName
                : $"{workingName}{originalExtension}";
        }
        catch (RenameRuleException exception)
        {
            return Candidate.Invalid(
                source,
                source.FileName,
                source.FileName,
                exception.ErrorCode,
                hasManualOverride);
        }
        catch (RegexMatchTimeoutException)
        {
            return Candidate.Invalid(
                source,
                source.FileName,
                source.FileName,
                "RENAME_REGEX_TIMEOUT",
                hasManualOverride);
        }
        catch (ArgumentException)
        {
            return Candidate.Invalid(
                source,
                source.FileName,
                source.FileName,
                "RENAME_REGEX_INVALID",
                hasManualOverride);
        }

        var proposedFileName = hasManualOverride ? manualName ?? string.Empty : calculatedFileName;
        var validationCode = ValidateFileName(
            proposedFileName,
            originalExtension,
            rules.IncludeExtension,
            Path.GetDirectoryName(source.FullPath)!);
        if (validationCode is not null)
        {
            return Candidate.Invalid(
                source,
                calculatedFileName,
                proposedFileName,
                validationCode,
                hasManualOverride);
        }

        var targetPath = Path.Combine(Path.GetDirectoryName(source.FullPath)!, proposedFileName);
        var isUnchanged = string.Equals(source.FullPath, targetPath, StringComparison.Ordinal);
        return new Candidate(
            source,
            calculatedFileName,
            proposedFileName,
            targetPath,
            isUnchanged ? RenamePlanItemStatus.Unchanged : RenamePlanItemStatus.Ready,
            null,
            hasManualOverride);
    }

    private static string ApplySearchReplace(
        string input,
        RenameSearchReplaceRule rule)
    {
        if (!rule.IsEnabled)
        {
            return input;
        }

        if (rule.SearchText.Length > MaximumPatternLength ||
            rule.ReplacementText.Length > MaximumReplacementLength)
        {
            throw new RenameRuleException("RENAME_RULE_TOO_LONG");
        }

        if (rule.UseRegularExpression)
        {
            var options = RegexOptions.CultureInvariant;
            if (!rule.MatchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(rule.SearchText, options, RegexTimeout);
            return rule.ReplaceAll
                ? regex.Replace(input, rule.ReplacementText)
                : regex.Replace(input, rule.ReplacementText, 1);
        }

        var comparison = rule.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (!rule.ReplaceAll)
        {
            var matchIndex = input.IndexOf(rule.SearchText, comparison);
            return matchIndex < 0
                ? input
                : string.Concat(
                    input.AsSpan(0, matchIndex),
                    rule.ReplacementText,
                    input.AsSpan(matchIndex + rule.SearchText.Length));
        }

        var result = new StringBuilder(input.Length);
        var searchIndex = 0;
        while (searchIndex < input.Length)
        {
            var matchIndex = input.IndexOf(rule.SearchText, searchIndex, comparison);
            if (matchIndex < 0)
            {
                result.Append(input, searchIndex, input.Length - searchIndex);
                break;
            }

            result.Append(input, searchIndex, matchIndex - searchIndex);
            result.Append(rule.ReplacementText);
            searchIndex = matchIndex + rule.SearchText.Length;
        }

        return result.ToString();
    }

    private static string ApplyInsertion(
        string input,
        RenameInsertionRule rule,
        RenameSourceFile source,
        int orderedIndex,
        int sourceCount)
    {
        if (!rule.IsEnabled)
        {
            return input;
        }

        var content = ExpandTokens(rule.Content, source, orderedIndex, sourceCount);
        return Insert(input, content, rule.Position, rule.Index, rule.CountFromEnd);
    }

    private static string ApplyNumbering(
        string input,
        RenameNumberingRule rule,
        RenameSourceFile source,
        int orderedIndex,
        int sourceCount)
    {
        if (!rule.IsEnabled)
        {
            return input;
        }

        if (rule.Step == 0 || rule.PaddingWidth is < 0 or > 18)
        {
            throw new RenameRuleException("RENAME_NUMBERING_INVALID");
        }

        long number;
        try
        {
            number = checked(rule.Start + rule.Step * orderedIndex);
        }
        catch (OverflowException)
        {
            throw new RenameRuleException("RENAME_NUMBERING_INVALID");
        }

        var automaticWidth = Math.Max(1, Math.Max(
            rule.Start.ToString(CultureInfo.InvariantCulture).TrimStart('-').Length,
            number.ToString(CultureInfo.InvariantCulture).TrimStart('-').Length));
        var width = rule.PaddingWidth == 0 ? automaticWidth : rule.PaddingWidth;
        var unsignedDigits = number
            .ToString(CultureInfo.InvariantCulture)
            .TrimStart('-')
            .PadLeft(width, '0');
        var formattedNumber = number < 0 ? $"-{unsignedDigits}" : unsignedDigits;
        var prefix = ExpandTokens(rule.Prefix, source, orderedIndex, sourceCount);
        var suffix = ExpandTokens(rule.Suffix, source, orderedIndex, sourceCount);
        return Insert(
            input,
            $"{prefix}{formattedNumber}{suffix}",
            rule.Position,
            rule.Index,
            rule.CountFromEnd);
    }

    private static string Insert(
        string input,
        string content,
        RenameInsertPosition position,
        int specifiedIndex,
        bool countFromEnd)
    {
        var textElementOffsets = StringInfo.ParseCombiningCharacters(input);
        var textElementCount = textElementOffsets.Length;
        var insertionElementIndex = position switch
        {
            RenameInsertPosition.Beginning => 0,
            RenameInsertPosition.End => textElementCount,
            RenameInsertPosition.SpecifiedIndex when specifiedIndex >= 0 =>
                countFromEnd ? textElementCount - specifiedIndex : specifiedIndex,
            _ => throw new RenameRuleException("RENAME_POSITION_INVALID"),
        };
        if (insertionElementIndex < 0 || insertionElementIndex > textElementCount)
        {
            throw new RenameRuleException("RENAME_POSITION_OUT_OF_RANGE");
        }

        var characterIndex = insertionElementIndex == textElementCount
            ? input.Length
            : textElementOffsets[insertionElementIndex];
        return input.Insert(characterIndex, content);
    }

    private static string ExpandTokens(
        string template,
        RenameSourceFile source,
        int orderedIndex,
        int sourceCount)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return TokenRegex().Replace(template, match =>
        {
            var token = match.Groups["token"].Value;
            return token switch
            {
                "name" => Path.GetFileNameWithoutExtension(source.FileName),
                "ext" => Path.GetExtension(source.FileName).TrimStart('.'),
                "index" => (orderedIndex + 1).ToString(
                    new string('0', Math.Max(1, sourceCount.ToString(CultureInfo.InvariantCulture).Length)),
                    CultureInfo.InvariantCulture),
                "parent" => Path.GetFileName(Path.GetDirectoryName(source.FullPath)) ?? string.Empty,
                _ when token.StartsWith("created:", StringComparison.Ordinal) =>
                    FormatDate(source.CreationTimeUtc, token["created:".Length..]),
                _ when token.StartsWith("modified:", StringComparison.Ordinal) =>
                    FormatDate(source.LastWriteTimeUtc, token["modified:".Length..]),
                _ => throw new RenameRuleException("RENAME_TOKEN_UNKNOWN"),
            };
        });
    }

    private static string FormatDate(DateTime utcValue, string format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 64)
        {
            throw new RenameRuleException("RENAME_DATE_FORMAT_INVALID");
        }

        try
        {
            return utcValue.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new RenameRuleException("RENAME_DATE_FORMAT_INVALID");
        }
    }

    private static string? ValidateFileName(
        string fileName,
        string originalExtension,
        bool includeExtension,
        string parentDirectory)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." or "..")
        {
            return "RENAME_NAME_EMPTY";
        }

        if (fileName.Length > MaximumFileNameLength)
        {
            return "RENAME_NAME_TOO_LONG";
        }

        if (fileName[^1] is ' ' or '.')
        {
            return "RENAME_NAME_TRAILING_CHARACTER";
        }

        if (fileName.Any(character =>
                character < 32 || InvalidFileNameCharacters.Contains(character)))
        {
            return "RENAME_NAME_INVALID_CHARACTER";
        }

        var reservedStem = fileName.Split('.')[0];
        if (ReservedWindowsNameRegex().IsMatch(reservedStem))
        {
            return "RENAME_NAME_RESERVED";
        }

        var extension = Path.GetExtension(fileName);
        if (!includeExtension &&
            !string.Equals(extension, originalExtension, StringComparison.OrdinalIgnoreCase))
        {
            return "RENAME_EXTENSION_PROTECTED";
        }

        if (includeExtension &&
            (string.IsNullOrWhiteSpace(extension) ||
             !DocumentAdmissionPolicy.IsSupported(DocumentOperation.BatchRename, extension)))
        {
            return "RENAME_EXTENSION_UNSUPPORTED";
        }

        var targetPath = Path.Combine(parentDirectory, fileName);
        return targetPath.Length > MaximumWindowsPathLength
            ? "RENAME_PATH_TOO_LONG"
            : null;
    }

    private static void ApplyBatchValidation(
        IReadOnlyList<Candidate> candidates,
        Func<string, bool> pathExists)
    {
        var sourcePaths = candidates
            .Select(static candidate => candidate.Source.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicateGroup in candidates
            .Where(static candidate => candidate.ErrorCode is null)
            .GroupBy(static candidate => candidate.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1))
        {
            foreach (var candidate in duplicateGroup)
            {
                candidate.SetConflict("RENAME_TARGET_DUPLICATE");
            }
        }

        foreach (var candidate in candidates.Where(static candidate => candidate.ErrorCode is null))
        {
            if (candidate.Status == RenamePlanItemStatus.Unchanged ||
                sourcePaths.Contains(candidate.TargetPath))
            {
                continue;
            }

            try
            {
                if (pathExists(candidate.TargetPath))
                {
                    candidate.SetConflict("RENAME_TARGET_EXISTS");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                candidate.SetInvalid("RENAME_TARGET_UNAVAILABLE");
            }
        }
    }

    private static RenameSourceFile[] OrderSources(
        IReadOnlyList<RenameSourceFile> sources,
        RenameSortMode sortMode)
    {
        IOrderedEnumerable<RenameSourceFile> ordered = sortMode switch
        {
            RenameSortMode.FileName => sources.OrderBy(
                static source => source.FileName,
                StringComparer.OrdinalIgnoreCase),
            RenameSortMode.CreationTime => sources.OrderBy(static source => source.CreationTimeUtc),
            RenameSortMode.LastWriteTime => sources.OrderBy(static source => source.LastWriteTimeUtc),
            _ => sources.OrderBy(static source => source.AddedOrder),
        };
        return ordered
            .ThenBy(static source => source.AddedOrder)
            .ThenBy(static source => source.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateSources(IReadOnlyList<RenameSourceFile> sources)
    {
        if (sources.Count > DocumentLimits.MaximumRenameBatchFiles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sources),
                $"A rename batch accepts at most {DocumentLimits.MaximumRenameBatchFiles} files.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.FullPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
            if (!paths.Add(Path.GetFullPath(source.FullPath)))
            {
                throw new ArgumentException(
                    "A rename source path must occur only once, ignoring case.",
                    nameof(sources));
            }
        }
    }

    [GeneratedRegex(@"\{(?<token>[^{}]+)\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(
        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex ReservedWindowsNameRegex();

    private sealed class RenameRuleException(string errorCode) : Exception
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed class Candidate(
        RenameSourceFile source,
        string calculatedFileName,
        string proposedFileName,
        string targetPath,
        RenamePlanItemStatus status,
        string? errorCode,
        bool hasManualOverride)
    {
        public RenameSourceFile Source { get; } = source;

        public string CalculatedFileName { get; } = calculatedFileName;

        public string ProposedFileName { get; } = proposedFileName;

        public string TargetPath { get; } = targetPath;

        public RenamePlanItemStatus Status { get; private set; } = status;

        public string? ErrorCode { get; private set; } = errorCode;

        public bool HasManualOverride { get; } = hasManualOverride;

        public static Candidate Invalid(
            RenameSourceFile source,
            string calculatedFileName,
            string proposedFileName,
            string errorCode,
            bool hasManualOverride) =>
            new(
                source,
                calculatedFileName,
                proposedFileName,
                source.FullPath,
                RenamePlanItemStatus.Invalid,
                errorCode,
                hasManualOverride);

        public void SetConflict(string code)
        {
            Status = RenamePlanItemStatus.Conflict;
            ErrorCode = code;
        }

        public void SetInvalid(string code)
        {
            Status = RenamePlanItemStatus.Invalid;
            ErrorCode = code;
        }

        public RenamePlanItem ToPlanItem() => new(
            Source,
            CalculatedFileName,
            ProposedFileName,
            TargetPath,
            Status,
            ErrorCode,
            HasManualOverride);
    }
}
