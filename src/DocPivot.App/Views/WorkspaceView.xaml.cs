using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DocPivot.App.ViewModels;
using DocPivot.Core.Documents;

namespace DocPivot.App.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }

    private void OnDropZoneDragEnter(object sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnDropZoneDragLeave(object sender, DragEventArgs e)
    {
        ResetDropZone();
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        e.Handled = true;
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        if (!TryGetDroppedPaths(e.Data, out var paths))
        {
            viewModel.ReportImportFailure();
            return;
        }

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

    private void UpdateDragState(DragEventArgs e)
    {
        var canAccept = DataContext is WorkspaceViewModel viewModel &&
            TryGetDroppedPaths(e.Data, out var paths) &&
            CanAcceptDrop(viewModel, paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropZoneShell.Background = (Brush)FindResource(
            canAccept ? "AccentSubtleBrush" : "DangerSubtleBrush");
        DropZoneStroke.Stroke = (Brush)FindResource(canAccept ? "AccentBrush" : "DangerBrush");
    }

    private static bool CanAcceptDrop(WorkspaceViewModel viewModel, string[] paths) =>
        !viewModel.IsProcessing &&
        paths.Any(path => IsSupportedPath(viewModel.SelectedTool.Operation, path));

    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
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

    private static bool IsSupportedPath(DocumentOperation operation, string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) &&
                DocumentAdmissionPolicy.IsSupported(operation, extension);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void ResetDropZone()
    {
        DropZoneShell.Background = (Brush)FindResource("FillSubtleBrush");
        DropZoneStroke.Stroke = (Brush)FindResource("BorderStrongBrush");
    }
}
