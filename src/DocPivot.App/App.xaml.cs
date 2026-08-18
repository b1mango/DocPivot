using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocPivot.App.Services;
using DocPivot.Infrastructure.Diagnostics;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App;

public partial class App : Application
{
    private StructuredDiagnosticLog? _diagnosticLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--office-worker", StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(DocPivot.OfficeWorker.Program.Run(e.Args[1..]));
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--pdf-table-worker", StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(DocPivot.PdfWorker.Program.Run(e.Args[1..]));
            return;
        }

        base.OnStartup(e);

        _diagnosticLog = CreateDiagnosticLog();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 DocPivot 可执行文件路径。");
        var officeWorkerClient = new OfficeWorkerClient(executablePath, ["--office-worker"]);
        var distributionRoot = FindDistributionRoot(executablePath);
        var pdfOperationsClient = new CompositePdfOperationsClient(distributionRoot);
        var pdfTableOperationsClient = new PdfTableWorkerClient(
            executablePath,
            distributionRoot,
            ["--pdf-table-worker"]);
        var pdfThumbnailRenderer = new GhostscriptPdfThumbnailRenderer(distributionRoot);
        var window = new MainWindow(
            new WindowsFilePickerService(),
            officeWorkerClient,
            new WindowsShellService(),
            officeWorkerClient,
            pdfOperationsClient,
            new BatchRenameExecutor(),
            pdfTableOperationsClient,
            pdfThumbnailRenderer);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Interlocked.Exchange(ref _diagnosticLog, null)?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteException("ui-unhandled-exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "文枢遇到无法恢复的界面错误，诊断信息已保存。请重新启动应用。",
            "文枢 DocPivot",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteException("process-unhandled-exception", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException("task-unobserved-exception", e.Exception);
        e.SetObserved();
    }

    private void WriteException(string eventName, Exception exception)
    {
        try
        {
            _diagnosticLog?.WriteAsync(
                DiagnosticLevel.Error,
                eventName,
                exception.ToString(),
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = exception.GetType().FullName,
                }).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Diagnostics must never replace the original failure.
        }
    }

    private static StructuredDiagnosticLog? CreateDiagnosticLog()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocPivot",
                "logs");
            return new StructuredDiagnosticLog(Path.Combine(directory, "app.ndjson"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FindDistributionRoot(string executablePath)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath)
            ?? AppContext.BaseDirectory;
        return PdfRuntimeDistributionRoot.Resolve(executableDirectory);
    }
}
