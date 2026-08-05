using DocPivot.Core.Renaming;

namespace DocPivot.App.ViewModels;

public sealed record RenameToolModeOption(
    RenameToolMode Mode,
    string Title);

public sealed record RenamePositionOption(
    RenameInsertPosition Position,
    string Title);

public sealed record RenameSortModeOption(
    RenameSortMode Mode,
    string Title);
