using System.Security;
using System.Security.Cryptography;

namespace DocPivot.Infrastructure.Pdf;

internal interface IQpdfToolProvider
{
    Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed class QpdfToolProbe : IQpdfToolProvider
{
    public const string PinnedVersion = "12.3.2";
    public static readonly string RelativeExecutablePath = Path.Combine(
        "vendor",
        "qpdf",
        PinnedVersion,
        "bin",
        "qpdf.exe");

    private static readonly IReadOnlyDictionary<string, string> ExpectedRuntimeHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["concrt140.dll"] = "2405355f0a58067b258f8df33c327e3a3d716eaac5a3a5aebb757842d85bd376",
            ["fix-qdf.exe"] = "f322a47cf83869bcfd3c7a1a60b173b89277d56f94ed22997c3eeb519be156b8",
            ["msvcp140.dll"] = "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9",
            ["msvcp140_1.dll"] = "bfad5aef4c63a669e3c140655cdfdf395b6c979b400a447bd5dcb65ed8826c3d",
            ["msvcp140_2.dll"] = "3ea06f0ee098b4823cb79599df3780e7f23cce52c19aac31d2a0d47efe33a5e9",
            ["msvcp140_atomic_wait.dll"] = "640b2aefced484d0368eea5bdd06addd0658a3a70a49256e560d6923b404a479",
            ["msvcp140_codecvt_ids.dll"] = "f2069a52880ec885ee7f0511186100eb7fada0411a2b4948fafea7735b878a18",
            ["qpdf.exe"] = "43f79db620ce09529a67572a5de87aec4065b95f11ba6e5918db557f943a7eac",
            ["qpdf30.dll"] = "623338ff5a9caab476f9e80ccc40c28c194208f4bc5d8e51eac7fca792e2e969",
            ["vcruntime140.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
            ["vcruntime140_1.dll"] = "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f",
            ["zlib-flate.exe"] = "9573d60241869907fb1fd6df682b0327f2403e7e79f9f563c96f10fdeac6b3f5",
        };

    private readonly string _runtimeDirectory;
    private readonly IQpdfCliRunner _runner;
    private readonly TimeSpan _timeout;

    public QpdfToolProbe(string distributionRoot)
        : this(distributionRoot, new QpdfCliRunner(), TimeSpan.FromSeconds(10))
    {
    }

    internal QpdfToolProbe(
        string distributionRoot,
        IQpdfCliRunner runner,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _runtimeDirectory = Path.Combine(
            PdfRuntimeDistributionRoot.Resolve(distributionRoot),
            "vendor",
            "qpdf",
            PinnedVersion,
            "bin");
        _runner = runner;
        _timeout = timeout;
    }

    public async Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var expectedFile in ExpectedRuntimeHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(_runtimeDirectory, expectedFile.Key);
                if (!File.Exists(path))
                {
                    return QpdfToolStatus.Unavailable(
                        "QPDF_RUNTIME_MISSING",
                        $"The pinned qpdf runtime file is missing: {expectedFile.Key}");
                }

                var actualHash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedFile.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return QpdfToolStatus.Unavailable(
                        "QPDF_RUNTIME_HASH_MISMATCH",
                        $"The pinned qpdf runtime hash does not match: {expectedFile.Key}");
                }
            }

            var executablePath = Path.Combine(_runtimeDirectory, "qpdf.exe");
            var versionResult = await _runner.RunAsync(
                    executablePath,
                    ["--version"],
                    _timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var expectedVersionText = $"qpdf version {PinnedVersion}";
            if (versionResult.ExitCode != 0 ||
                !versionResult.StandardOutput.Contains(expectedVersionText, StringComparison.Ordinal))
            {
                return QpdfToolStatus.Unavailable(
                    "QPDF_VERSION_MISMATCH",
                    $"The qpdf runtime did not report the pinned version {PinnedVersion}.");
            }

            return QpdfToolStatus.Ready(executablePath, PinnedVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return QpdfToolStatus.Unavailable("QPDF_PROBE_TIMEOUT", exception.Message, isRetryable: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return QpdfToolStatus.Unavailable(
                "QPDF_PROBE_FAILED",
                "The pinned qpdf runtime could not be verified.",
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
