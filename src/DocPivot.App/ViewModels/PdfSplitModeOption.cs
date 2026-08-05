using DocPivot.Infrastructure.Pdf;

namespace DocPivot.App.ViewModels;

public sealed record PdfSplitModeOption(
    PdfSplitMode Mode,
    string Title);
