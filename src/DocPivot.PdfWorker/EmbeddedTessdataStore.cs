using System.Reflection;
using System.Security.Cryptography;

namespace DocPivot.PdfWorker;

public sealed class EmbeddedTessdataStore
{
    public const string ModelRevision = "87416418657359cb625c412a48b6e1d6d41c29bd";

    private static readonly Dictionary<string, ModelFile> ModelFiles =
        new Dictionary<string, ModelFile>(StringComparer.Ordinal)
        {
            ["eng"] = new(
                "eng.traineddata",
                "DocPivot.PdfWorker.Models.eng.traineddata",
                "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2"),
            ["chi_sim"] = new(
                "chi_sim.traineddata",
                "DocPivot.PdfWorker.Models.chi_sim.traineddata",
                "A5FCB6F0DB1E1D6D8522F39DB4E848F05984669172E584E8D76B6B3141E1F730"),
        };

    private static readonly ModelFile LicenseFile = new(
        "LICENSE.txt",
        "DocPivot.PdfWorker.Models.LICENSE.txt",
        "CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30");

    private readonly Assembly _assembly;
    private readonly string _cacheRoot;

    public EmbeddedTessdataStore(string? cacheRoot = null)
    {
        _assembly = typeof(EmbeddedTessdataStore).Assembly;
        var localApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        }

        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            localApplicationData,
            "DocPivot",
            "models"));
    }

    public async Task<string> EnsureAvailableAsync(
        string language,
        CancellationToken cancellationToken = default)
    {
        var requestedLanguages = ParseLanguages(language);
        var directory = Path.Combine(_cacheRoot, "tessdata-fast", ModelRevision);
        Directory.CreateDirectory(directory);

        foreach (var requestedLanguage in requestedLanguages)
        {
            await EnsureFileAsync(ModelFiles[requestedLanguage], directory, cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureFileAsync(LicenseFile, directory, cancellationToken).ConfigureAwait(false);
        return directory;
    }

    private static string[] ParseLanguages(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new PdfTableWorkerException(
                "OCR_LANGUAGE_INVALID",
                "At least one OCR language is required.");
        }

        var values = language
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0 || values.Any(value => !ModelFiles.ContainsKey(value)))
        {
            throw new PdfTableWorkerException(
                "OCR_LANGUAGE_UNAVAILABLE",
                "The requested offline OCR language is not bundled.");
        }

        return values;
    }

    private async Task EnsureFileAsync(
        ModelFile model,
        string directory,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, model.FileName);
        if (File.Exists(destination) &&
            string.Equals(
                await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false),
                model.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var staging = Path.Combine(directory, $".{model.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var source = _assembly.GetManifestResourceStream(model.ResourceName)
                ?? throw new PdfTableWorkerException(
                    "OCR_MODEL_RESOURCE_MISSING",
                    "A bundled OCR model resource is missing.");
            await using (var output = new FileStream(
                staging,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = await ComputeSha256Async(staging, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdfTableWorkerException(
                    "OCR_MODEL_HASH_MISMATCH",
                    "A bundled OCR model failed integrity verification.");
            }

            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale cache staging file is harmless and can be retried later.
        }
    }

    private sealed record ModelFile(string FileName, string ResourceName, string Sha256);
}
