using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DocPivot.App.ViewModels;
using DocPivot.Core.Documents;

namespace DocPivot.App.Services;

/// <summary>Shared admission rules for files dropped onto the workspace.</summary>
internal static class QueueDropAdmissions
{
    public static void AddDroppedPaths(WorkspaceViewModel viewModel, string[] paths)
    {
        if (!CanAcceptDrop(viewModel, paths))
        {
            return;
        }

        try
        {
            viewModel.AddPaths(paths);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException)
        {
            viewModel.ReportImportFailure();
        }
    }

    public static bool CanAcceptDrop(WorkspaceViewModel viewModel, string[] paths) =>
        !viewModel.IsProcessing &&
        paths.Any(path => IsSupportedPath(viewModel.SelectedTool.Operation, path));

    public static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
    {
        paths = [];
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) ||
                data.GetData(DataFormats.FileDrop) is not string[] droppedPaths)
            {
                return false;
            }

            paths = droppedPaths;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    public static QueuedFileViewModel? ResolveFile(
        WorkspaceViewModel viewModel,
        PdfPageThumbnailViewModel thumbnail) =>
        thumbnail.SourceFilePath is null
            ? null
            : viewModel.Files.FirstOrDefault(file =>
                string.Equals(file.FullPath, thumbnail.SourceFilePath, StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedPath(DocumentOperation operation, string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            return Directory.Exists(path) || !string.IsNullOrWhiteSpace(extension) &&
                DocumentAdmissionPolicy.IsSupported(operation, extension);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
