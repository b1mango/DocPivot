using DocPivot.Core.Contracts;

namespace DocPivot.App.ViewModels;

public sealed record PdfWorksheetModeOption(
    WorkerPdfWorksheetMode Mode,
    string Title,
    string Description);
