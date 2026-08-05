using System.Security;
using System.Security.Cryptography;

namespace DocPivot.Infrastructure.Pdf.Ghostscript;

internal interface IGhostscriptRuntimeProbe
{
    Task<GhostscriptEngineStatus> ProbeAsync(CancellationToken cancellationToken = default);
}

internal sealed class GhostscriptRuntimeProbe : IGhostscriptRuntimeProbe
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedFileHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("bin", "gswin64c.exe")] =
                "de3bce7c03dfce0dfd78af8bf1d464c6a21fbcb7237f127ec5c027179d2c2741",
            [Path.Combine("bin", "gsdll64.dll")] =
                "af9bcd9313956aea105fd38da35413d50e446548ba21b6e85b87da3f24e8c546",
            [Path.Combine("bin", "msvcp140.dll")] =
                "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9",
            [Path.Combine("bin", "vcruntime140.dll")] =
                "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
            [Path.Combine("bin", "vcruntime140_1.dll")] =
                "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f",
        };

    private static readonly IReadOnlyList<string> RequiredResources =
    [
        Path.Combine("Resource", "Init", "gs_init.ps"),
        Path.Combine("Resource", "Init", "gs_pdfwr.ps"),
        Path.Combine("Resource", "Init", "Fontmap.GS"),
        Path.Combine("iccprofiles", "default_rgb.icc"),
        Path.Combine("iccprofiles", "default_cmyk.icc"),
        Path.Combine("lib", "PDFX_def.ps"),
        "LICENSE.txt",
        "README.upstream.rst",
    ];

    private readonly string _runtimeDirectory;
    private readonly IGhostscriptProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public GhostscriptRuntimeProbe(
        string distributionRoot,
        IGhostscriptProcessRunner runner,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _runtimeDirectory = Path.Combine(
            Path.GetFullPath(distributionRoot),
            "vendor",
            "ghostscript",
            GhostscriptPdfOptimizer.PinnedVersion,
            "runtime");
        _runner = runner;
        _timeout = timeout;
    }

    public async Task<GhostscriptEngineStatus> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var expectedFile in ExpectedFileHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(_runtimeDirectory, expectedFile.Key);
                if (!File.Exists(path))
                {
                    return GhostscriptEngineStatus.Unavailable(
                        "GHOSTSCRIPT_RUNTIME_MISSING",
                        $"The pinned Ghostscript runtime file is missing: {expectedFile.Key}");
                }

                var actualHash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedFile.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return GhostscriptEngineStatus.Unavailable(
                        "GHOSTSCRIPT_RUNTIME_HASH_MISMATCH",
                        $"The pinned Ghostscript runtime hash does not match: {expectedFile.Key}");
                }
            }

            foreach (var resource in RequiredResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(_runtimeDirectory, resource)))
                {
                    return GhostscriptEngineStatus.Unavailable(
                        "GHOSTSCRIPT_RESOURCE_MISSING",
                        $"The pinned Ghostscript resource is missing: {resource}");
                }
            }

            var executablePath = Path.Combine(_runtimeDirectory, "bin", "gswin64c.exe");
            var versionResult = await _runner.RunAsync(
                    executablePath,
                    ["-version"],
                    _timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var versionText = versionResult.StandardOutput + versionResult.StandardError;
            var expectedVersionText = $"GPL Ghostscript {GhostscriptPdfOptimizer.PinnedVersion}";
            if (versionResult.ExitCode != 0 ||
                !versionText.Contains(expectedVersionText, StringComparison.Ordinal))
            {
                return GhostscriptEngineStatus.Unavailable(
                    "GHOSTSCRIPT_VERSION_MISMATCH",
                    $"Ghostscript did not report the pinned version " +
                    $"{GhostscriptPdfOptimizer.PinnedVersion}.");
            }

            return GhostscriptEngineStatus.Ready(
                executablePath,
                GhostscriptPdfOptimizer.PinnedVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return GhostscriptEngineStatus.Unavailable(
                "GHOSTSCRIPT_PROBE_TIMEOUT",
                exception.Message,
                isRetryable: true);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            CryptographicException or
            InvalidOperationException)
        {
            return GhostscriptEngineStatus.Unavailable(
                "GHOSTSCRIPT_PROBE_FAILED",
                "The pinned Ghostscript runtime could not be verified.",
                isRetryable: exception is IOException);
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
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
