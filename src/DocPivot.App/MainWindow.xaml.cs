using System.ComponentModel;
using System.Windows;
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
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
}
