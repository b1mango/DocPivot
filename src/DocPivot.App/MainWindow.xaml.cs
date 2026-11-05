using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DocPivot.App.Controls;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.App.Views;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Preferences;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultCloseCleanupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ToastHold = TimeSpan.FromSeconds(3);
    private static readonly Duration ToastFade = new(TimeSpan.FromMilliseconds(180));

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly TimeSpan _closeCleanupTimeout;
    private Task _initializationTask = Task.CompletedTask;
    private bool _closeAfterCancellation;
    private bool _allowClose;
    private readonly DispatcherTimer _toastTimer;

    public MainWindow(
        IFilePickerService filePicker,
        IOfficeWorkerClient officeWorkerClient,
        IShellService shellService,
        IExcelOperationsClient? excelOperationsClient = null,
        IPdfOperationsClient? pdfOperationsClient = null,
        IBatchRenameExecutor? batchRenameExecutor = null,
        IPdfTableOperationsClient? pdfTableOperationsClient = null,
        IPdfThumbnailRenderer? pdfThumbnailRenderer = null,
        IUserPreferencesStore? preferencesStore = null,
        TimeSpan? closeCleanupTimeout = null)
    {
        _closeCleanupTimeout = closeCleanupTimeout ?? DefaultCloseCleanupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_closeCleanupTimeout, TimeSpan.Zero);
        InitializeComponent();
        DataContext = new WorkspaceViewModel(
            filePicker,
            officeWorkerClient,
            shellService,
            excelOperationsClient ?? officeWorkerClient as IExcelOperationsClient,
            pdfOperationsClient,
            batchRenameExecutor,
            pdfTableOperationsClient,
            pdfThumbnailRenderer,
            preferencesStore);
        _toastTimer = new DispatcherTimer { Interval = ToastHold };
        _toastTimer.Tick += (_, _) => HideToast();
        if (DataContext is WorkspaceViewModel workspace)
        {
            workspace.PropertyChanged += OnWorkspacePropertyChanged;
        }

        PreviewDragEnter += OnWindowDragOver;
        PreviewDragOver += OnWindowDragOver;
        PreviewDragLeave += OnWindowDragLeave;
        PreviewDrop += OnWindowDrop;
        PreviewKeyDown += OnWindowKeyDown;
        Activated += OnWindowActivated;
        SourceInitialized += OnSourceInitialized;
        ThemeService.ThemeChanged += OnThemeChanged;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            _initializationTask = viewModel.InitializeAsync();
            await _initializationTask;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowBackdropService.ApplyTheme(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        _toastTimer.Stop();
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        var processingTask = viewModel.WaitForProcessingCompletionAsync();
        var worksheetDiscoveryTask = viewModel.WaitForWorksheetDiscoveryAsync();
        var pdfPreflightTask = viewModel.WaitForPdfPreflightAsync();
        var pdfPreviewTask = viewModel.WaitForPdfPreviewAsync();
        var renameUndoTask = viewModel.WaitForRenameUndoCompletionAsync();
        if (_initializationTask.IsCompleted &&
            processingTask.IsCompleted &&
            worksheetDiscoveryTask.IsCompleted &&
            pdfPreflightTask.IsCompleted &&
            pdfPreviewTask.IsCompleted &&
            renameUndoTask.IsCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_closeAfterCancellation)
        {
            return;
        }

        _closeAfterCancellation = true;
        IsEnabled = false;
        viewModel.RequestShutdown();
        try
        {
            await Task.WhenAll(
                    _initializationTask,
                    processingTask,
                    worksheetDiscoveryTask,
                    pdfPreflightTask,
                    pdfPreviewTask,
                    renameUndoTask)
                .WaitAsync(_closeCleanupTimeout);
        }
        catch (TimeoutException)
        {
            // Closing must remain bounded even if a worker ignores termination.
        }
        catch (Exception)
        {
            // Cleanup failures must not replace the user's close request.
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdropService.Apply(this);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                // Restrict the maximized window to the monitor work area so that
                // it never overlaps the taskbar or other reserved screen regions.
                var work = monitorInfo.WorkArea;
                var monitorRect = monitorInfo.MonitorArea;
                info.MaxPosition.X = work.Left - monitorRect.Left;
                info.MaxPosition.Y = work.Top - monitorRect.Top;
                info.MaxSize.X = work.Right - work.Left;
                info.MaxSize.Y = work.Bottom - work.Top;
                Marshal.StructureToPtr(info, lParam, fDeleteOld: true);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 Reserved;
        public Point32 MaxSize;
        public Point32 MaxPosition;
        public Point32 MinTrackSize;
        public Point32 MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 MonitorArea;
        public Rect32 WorkArea;
        public uint Flags;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        (DataContext as WorkspaceViewModel)?.RefreshRecentOutputTimes();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target && DataContext is WorkspaceViewModel viewModel)
        {
            WorkspaceView.ShowAddMenu(target, viewModel, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsSettingsOpen)
        {
            viewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+Z brings back the last removed files, but never steals undo from a text field.
        if (e.Key == Key.Z &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase &&
            viewModel.HasUndoableRemoval &&
            viewModel.UndoRemoveCommand.CanExecute(null))
        {
            viewModel.UndoRemoveCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ----- Status toast -----

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.StatusMessage) ||
            sender is not WorkspaceViewModel viewModel ||
            WorkspaceViewModel.IsQuietStatus(viewModel.StatusMessage))
        {
            return;
        }

        ShowToast(viewModel.StatusMessage);
    }

    private void ShowToast(string message)
    {
        StatusToastText.Text = message;
        StatusToast.ToolTip = message;
        StatusToast.Margin = new Thickness(0, 0, 0, Workspace.ToastInset);
        StatusToast.Visibility = Visibility.Visible;
        StatusToast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, ToastFade));
        StatusToastShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, ToastFade) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, ToastFade);
        fade.Completed += (_, _) =>
        {
            if (!_toastTimer.IsEnabled)
            {
                StatusToast.Visibility = Visibility.Collapsed;
            }
        };
        StatusToast.BeginAnimation(OpacityProperty, fade);
    }

    // ----- Whole-window drop -----

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not WorkspaceViewModel viewModel ||
            !QueueDropAdmissions.TryGetDroppedPaths(e.Data, out var paths))
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var canAccept = QueueDropAdmissions.CanAcceptDrop(viewModel, paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        var folders = paths.Count(System.IO.Directory.Exists);
        DropOverlayText.Text = canAccept
            ? folders == 0 ? $"松开添加 {paths.Length} 个文件" : $"松开添加 {paths.Length} 项"
            : "这些文件不能在这里处理";
        DropOverlayHint.Text = canAccept
            ? folders > 0 ? "文件夹里的可用文件会一并加入" : viewModel.SelectedTool.Title
            : $"{viewModel.SelectedTool.Title}支持 {viewModel.SelectedTool.AcceptedFormats}";
        DropOverlayStroke.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, canAccept ? "AccBrush" : "BadBrush");
        DropOverlayIcon.Data = (Geometry)FindResource(canAccept ? "PhTrayArrowDownL" : "PhWarningCircleL");
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer crosses into a child; only hide once it leaves the window.
        var point = e.GetPosition(Root);
        if (point.X <= 0 || point.Y <= 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
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

        if (viewModel.IsSettingsOpen)
        {
            viewModel.CloseSettingsCommand.Execute(null);
        }

        QueueDropAdmissions.AddDroppedPaths(viewModel, paths);
    }
}
