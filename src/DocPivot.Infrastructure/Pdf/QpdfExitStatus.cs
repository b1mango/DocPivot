using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Pdf;

internal static class QpdfExitStatus
{
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 3;

    public static bool HasWarnings(ExternalProcessResult result) =>
        result.ExitCode == 3 || GetDiagnosticLines(result.StandardError).Any(IsWarningLine);

    public static bool TryGetEncryptionStatus(ExternalProcessResult result, out bool isEncrypted)
    {
        isEncrypted = IsSuccess(result.ExitCode);
        // --is-encrypted uses 2 for both unencrypted input and read failures.
        return isEncrypted ||
            (result.ExitCode == 2 && GetDiagnosticLines(result.StandardError).All(IsWarningLine));
    }

    private static string[] GetDiagnosticLines(string standardError) =>
        standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsWarningLine(string line) =>
        line.StartsWith("WARNING:", StringComparison.Ordinal) ||
        line is "qpdf: operation succeeded with warnings" or
            "qpdf: operation succeeded with warnings; resulting file may have some problems";
}
