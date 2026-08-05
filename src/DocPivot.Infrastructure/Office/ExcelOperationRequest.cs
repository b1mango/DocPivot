namespace DocPivot.Infrastructure.Office;

public sealed record ExcelOperationRequest(
    ExcelOperationKind Kind,
    IReadOnlyList<string> Inputs,
    string Output,
    ExcelOperationOptions Options)
{
    public static ExcelOperationRequest Merge(
        IReadOnlyList<string> inputs,
        string output,
        ExcelOperationOptions? options = null) =>
        new(ExcelOperationKind.Merge, inputs, output, options ?? ExcelOperationOptions.SafeDefaults);

    public static ExcelOperationRequest Split(
        string input,
        string outputDirectory,
        ExcelOperationOptions? options = null) =>
        new(ExcelOperationKind.Split, [input], outputDirectory, options ?? ExcelOperationOptions.SafeDefaults);

    public static ExcelOperationRequest Compress(
        string input,
        string output,
        ExcelOperationOptions? options = null) =>
        new(ExcelOperationKind.Compress, [input], output, options ?? ExcelOperationOptions.SafeDefaults);
}
