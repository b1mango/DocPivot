using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultCloseCleanupTimeout = TimeSpan.FromSeconds(8);

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly TimeSpan _closeCleanupTimeout;
    private Task _initializationTask = Task.CompletedTask;
    private bool _closeAfterCancellation;
    private bool _allowClose;

    public MainWindow(
        IFilePickerService filePicker,
        IOfficeWorkerClient officeWorkerClient,
        IShellService shellService,
        IExcelOperationsClient? excelOperationsClient = null,
        IPdfOperationsClient? pdfOperationsClient = null,
        IBatchRenameExecutor? batchRenameExecutor = null,
        IPdfTableOperationsClient? pdfTableOperationsClient = null,
        IPdfThumbnailRenderer? pdfThumbnailRenderer = null,
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
            pdfThumbnailRenderer);
        SourceInitialized += OnSourceInitialized;
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

    private void OnClosed(object? sender, EventArgs e)
    {
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
        MaximizeGlyph.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "Icon_Restore" : "Icon_Maximize");
    }
}
