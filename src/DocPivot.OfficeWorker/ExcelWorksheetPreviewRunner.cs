using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;

namespace DocPivot.OfficeWorker;

internal static class ExcelWorksheetPreviewRunner
{
    public static int Run(
        WorkerExcelPreviewStartMessage request,
        Action<object> writeMessage,
        Action<int> reportProcessId)
    {
        try
        {
            ValidateProtocol(request.V, request.Type, request.Operation, OfficeWorkerOperations.RenderExcelWorksheetPreviews);
            if (string.IsNullOrWhiteSpace(request.Input) || string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new OfficeWorkerException("START_MESSAGE_INVALID", "Excel preview input and output paths are required.");
            }

            var inputPath = Path.GetFullPath(request.Input);
            var inspection = OfficeDocumentInspector.Inspect(inputPath);
            if (!inspection.IsValid || inspection.Kind is not (OfficeDocumentKind.ExcelBinary or OfficeDocumentKind.ExcelOpenXml))
            {
                throw new OfficeWorkerException(inspection.ErrorCode ?? "INPUT_INVALID", "The Excel preview input is invalid.");
            }

            var outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var previews = ExcelWorksheetPreviewExporter.Export(
                inputPath, outputDirectory, request.JobId, reportProcessId,
                stage => writeMessage(WorkerProgressMessage.Create(request.JobId, stage, 1, 1)));
            writeMessage(WorkerExcelPreviewsMessage.Create(request.JobId, previews));
            return 0;
        }
        catch (Exception exception) when (exception is OfficeWorkerException or COMException or IOException or UnauthorizedAccessException)
        {
            return WriteError(request.JobId, exception, writeMessage);
        }
    }

    public static int Run(WorkerExcelPreviewResizeStartMessage request, Action<object> writeMessage)
    {
        try
        {
            ValidateProtocol(request.V, request.Type, request.Operation, OfficeWorkerOperations.ResizeExcelWorksheetPreviews);
            if (request.ImagePaths is null || request.PixelWidth < 1 ||
                request.ImagePaths.Any(static path => string.IsNullOrWhiteSpace(path) ||
                    !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)))
            {
                throw new OfficeWorkerException("START_MESSAGE_INVALID", "PNG preview paths and a positive pixel width are required.");
            }

            var artifacts = new List<string>();
            foreach (var path in request.ImagePaths)
            {
                try
                {
                    ResizeImage(path, request.PixelWidth);
                    artifacts.Add(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
                {
                    // A failed preview image must not prevent the remaining sheet previews.
                }
            }

            writeMessage(WorkerResultMessage.Succeeded(request.JobId, artifacts));
            return 0;
        }
        catch (Exception exception) when (exception is OfficeWorkerException or IOException or UnauthorizedAccessException)
        {
            return WriteError(request.JobId, exception, writeMessage);
        }
    }

    private static void ResizeImage(string path, int pixelWidth)
    {
        BitmapFrame frame;
        using (var input = File.OpenRead(path))
        {
            frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        if (frame.PixelWidth == pixelWidth)
        {
            return;
        }

        var scale = (double)pixelWidth / frame.PixelWidth;
        var resized = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using var output = new MemoryStream();
        encoder.Save(output);
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void ValidateProtocol(int version, string type, string operation, string expectedOperation)
    {
        if (version != WorkerProtocol.CurrentVersion || type != WorkerMessageTypes.Start || operation != expectedOperation)
        {
            throw new OfficeWorkerException("START_MESSAGE_INVALID", "The Excel preview start message is invalid.");
        }
    }

    private static int WriteError(Guid jobId, Exception exception, Action<object> writeMessage)
    {
        var error = exception as OfficeWorkerException;
        writeMessage(WorkerErrorMessage.Create(jobId,
            error?.Code ?? "EXCEL_PREVIEW_FAILED", error?.Message ?? "The Excel previews could not be generated.",
            error?.Retryable ?? true));
        return 2;
    }
}
