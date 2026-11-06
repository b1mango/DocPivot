using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Office;

public sealed class ExcelWorksheetPreviewService : IExcelWorksheetPreviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPdfThumbnailRenderer _thumbnailRenderer;
    private readonly Func<object, CancellationToken, Task<ExternalProcessResult>> _runWorker;

    public ExcelWorksheetPreviewService(
        string executablePath,
        IPdfThumbnailRenderer thumbnailRenderer,
        IReadOnlyList<string>? argumentPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(thumbnailRenderer);
        _thumbnailRenderer = thumbnailRenderer;
        string[] arguments = [.. argumentPrefix ?? [], "--execute", "--timeout-seconds", "300"];
        _runWorker = (request, cancellationToken) => ExternalProcessRunner.RunAsync(
            executablePath, arguments, JsonSerializer.Serialize(request, JsonOptions),
            TimeSpan.FromSeconds(315), cancellationToken: cancellationToken);
    }

    internal ExcelWorksheetPreviewService(
        IPdfThumbnailRenderer thumbnailRenderer,
        Func<object, CancellationToken, Task<ExternalProcessResult>> runWorker)
    {
        _thumbnailRenderer = thumbnailRenderer;
        _runWorker = runWorker;
    }

    public async Task<IReadOnlyList<ExcelWorksheetPreview>> RenderPreviewsAsync(
        string workbookPath,
        string outputDirectory,
        int pixelWidth,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(workbookPath);
        var key = GetCacheKey(sourcePath, pixelWidth);
        var cacheDirectory = Path.Combine(Path.GetFullPath(outputDirectory), key);
        var manifestPath = Path.Combine(cacheDirectory, "previews.json");
        var cached = await ReadCacheAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var jobId = Guid.NewGuid();
        var renderDirectory = Path.Combine(cacheDirectory, jobId.ToString("N"));
        Directory.CreateDirectory(renderDirectory);
        var request = WorkerExcelPreviewStartMessage.Create(jobId, sourcePath, renderDirectory);
        var exported = await _runWorker(request, cancellationToken).ConfigureAwait(false);
        var message = ReadResponse<WorkerExcelPreviewsMessage>(exported, jobId, WorkerExcelPreviewsMessage.MessageType);
        var previews = new List<ExcelWorksheetPreview>(message.Worksheets.Count);
        foreach (var worksheet in message.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? imagePath = null;
            if (worksheet.PdfPath is not null)
            {
                try
                {
                    var images = await _thumbnailRenderer.RenderAsync(
                        worksheet.PdfPath, 1,
                        Path.Combine(renderDirectory, worksheet.Position.ToString(CultureInfo.InvariantCulture)),
                        firstPage: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
                    imagePath = images.Single();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
                {
                    // PDF export and rendering can fail independently for one sheet.
                }
            }

            previews.Add(new ExcelWorksheetPreview(worksheet.Name, worksheet.Position, imagePath, worksheet.PageCount));
        }

        var imagePaths = previews.Where(static preview => preview.ImagePath is not null)
            .Select(static preview => preview.ImagePath!).ToArray();
        if (imagePaths.Length > 0)
        {
            var resizeRequest = WorkerExcelPreviewResizeStartMessage.Create(Guid.NewGuid(), imagePaths, pixelWidth);
            var resized = await _runWorker(resizeRequest, cancellationToken).ConfigureAwait(false);
            var resizeResult = ReadResponse<WorkerResultMessage>(resized, resizeRequest.JobId, WorkerMessageTypes.Result);
            var successfulImages = resizeResult.Artifacts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < previews.Count; index++)
            {
                if (previews[index].ImagePath is { } path && !successfulImages.Contains(path))
                {
                    previews[index] = previews[index] with { ImagePath = null };
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (key == GetCacheKey(sourcePath, pixelWidth))
        {
            var temporaryManifest = Path.Combine(renderDirectory, "previews.json");
            await File.WriteAllTextAsync(temporaryManifest,
                JsonSerializer.Serialize(previews, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryManifest, manifestPath, overwrite: true);
        }

        return previews;
    }

    private static string GetCacheKey(string sourcePath, int pixelWidth)
    {
        var file = new FileInfo(sourcePath);
        var normalizedPath = OperatingSystem.IsWindows() ? sourcePath.ToUpperInvariant() : sourcePath;
        var identity = FormattableString.Invariant($"v1|{normalizedPath}|{file.LastWriteTimeUtc.Ticks}|{file.Length}|{pixelWidth}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static async Task<IReadOnlyList<ExcelWorksheetPreview>?> ReadCacheAsync(
        string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var previews = JsonSerializer.Deserialize<ExcelWorksheetPreview[]>(json, JsonOptions);
            return previews is not null && previews.All(static preview => preview.ImagePath is null || File.Exists(preview.ImagePath))
                ? previews : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static T ReadResponse<T>(ExternalProcessResult result, Guid jobId, string expectedType)
    {
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("v", out var version) || version.GetInt32() != WorkerProtocol.CurrentVersion ||
                    !root.TryGetProperty("jobId", out var job) || job.GetGuid() != jobId ||
                    !root.TryGetProperty("type", out var type))
                {
                    continue;
                }

                if (type.GetString() == WorkerMessageTypes.Error)
                {
                    var error = JsonSerializer.Deserialize<WorkerErrorMessage>(line, JsonOptions)!;
                    throw new InvalidOperationException(error.Message);
                }

                if (result.ExitCode == 0 && type.GetString() == expectedType)
                {
                    return JsonSerializer.Deserialize<T>(line, JsonOptions)
                        ?? throw new InvalidOperationException("The Excel preview worker returned an empty result.");
                }
            }
            catch (JsonException)
            {
                // Ignore non-protocol output from the external process.
            }
        }

        throw new InvalidOperationException("The Excel preview worker did not return a valid result.");
    }
}
