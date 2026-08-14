using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DocPivot.App.ViewModels;
using DocPivot.Core.Documents;
using GongSolutions.Wpf.DragDrop;

namespace DocPivot.App.Services;

/// <summary>
/// Attached property that marks the element acting as a drag grip. Queue rows
/// can be dragged from anywhere (including the file name); interactive
/// elements such as buttons and text editors inside a row never start a drag.
/// </summary>
public static class QueueDragDrop
{
    public static readonly DependencyProperty IsGripProperty =
        DependencyProperty.RegisterAttached(
            "IsGrip",
            typeof(bool),
            typeof(QueueDragDrop),
            new PropertyMetadata(false));

    public static bool GetIsGrip(DependencyObject element) => (bool)element.GetValue(IsGripProperty);

    public static void SetIsGrip(DependencyObject element, bool value) =>
        element.SetValue(IsGripProperty, value);
}

/// <summary>
/// Drag source that allows drags started anywhere on a queue row, except from
/// interactive controls (buttons, text boxes, scroll bars) inside the row.
/// </summary>
public sealed class QueueDragSource : IDragSource
{
    private static readonly DefaultDragHandler Inner = new();

    public bool CanStartDrag(IDragInfo dragInfo)
    {
        if (dragInfo is not GripDragInfo { PressedElement: DependencyObject pressed })
        {
            return false;
        }

        for (var current = pressed; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (QueueDragDrop.GetIsGrip(current))
            {
                return true;
            }

            if (current is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ScrollBar)
            {
                return false;
            }

            if (ReferenceEquals(current, dragInfo.VisualSourceItem))
            {
                return true;
            }
        }

        return false;
    }

    public void StartDrag(IDragInfo dragInfo) => Inner.StartDrag(dragInfo);

    public void Dropped(IDropInfo dropInfo) => Inner.Dropped(dropInfo);

    public void DragCancelled()
    {
    }

    public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo) =>
        Inner.DragDropOperationFinished(operationResult, dragInfo);

    public bool TryCatchOccurredException(Exception exception) =>
        Inner.TryCatchOccurredException(exception);
}

public sealed class GripDragInfoBuilder : IDragInfoBuilder
{
    public IDragInfo CreateDragInfo(
        object sender,
        object originalSource,
        MouseButton mouseButton,
        Func<IInputElement, Point> getPosition) =>
        new GripDragInfo(sender, originalSource, mouseButton, getPosition);
}

internal sealed class GripDragInfo(
    object sender,
    object originalSource,
    MouseButton mouseButton,
    Func<IInputElement, Point> getPosition)
    : DragInfo(sender, originalSource, mouseButton, getPosition)
{
    public object PressedElement { get; } = originalSource;
}

/// <summary>
/// Single drop target for every queue list: internal drags reorder files with
/// an insertion indicator (including past the last item), external file drops
/// go through the regular admission pipeline.
/// </summary>
public sealed class QueueDropTarget : IDropTarget
{
    public void DragEnter(IDropInfo dropInfo) => UpdateDragState(dropInfo);

    public void DragOver(IDropInfo dropInfo)
    {
        UpdateDragState(dropInfo);
        ApplyLiveReorder(dropInfo);
    }

    public void DragLeave(IDropInfo dropInfo)
    {
    }

    public void DropHint(IDropHintInfo dropHintInfo)
    {
    }

