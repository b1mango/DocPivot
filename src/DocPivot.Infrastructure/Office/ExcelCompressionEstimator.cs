using System.Collections.Concurrent;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Core.Excel;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Office;

public sealed class ExcelCompressionEstimator : IExcelCompressionEstimator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(60);
    private readonly string _executablePath;
    private readonly string[] _argumentPrefix;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<EstimateKey, ExcelCompressionEstimate> _estimates = new();

    public ExcelCompressionEstimator(
        string executablePath,
        IReadOnlyList<string>? argumentPrefix = null,
        string? cacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _argumentPrefix = argumentPrefix?.ToArray() ?? [];
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Path.GetTempPath(), "DocPivot", "excel-compression-estimates", "v1");
    }

    public async Task<ExcelCompressionEstimate> EstimateAsync(
        string workbookPath,
        int strength,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        _ = ExcelCompressionProfile.FromStrength(strength);
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(workbookPath);
        var key = new EstimateKey(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length, strength);
        if (!string.Equals(file.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return new ExcelCompressionEstimate(file.Length, file.Length);
        }

        if (_estimates.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var request = WorkerExcelCompressionEstimateStartMessage.Create(
            Guid.NewGuid(),
            file.FullName,
            strength,
            _cacheDirectory);
        var result = await ExternalProcessRunner.RunAsync(
            _executablePath,
            [.. _argumentPrefix, "--execute", "--timeout-seconds", "60"],
            JsonSerializer.Serialize(request, JsonOptions),
            WorkerTimeout + TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        WorkerExcelCompressionEstimateMessage? estimate = null;
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("v", out var version) || version.GetInt32() != WorkerProtocol.CurrentVersion ||
                !root.TryGetProperty("jobId", out var jobId) || jobId.GetGuid() != request.JobId)
            {
                continue;
            }

            var type = root.GetProperty("type").GetString();
            if (type == WorkerMessageTypes.Error)
            {
                var error = root.Deserialize<WorkerErrorMessage>(JsonOptions)!;
                throw new InvalidOperationException($"{error.Code}: {error.Message}");
            }

            if (type == WorkerExcelCompressionEstimateMessage.MessageType)
            {
                estimate = root.Deserialize<WorkerExcelCompressionEstimateMessage>(JsonOptions);
            }
        }

        if (result.ExitCode != 0 || estimate is null || estimate.OriginalBytes != key.Length ||
            estimate.EstimatedBytes < 0 || estimate.EstimatedBytes > estimate.OriginalBytes)
        {
            throw new InvalidOperationException("Excel compression worker did not return a valid estimate.");
        }

        var value = new ExcelCompressionEstimate(estimate.OriginalBytes, estimate.EstimatedBytes);
        _estimates[key] = value;
        return value;
    }

    private sealed record EstimateKey(string Path, long LastWriteTicks, long Length, int Strength);
}
