using DocPivot.Core.Excel;

namespace DocPivot.App.ViewModels;

public sealed record ExcelImageCompressionProfileOption(
    ExcelImageCompressionLevel Level,
    string Title,
    string Description);