    public void Drop(IDropInfo dropInfo)
    {
        if (ResolveViewModel(dropInfo) is not { } viewModel)
        {
            return;
        }

        switch (dropInfo.Data)
        {
            case QueuedFileViewModel sourceFile:
                if (dropInfo.VisualTarget is not System.Windows.Controls.ItemsControl)
                {
                    // See UpdateDragState: internal drags are handled by the inner
                    // ItemsControl; the panel Border only accepts external files.
                    dropInfo.NotHandled = true;
                    return;
                }

                viewModel.MoveFileTo(sourceFile, MapInsertIndex(dropInfo, viewModel));
                return;
            case PdfPageThumbnailViewModel thumbnail:
                if (dropInfo.VisualTarget is not System.Windows.Controls.ItemsControl)
                {
                    dropInfo.NotHandled = true;
                    return;
                }

                var source = QueueDropAdmissions.ResolveFile(viewModel, thumbnail);
                if (source is not null)
                {
                    viewModel.MoveFileTo(source, MapInsertIndex(dropInfo, viewModel));
                }

                return;
        }

        if (dropInfo.Data is IDataObject data &&
            QueueDropAdmissions.TryGetDroppedPaths(data, out var paths))
        {
            QueueDropAdmissions.AddDroppedPaths(viewModel, paths);
        }
    }

    private static void UpdateDragState(IDropInfo dropInfo)
    {
        if (ResolveViewModel(dropInfo) is not { } viewModel)
        {
            dropInfo.Effects = DragDropEffects.None;
            return;
        }

        switch (dropInfo.Data)
        {
            case QueuedFileViewModel:
            case PdfPageThumbnailViewModel:
                if (dropInfo.VisualTarget is not System.Windows.Controls.ItemsControl)
                {
                    // Internal reorder drags belong to the queue list / thumbnail
                    // panel. The preview panel Border listens with tunneled events
                    // for external file drops; let internal drags pass through to
                    // the inner ItemsControl so insert indexes resolve correctly.
                    dropInfo.Effects = DragDropEffects.None;
                    dropInfo.DropTargetAdorner = null;
                    dropInfo.NotHandled = true;
                    return;
                }

                var canReorder = viewModel.IsMergeOrderingEnabled && !viewModel.IsProcessing;
                dropInfo.Effects = canReorder ? DragDropEffects.Move : DragDropEffects.None;
                dropInfo.DropTargetAdorner = canReorder ? DropTargetAdorners.Insert : null;
                return;
        }

        if (dropInfo.Data is IDataObject data &&
            QueueDropAdmissions.TryGetDroppedPaths(data, out var paths))
        {
            dropInfo.Effects = QueueDropAdmissions.CanAcceptDrop(viewModel, paths)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            dropInfo.DropTargetAdorner = null;
            return;
        }

        dropInfo.Effects = DragDropEffects.None;
    }

    /// <summary>
    /// Reorders the dragged file as soon as the pointer crosses another item,
    /// so the list reflects the target order during the drag instead of only
    /// on drop. No-ops while the pointer stays within the same slot.
    /// </summary>
    private static void ApplyLiveReorder(IDropInfo dropInfo)
    {
        if (dropInfo.Effects != DragDropEffects.Move ||
            ResolveViewModel(dropInfo) is not { } viewModel)
        {
            return;
        }

        switch (dropInfo.Data)
        {
            case QueuedFileViewModel sourceFile:
                viewModel.MoveFileTo(sourceFile, MapInsertIndex(dropInfo, viewModel));
                return;
            case PdfPageThumbnailViewModel thumbnail:
                var source = QueueDropAdmissions.ResolveFile(viewModel, thumbnail);
                if (source is not null)
                {
                    viewModel.MoveFileTo(source, MapInsertIndex(dropInfo, viewModel));
                }

                return;
        }
    }

    private static WorkspaceViewModel? ResolveViewModel(IDropInfo dropInfo) =>
        (dropInfo.VisualTarget as FrameworkElement)?.DataContext as WorkspaceViewModel;

    private static int MapInsertIndex(IDropInfo dropInfo, WorkspaceViewModel viewModel) =>
        // Drops on the panel background (outside any item) append to the end.
        dropInfo.TargetCollection is null ? viewModel.Files.Count : dropInfo.InsertIndex;
}

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
            return !string.IsNullOrWhiteSpace(extension) &&
                DocumentAdmissionPolicy.IsSupported(operation, extension);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
