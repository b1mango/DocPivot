using System.Text;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Pdf.Ghostscript;

internal interface IGhostscriptSearchableTextVerifier
{
    Task<GhostscriptSearchableTextVerification> VerifyAsync(
        string executablePath,
        string sourcePath,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

internal sealed record GhostscriptSearchableTextVerification(
    bool IsSucceeded,
    string Status,
    int SourceCharacterCount,
    int OutputCharacterCount,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static GhostscriptSearchableTextVerification Succeeded(
        string status,
        int sourceCharacterCount,
        int outputCharacterCount) =>
        new(
            true,
            status,
            sourceCharacterCount,
            outputCharacterCount,
            null,
            null);

    public static GhostscriptSearchableTextVerification Failed(
        string errorCode,
        string errorMessage,
        int sourceCharacterCount = 0,
        int outputCharacterCount = 0) =>
        new(
            false,
            "failed",
            sourceCharacterCount,
            outputCharacterCount,
            errorCode,
            errorMessage);
}

internal sealed class GhostscriptSearchableTextVerifier(
    IGhostscriptProcessRunner runner) : IGhostscriptSearchableTextVerifier
{
    private readonly IGhostscriptProcessRunner _runner =
        runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<GhostscriptSearchableTextVerification> VerifyAsync(
        string executablePath,
        string sourcePath,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var source = await ExtractTextAsync(
                executablePath,
                sourcePath,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!source.IsSucceeded)
        {
            return GhostscriptSearchableTextVerification.Failed(
                "PDF_TEXT_VERIFICATION_FAILED",
                "The searchable text layer could not be read before compression.");
        }

        var output = await ExtractTextAsync(
                executablePath,
                outputPath,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!output.IsSucceeded)
        {
            return GhostscriptSearchableTextVerification.Failed(
                "PDF_TEXT_VERIFICATION_FAILED",
                "The searchable text layer could not be read after compression.",
                source.NormalizedText.Length);
        }

        if (source.NormalizedText.Length == 0)
        {
            return GhostscriptSearchableTextVerification.Succeeded(
                "not-present",
                0,
                output.NormalizedText.Length);
        }

        if (output.NormalizedText.Length == 0)
        {
            return GhostscriptSearchableTextVerification.Failed(
                "PDF_SEARCHABLE_TEXT_LOST",
                "Compression removed the searchable text layer.",
                source.NormalizedText.Length,
                0);
        }

        if (!string.Equals(
                source.NormalizedText,
                output.NormalizedText,
                StringComparison.Ordinal))
        {
            return GhostscriptSearchableTextVerification.Failed(
                "PDF_SEARCHABLE_TEXT_MISMATCH",
                "Compression changed the searchable text content.",
                source.NormalizedText.Length,
                output.NormalizedText.Length);
        }

        return GhostscriptSearchableTextVerification.Succeeded(
            "verified",
            source.NormalizedText.Length,
            output.NormalizedText.Length);
    }

    private async Task<TextExtractionResult> ExtractTextAsync(
        string executablePath,
        string pdfPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
                executablePath,
                [
                    "-q",
                    "-dSAFER",
                    "-dBATCH",
                    "-dNOPAUSE",
                    "-dNOPROMPT",
                    "-sDEVICE=txtwrite",
                    "-sOutputFile=-",
                    "-f",
                    pdfPath,
                ],
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0
            ? new TextExtractionResult(true, Normalize(result.StandardOutput))
            : new TextExtractionResult(false, string.Empty);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character) && !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private sealed record TextExtractionResult(
        bool IsSucceeded,
        string NormalizedText);
}
