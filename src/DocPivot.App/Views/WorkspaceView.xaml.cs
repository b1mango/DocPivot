using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
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
        var viewModel = DataContext as WorkspaceViewModel;
        var canAccept = viewModel is not null &&
            QueueDropAdmissions.TryGetDroppedPaths(e.Data, out var paths) &&
            QueueDropAdmissions.CanAcceptDrop(viewModel, paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        // Highlight whichever target is on screen: the empty-state well or the add row.
        var (zone, stroke) = viewModel?.HasFiles == true
            ? (AddRow, AddRowStroke)
            : (DropZoneShell, DropZoneStroke);
        zone.SetResourceReference(Border.BackgroundProperty, canAccept ? "AccentSubtleBrush" : "DangerSubtleBrush");
        stroke.SetResourceReference(Shape.StrokeProperty, canAccept ? "AccentBrush" : "DangerBrush");
    }

    private void ResetDropZone()
    {
        DropZoneShell.ClearValue(Border.BackgroundProperty);
        AddRow.ClearValue(Border.BackgroundProperty);
        DropZoneStroke.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
        AddRowStroke.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
    }
}
