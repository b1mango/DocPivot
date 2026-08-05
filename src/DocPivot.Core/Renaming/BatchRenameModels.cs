namespace DocPivot.Core.Renaming;

public enum RenameInsertPosition
{
    Beginning,
    SpecifiedIndex,
    End,
}

public enum RenameSortMode
{
    AddedOrder,
    FileName,
    CreationTime,
    LastWriteTime,
}

public enum RenamePlanItemStatus
{
    Ready,
    Unchanged,
    Invalid,
    Conflict,
}

public sealed record RenameSearchReplaceRule(
    string SearchText = "",
    string ReplacementText = "",
    bool MatchCase = false,
    bool ReplaceAll = true,
    bool UseRegularExpression = false)
{
    public bool IsEnabled => !string.IsNullOrEmpty(SearchText);
}

public sealed record RenameInsertionRule(
    string Content = "",
    RenameInsertPosition Position = RenameInsertPosition.Beginning,
    int Index = 0,
    bool CountFromEnd = false)
{
    public bool IsEnabled => !string.IsNullOrEmpty(Content);
}

public sealed record RenameNumberingRule(
    bool IsEnabled = false,
    long Start = 1,
    long Step = 1,
    int PaddingWidth = 0,
    string Prefix = "",
    string Suffix = "",
    RenameInsertPosition Position = RenameInsertPosition.End,
    int Index = 0,
    bool CountFromEnd = false);

public sealed record BatchRenameRuleSet(
    RenameSearchReplaceRule SearchReplace,
    RenameInsertionRule Insertion,
    RenameNumberingRule Numbering,
    RenameSortMode SortMode = RenameSortMode.AddedOrder,
    bool IncludeExtension = false)
{
    public static BatchRenameRuleSet Default { get; } = new(
        new RenameSearchReplaceRule(),
        new RenameInsertionRule(),
        new RenameNumberingRule());
}

public sealed record RenameSourceFile(
    string FullPath,
    string FileName,
    long SizeBytes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    int AddedOrder);

public sealed record RenamePlanItem(
    RenameSourceFile Source,
    string CalculatedFileName,
    string ProposedFileName,
    string TargetPath,
    RenamePlanItemStatus Status,
    string? ErrorCode,
    bool HasManualOverride)
{
    public bool IsChanged => Status == RenamePlanItemStatus.Ready;

    public bool IsValid => Status is RenamePlanItemStatus.Ready or RenamePlanItemStatus.Unchanged;
}

public sealed record BatchRenamePlan(IReadOnlyList<RenamePlanItem> Items)
{
    public int ChangedCount => Items.Count(static item => item.IsChanged);

    public int ConflictCount => Items.Count(static item => item.Status == RenamePlanItemStatus.Conflict);

    public int InvalidCount => Items.Count(static item => item.Status == RenamePlanItemStatus.Invalid);

    public bool CanExecute => ChangedCount > 0 && ConflictCount == 0 && InvalidCount == 0;
}
