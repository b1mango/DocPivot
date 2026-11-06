using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Pdf;

public sealed class PdfCompressionEstimator : IPdfCompressionEstimator, IDisposable
{
    private const long ExactSizeLimit = 8 * 1024 * 1024;
    private const int ExactPageLimit = 40;
    private const int SamplePageCount = 8;
    private const int MaximumCachedFiles = 8;
    private const long MaximumCachedSampleBytes = 64 * 1024 * 1024;

    private readonly IPdfOperationsClient _operations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<CacheEntry> _cache = new();
    private long _cachedSampleBytes;

    public PdfCompressionEstimator(string distributionRoot)
        : this(new CompositePdfOperationsClient(distributionRoot))
    {
    }

    public PdfCompressionEstimator(IPdfOperationsClient operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
    }

    public async Task<PdfCompressionEstimate> EstimateAsync(
        string pdfPath,
        int strength,
        string? password,
        CancellationToken cancellationToken)
    {
        _ = PdfCompressionProfile.FromStrength(strength);
        var path = Path.GetFullPath(pdfPath);
        // Separate credentials without retaining passwords or serving a decrypted cache to a different caller.
        var credential = password is null ? string.Empty :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = GetIdentity(path, credential);
            var node = _cache.First;
            while (node is not null && node.Value.Identity != identity)
            {
                node = node.Next;
            }

            var entry = node?.Value;
            if (node is not null)
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                if (entry!.Estimates.TryGetValue(strength, out var cached))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return cached;
                }
            }

            using var workspace = JobWorkspace.Create(
                Path.Combine(Path.GetTempPath(), "DocPivot", "pdf-estimates"), Guid.NewGuid());
            if (entry is null)
            {
                var preflight = await _operations.PreflightAsync(
                    new PdfPreflightRequest(path, password), cancellationToken).ConfigureAwait(false);
                if (!preflight.IsSucceeded)
                {
                    throw new IOException($"{preflight.ErrorCode}: {preflight.ErrorMessage}");
                }

                entry = new CacheEntry(identity, preflight.PageCount!.Value);
            }

            var exact = identity.Length <= ExactSizeLimit && entry.PageCount <= ExactPageLimit;
            var inputPath = path;
            var inputPassword = password;
            if (!exact)
            {
                inputPath = workspace.GetIntermediatePath("sample.pdf");
                if (entry.Sample is not null)
                {
                    await File.WriteAllBytesAsync(inputPath, entry.Sample, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var count = Math.Min(SamplePageCount, entry.PageCount);
                    var pages = Enumerable.Range(0, count).Select(index =>
                        (count == 1 ? 1 : 1 + index * (entry.PageCount - 1) / (count - 1))
                        .ToString(CultureInfo.InvariantCulture));
                    var split = await _operations.SplitAsync(new PdfSplitRequest(
                        path, inputPath, PdfSplitSelection.CustomRange(string.Join(",", pages)), password),
                        cancellationToken).ConfigureAwait(false);
                    EnsureSucceeded(split);
                    if (new FileInfo(inputPath).Length <= MaximumCachedSampleBytes)
                    {
                        var sample = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
                        entry.Sample = sample;
                        if (node is not null)
                        {
                            _cachedSampleBytes += sample.LongLength;
                        }
                    }
                }

                inputPassword = null;
            }

            var originalSampleBytes = new FileInfo(inputPath).Length;
            var outputPath = workspace.GetIntermediatePath("compressed.pdf");
            var result = await _operations.OptimizeAsync(
                new PdfOptimizeRequest(inputPath, outputPath, strength, inputPassword),
                cancellationToken).ConfigureAwait(false);
            EnsureSucceeded(result);
            var outputBytes = new FileInfo(result.Artifacts[0]).Length;
            // A ratio of the same extracted subset accounts for shared resources better than bytes per page.
            var estimatedBytes = exact ? outputBytes :
                (long)Math.Round(identity.Length * Math.Min(1d, (double)outputBytes / originalSampleBytes));
            var estimate = new PdfCompressionEstimate(
                identity.Length, Math.Min(identity.Length, estimatedBytes), exact);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetIdentity(path, credential) != identity)
            {
                throw new IOException("The PDF changed while its compressed size was being estimated.");
            }

            entry.Estimates[strength] = estimate;
            if (node is null)
            {
                _cache.AddFirst(entry);
                _cachedSampleBytes += entry.Sample?.LongLength ?? 0;
            }

            TrimCache();
            return estimate;
        }
        finally
        {
            // Also enforce the bound when compression fails after caching a new sample.
            TrimCache();
            _gate.Release();
        }
    }

    // Call after pending estimates have completed or been canceled.
    public void Dispose()
    {
        _cache.Clear();
        _gate.Dispose();
    }

    private static FileIdentity GetIdentity(string path, string credential)
    {
        var file = new FileInfo(path);
        return new FileIdentity(path, file.LastWriteTimeUtc, file.Length, credential);
    }

    private static void EnsureSucceeded(OperationExecutionResult result)
    {
        if (!result.IsSucceeded)
        {
            throw new IOException($"{result.ErrorCode}: {result.ErrorMessage}");
        }
    }

    private void TrimCache()
    {
        while (_cache.Count > MaximumCachedFiles || _cachedSampleBytes > MaximumCachedSampleBytes)
        {
            _cachedSampleBytes -= _cache.Last!.Value.Sample?.LongLength ?? 0;
            _cache.RemoveLast();
        }
    }

    private sealed record FileIdentity(string Path, DateTime LastWriteTimeUtc, long Length, string Credential);

    private sealed class CacheEntry(FileIdentity identity, int pageCount)
    {
        public FileIdentity Identity { get; } = identity;
        public int PageCount { get; } = pageCount;
        public byte[]? Sample { get; set; }
        public Dictionary<int, PdfCompressionEstimate> Estimates { get; } = new();
    }
}
