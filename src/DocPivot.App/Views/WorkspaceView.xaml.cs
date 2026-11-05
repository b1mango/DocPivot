using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocPivot.App.Controls;
using DocPivot.App.ViewModels;

namespace DocPivot.App.Views;

/// <summary>
/// The workspace: an empty page with a single question, then the file list with a composer
/// docked under it. Everything here is view plumbing (focus, keys, menus); the decisions
/// live in <see cref="WorkspaceViewModel"/>.
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }

    /// <summary>Space the window toast should keep free so it never sits on the composer.</summary>
    public double ToastInset => WorkArea.IsVisible ? Composer.ActualHeight + Composer.Margin.Bottom + 14 : 48;

    /// <summary>
    /// Rename takes files or whole folders, so "+" offers both; the other tools go straight
    /// to the file picker.
    /// </summary>
    internal static void ShowAddMenu(FrameworkElement target, WorkspaceViewModel viewModel, PlacementMode placement)
    {
        if (!viewModel.IsBatchRenameTool)
        {
            if (viewModel.AddFilesCommand.CanExecute(null))
            {
                viewModel.AddFilesCommand.Execute(null);
            }

            return;
        }

        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Items.Add(CreateMenuItem("添加文件", "PhFileL", viewModel.AddFilesCommand));
        menu.Items.Add(CreateMenuItem("添加文件夹", "PhFolderSimplePlusL", viewModel.AddRenameFolderCommand));
        menu.IsOpen = true;
    }

    internal static MenuItem CreateMenuItem(string header, string icon, ICommand command)
    {
        var glyph = new Icon { Data = Application.Current.TryFindResource(icon) as Geometry };
        glyph.SetResourceReference(Icon.ForegroundProperty, "T2Brush");
        return new MenuItem { Header = header, Command = command, Icon = glyph };
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target && DataContext is WorkspaceViewModel viewModel)
        {
            // Inside the docked composer the menu opens upwards, elsewhere downwards.
            var placement = Composer.IsAncestorOf(target) ? PlacementMode.Top : PlacementMode.Bottom;
            ShowAddMenu(target, viewModel, placement);
        }
    }

    private void OnHeroPromptClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is WorkspaceViewModel viewModel)
        {
            ShowAddMenu(HeroPrompt, viewModel, PlacementMode.Bottom);
        }
    }

    // ----- Rows -----

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row)
        {
            return;
        }

        if (e.ClickCount == 2 && row.DataContext is QueuedFileViewModel { CanEditRenamePreview: true } file)
        {
            file.IsEditingRename = true;
            e.Handled = true;
            return;
        }

        // Clicking a row selects it for the keyboard (Delete, Alt+↑↓) without stealing
        // focus from a control inside it.
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source, row) is null && FindAncestor<TextBoxBase>(source, row) is null)
        {
            row.Focus();
        }
    }

    private void OnEditNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFileViewModel file })
        {
            file.IsEditingRename = true;
        }
    }

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or ComboBox ||
            Keyboard.FocusedElement is not FrameworkElement { DataContext: QueuedFileViewModel file } ||
            DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        ICommand? command = key switch
        {
            Key.Delete or Key.Back when !alt => viewModel.RemoveFileCommand,
            Key.Up when alt => viewModel.MoveFileUpCommand,
            Key.Down when alt => viewModel.MoveFileDownCommand,
            _ => null,
        };

        if (key == Key.F2 && file.CanEditRenamePreview)
        {
            file.IsEditingRename = true;
            e.Handled = true;
            return;
        }

        if (command is null || !command.CanExecute(file))
        {
            return;
        }

        var index = viewModel.Files.IndexOf(file);
        command.Execute(file);
        e.Handled = true;

        // Keep the keyboard on the list: the moved row, or the row that took the removed one's place.
        var next = viewModel.Files.Contains(file)
            ? file
            : viewModel.Files.Count == 0 ? null : viewModel.Files[Math.Min(index, viewModel.Files.Count - 1)];
        if (next is not null)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusRow(next));
        }
    }

    private void FocusRow(QueuedFileViewModel file)
    {
        if (FileList.ItemContainerGenerator.ContainerFromItem(file) is ContentPresenter presenter &&
            VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
            VisualTreeHelper.GetChild(presenter, 0) is UIElement row)
        {
            row.Focus();
        }
    }

    // ----- Inline rename -----

    private void OnNameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            Keyboard.Focus(box);

            // Select the stem so typing replaces the name and keeps the extension.
            var dot = box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        });
    }

    private void OnNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            EndNameEdit(box, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndNameEdit(box, commit: false);
            e.Handled = true;
        }
    }

    private void OnNameEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            EndNameEdit(box, commit: true);
        }
    }

    private static void EndNameEdit(TextBox box, bool commit)
    {
        if (box.DataContext is not QueuedFileViewModel { IsEditingRename: true } file)
        {
            return;
        }

        var binding = BindingOperations.GetBindingExpression(box, TextBox.TextProperty);
        if (commit && box.Text != file.RenamePreviewName)
        {
            binding?.UpdateSource();
        }
        else
        {
            binding?.UpdateTarget();
        }

        file.IsEditingRename = false;
    }

    private static T? FindAncestor<T>(DependencyObject start, DependencyObject stop)
        where T : DependencyObject
    {
        for (var current = start; current is not null && current != stop; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
