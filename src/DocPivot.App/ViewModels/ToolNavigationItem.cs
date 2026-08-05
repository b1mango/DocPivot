using DocPivot.Core.Documents;

namespace DocPivot.App.ViewModels;

public sealed record ToolNavigationItem(
    DocumentOperation Operation,
    string Title,
    string Subtitle,
    string Icon,
    string AcceptedFormats);

