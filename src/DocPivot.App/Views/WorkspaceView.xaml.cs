using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;

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
        if (!QueueDropAdmissions.TryGetDroppedPaths(e.Data, out var paths))
        {
            viewModel.ReportImportFailure();
            return;
        }

        QueueDropAdmissions.AddDroppedPaths(viewModel, paths);
    }

    private void OnDropZoneMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is WorkspaceViewModel viewModel &&
            viewModel.AddFilesCommand.CanExecute(null))
        {
            viewModel.AddFilesCommand.Execute(null);
        }
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var canAccept = DataContext is WorkspaceViewModel viewModel &&
            QueueDropAdmissions.TryGetDroppedPaths(e.Data, out var paths) &&
            QueueDropAdmissions.CanAcceptDrop(viewModel, paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropZoneShell.Background = (Brush)FindResource(
            canAccept ? "AccentSubtleBrush" : "DangerSubtleBrush");
        DropZoneStroke.Stroke = (Brush)FindResource(canAccept ? "AccentBrush" : "DangerBrush");
    }

    private void ResetDropZone()
    {
        DropZoneShell.Background = (Brush)FindResource("SurfaceGlassBrush");
        DropZoneStroke.Stroke = (Brush)FindResource("BorderStrongBrush");
    }
}
