using DocPivot.Core.Contracts;

namespace DocPivot.App.ViewModels;

public sealed record PdfOcrModeOption(
    WorkerPdfOcrMode Mode,
    string Title,
    string Description);
