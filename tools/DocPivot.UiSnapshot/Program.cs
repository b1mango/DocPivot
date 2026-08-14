using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DocPivot.App;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Renaming;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Tables;
using DocPivotApplication = DocPivot.App.App;

namespace DocPivot.UiSnapshot;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputDirectory = Path.Combine(repositoryRoot, "artifacts", "ui-snapshots");
        PrepareOutputDirectory(repositoryRoot, outputDirectory);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "DocPivot.UiSnapshot",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);

        var application = new DocPivotApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        application.InitializeComponent();

        try
        {
            if (Environment.GetEnvironmentVariable("DOCPIVOT_SNAPSHOT_ONLY_DRAG") == "1")
            {
                CaptureDragReorderScenario(application, outputDirectory);
                return 0;
            }

            CaptureScenario(application, outputDirectory, "01-empty.png", static _ => { });
            CaptureScenario(
                application,
                outputDirectory,
                "14-settings.png",
                static viewModel => viewModel.OpenSettingsCommand.Execute(null));
            CaptureScenario(
                application,
                outputDirectory,
                "02-queued-long-name.png",
                viewModel => ConfigureQueuedScenario(viewModel, fixtureRoot));
            CaptureScenario(
                application,
                outputDirectory,
                "03-mixed-states.png",
                viewModel => ConfigureMixedScenario(viewModel, fixtureRoot));
            CaptureScenario(
                application,
                outputDirectory,
                "04-minimum-size.png",
                viewModel => ConfigureQueuedScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "05-pdf-compression-minimum-size.png",
                ConfigurePdfCompressionScenario,
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "06-batch-rename-minimum-size.png",
                viewModel => ConfigureBatchRenameScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "07-batch-rename-insert-minimum-size.png",
                viewModel => ConfigureBatchRenameInsertScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "08-batch-rename-numbering-minimum-size.png",
                viewModel => ConfigureBatchRenameNumberingScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "10-excel-compression-minimum-size.png",
                viewModel => ConfigureExcelCompressionScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "11-excel-merge-minimum-size.png",
                viewModel => ConfigureExcelMergeScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "12-pdf-to-excel-minimum-size.png",
                viewModel => ConfigurePdfToExcelScenario(viewModel, fixtureRoot),
                1060,
                680);
            CaptureScenario(
                application,
                outputDirectory,
                "13-pdf-split-visual.png",
                viewModel => ConfigurePdfVisualSplitScenario(viewModel, fixtureRoot));
            CaptureScenario(
                application,
                outputDirectory,
                "15-pdf-merge-visual.png",
                viewModel => ConfigurePdfMergeVisualScenario(viewModel, fixtureRoot));
            CaptureRealFilesScenario(application, outputDirectory);
            CaptureRealSplitScenario(application, outputDirectory);
            // The drag scenario moves the real mouse cursor; opt in explicitly.
            if (Environment.GetEnvironmentVariable("DOCPIVOT_SNAPSHOT_DRAG") == "1")
            {
                CaptureDragReorderScenario(application, outputDirectory);
            }
        }
        finally
        {
            application.Shutdown();
            DeleteFixtureDirectory(fixtureRoot);
        }

        Console.WriteLine($"UI snapshots created: {outputDirectory}");
        return 0;
    }

    private static void CaptureScenario(
        Application application,
        string outputDirectory,
        string fileName,
        Action<WorkspaceViewModel> configure,
        double width = 1360,
        double height = 820)
    {
        var window = new MainWindow(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: new FakePdfOperationsClient(),
            excelOperationsClient: new FakeExcelOperationsClient(),
            batchRenameExecutor: new BatchRenameExecutor(),
            pdfTableOperationsClient: new FakePdfTableOperationsClient())
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            Width = width,
            Height = height,
        };
        window.Background = (Brush)application.FindResource("WindowFallbackBrush");
        application.MainWindow = window;

        var viewModel = (WorkspaceViewModel)window.DataContext;
        configure(viewModel);

        try
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            EnsureBitmapIsNonBlank(bitmap, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(
                Path.Combine(outputDirectory, fileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
            if (stream.Length < 4_096)
            {
                throw new InvalidOperationException($"UI snapshot {fileName} is unexpectedly small.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureRealSplitScenario(Application application, string outputDirectory)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var splitFile = Path.Combine(
            desktop,
            "诚诚矿业-都成上海铅精矿结算资料CCKY-XS-Pb20250913-001 DCSH-CG-Pb20250913-001.pdf");
        if (!File.Exists(splitFile))
        {
            Console.WriteLine("real-split scenario skipped: desktop fixture missing");
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var window = new MainWindow(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: new FakePdfOperationsClient(pageCount: 26),
            excelOperationsClient: new FakeExcelOperationsClient(),
            batchRenameExecutor: new BatchRenameExecutor(),
            pdfTableOperationsClient: new FakePdfTableOperationsClient(),
            pdfThumbnailRenderer: new GhostscriptPdfThumbnailRenderer(repositoryRoot))
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            Width = 1360,
            Height = 820,
        };
        window.Background = (Brush)application.FindResource("WindowFallbackBrush");
        application.MainWindow = window;

        var viewModel = (WorkspaceViewModel)window.DataContext;
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Split);
        viewModel.SelectedPdfSplitMode = AssertSingle(
            viewModel.PdfSplitModes,
            static option => option.Mode == PdfSplitMode.VisualCuts);
        viewModel.AddPaths([splitFile]);

        try
        {
            window.Show();
            var deadline = DateTime.UtcNow.AddSeconds(150);
            while (viewModel.PdfPreviewStatus != "点击页间剪刀设置拆分位置" &&
                DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(500),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    static (_, _) => { },
                    window.Dispatcher);
                timer.Tick += (_, _) =>
                {
                    frame.Continue = false;
                    timer.Stop();
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }

            Console.WriteLine(
                $"[split] thumbs={viewModel.PdfPageThumbnails.Count} status={viewModel.PdfPreviewStatus}");
            if (viewModel.Files.Count > 0)
            {
                var first = viewModel.Files[0];
                Console.WriteLine(
                    $"[split] file state={first.State} pages={first.PdfPageCount} " +
                    $"preflight={first.PdfPreflightStatusText} resolved={first.IsPdfPreflightResolved} " +
                    $"visual={viewModel.IsPdfVisualSplitMode} show={viewModel.ShowPdfVisualPreview}");
            }
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(
                Path.Combine(outputDirectory, "98-real-split.png"),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureRealFilesScenario(Application application, string outputDirectory)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var realFiles = new[]
        {
            "河池泰和-都成上海过磅单明细及水分确认单（双章）.pdf",
            "河池泰和-都成上海锌精矿结算单HCTH-XS-Zn20260624-001  DCSH-CG-Zn20260624-001（双章）.pdf",
            "河池泰和-都成上海锌精矿购销合同HCTH-XS-20260624-001  DCSH-CG-Zn20260624-001（双章）.pdf",
            "河池泰和-都成上海锌精矿预结算报告499.12t（双章）.pdf",
            "河池泰和-都成上海锌精矿过磅单499.12t（盖章）.pdf",
        };
        var paths = realFiles.Select(name => Path.Combine(desktop, name)).ToArray();
        if (paths.Any(path => !File.Exists(path)))
        {
            Console.WriteLine("real-files scenario skipped: desktop fixtures missing");
            return;
        }

        var window = new MainWindow(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: new FakePdfOperationsClient(),
            excelOperationsClient: new FakeExcelOperationsClient(),
            batchRenameExecutor: new BatchRenameExecutor(),
            pdfTableOperationsClient: new FakePdfTableOperationsClient(),
            pdfThumbnailRenderer: new GhostscriptPdfThumbnailRenderer(FindRepositoryRoot()))
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            Width = 1360,
            Height = 820,
        };
        window.Background = (Brush)application.FindResource("WindowFallbackBrush");
        application.MainWindow = window;

        var viewModel = (WorkspaceViewModel)window.DataContext;
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Merge);
        viewModel.AddPaths(paths);

        try
        {
            window.Show();
            var waitTask = Task.Run(
                async () => await viewModel.WaitForPdfPreviewAsync().WaitAsync(TimeSpan.FromSeconds(75)));
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!waitTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(500),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    static (_, _) => { },
                    window.Dispatcher);
                timer.Tick += (_, _) =>
                {
                    frame.Continue = false;
                    timer.Stop();
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
                Console.WriteLine($"[probe] thumbs={viewModel.PdfPageThumbnails.Count} status={viewModel.PdfPreviewStatus}");
            }

            Console.WriteLine($"[probe] done thumbs={viewModel.PdfPageThumbnails.Count} status={viewModel.PdfPreviewStatus}");
            try
            {
                waitTask.GetAwaiter().GetResult();
                Console.WriteLine("[probe] wait task completed cleanly");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[probe] wait task FAULTED: {exception}");
            }

            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            foreach (var thumb in viewModel.PdfPageThumbnails)
            {
                if (thumb.Thumbnail is BitmapSource thumbSource)
                {
                    Console.WriteLine(
                        $"[vm] thumb {thumb.DisplayLabel}: {thumbSource.PixelWidth}x{thumbSource.PixelHeight} " +
                        $"dpi={thumbSource.DpiX:F1}x{thumbSource.DpiY:F1} rot={thumb.Rotation} type={thumbSource.GetType().Name}");
                }
            }

            DumpVisualTreeDiagnostics(window);
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(
                Path.Combine(outputDirectory, "99-real-files.png"),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MouseeventfLeftdown = 0x02;
    private const uint MouseeventfLeftup = 0x04;

    private static void CaptureDragReorderScenario(Application application, string outputDirectory)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var realFiles = new[]
        {
            "河池泰和-都成上海过磅单明细及水分确认单（双章）.pdf",
            "河池泰和-都成上海锌精矿结算单HCTH-XS-Zn20260624-001  DCSH-CG-Zn20260624-001（双章）.pdf",
            "河池泰和-都成上海锌精矿购销合同HCTH-XS-20260624-001  DCSH-CG-Zn20260624-001（双章）.pdf",
            "河池泰和-都成上海锌精矿预结算报告499.12t（双章）.pdf",
            "河池泰和-都成上海锌精矿过磅单499.12t（盖章）.pdf",
        };
        var paths = realFiles.Select(name => Path.Combine(desktop, name)).ToArray();
        if (paths.Any(path => !File.Exists(path)))
        {
            Console.WriteLine("drag-reorder scenario skipped: desktop fixtures missing");
            return;
        }

        var window = new MainWindow(
            new FakeFilePickerService(),
            new FakeOfficeWorkerClient(),
            new FakeShellService(),
            pdfOperationsClient: new FakePdfOperationsClient(),
            excelOperationsClient: new FakeExcelOperationsClient(),
            batchRenameExecutor: new BatchRenameExecutor(),
            pdfTableOperationsClient: new FakePdfTableOperationsClient(),
            pdfThumbnailRenderer: new GhostscriptPdfThumbnailRenderer(FindRepositoryRoot()))
        {
            ShowActivated = true,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60,
            Top = 60,
            Width = 1360,
            Height = 820,
        };
        window.Background = (Brush)application.FindResource("WindowFallbackBrush");
        application.MainWindow = window;

        var viewModel = (WorkspaceViewModel)window.DataContext;
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Merge);
        viewModel.AddPaths(paths);

        try
        {
            window.Show();
            var readyDeadline = DateTime.UtcNow.AddSeconds(90);
            while (viewModel.PdfPageThumbnails.Count < 5 && DateTime.UtcNow < readyDeadline)
            {
                PumpFrame(window, 500);
            }

            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var fileList = FindVisualDescendants<ItemsControl>(window)
                .FirstOrDefault(itemsControl =>
                    itemsControl.IsVisible &&
                    GongSolutions.Wpf.DragDrop.DragDrop.GetDragInfoBuilder(itemsControl) is not null);
            Console.WriteLine($"[dragtest] fileList found={fileList is not null} items={fileList?.Items.Count ?? -1}");
            if (fileList is null || fileList.Items.Count < 4)
            {
                return;
            }

            var container0 = (FrameworkElement)fileList.ItemContainerGenerator.ContainerFromIndex(0)!;
            var container3 = (FrameworkElement)fileList.ItemContainerGenerator.ContainerFromIndex(3)!;
            var p0 = container0.PointToScreen(new Point(container0.ActualWidth / 2, container0.ActualHeight / 2));
            var p3 = container3.PointToScreen(new Point(container3.ActualWidth / 2, container3.ActualHeight / 2));
            Console.WriteLine($"[dragtest] row0=({p0.X:F0},{p0.Y:F0}) row3=({p3.X:F0},{p3.Y:F0})");
            Console.WriteLine("[dragtest] order before: " + string.Join(" | ", viewModel.Files.Select(file => file.FileName)));

            var screenTopLeft = window.PointToScreen(new Point(0, 0));
            var screenBottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            var screenRect = new System.Drawing.Rectangle(
                (int)screenTopLeft.X,
                (int)screenTopLeft.Y,
                (int)(screenBottomRight.X - screenTopLeft.X),
                (int)(screenBottomRight.Y - screenTopLeft.Y));
            Console.WriteLine($"[dragtest] screenRect={screenRect}");

            var dragTask = Task.Run(() =>
            {
                Thread.Sleep(400);
                SetCursorPos((int)p0.X, (int)p0.Y);
                Thread.Sleep(150);
                mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(250);
                const int steps = 30;
                for (var step = 1; step <= steps; step++)
                {
                    var x = p0.X + (p3.X - p0.X) * step / steps;
                    var y = p0.Y + (p3.Y - p0.Y) * step / steps;
                    SetCursorPos((int)x, (int)y);
                    Thread.Sleep(50);
                    if (step == 15)
                    {
                        CaptureScreenRegion(screenRect, Path.Combine(outputDirectory, "96-drag-mid.png"));
                    }
                }

                Thread.Sleep(300);
                mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            });

            var dragDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!dragTask.IsCompleted && DateTime.UtcNow < dragDeadline)
            {
                PumpFrame(window, 200);
            }

            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine("[dragtest] order after:  " + string.Join(" | ", viewModel.Files.Select(file => file.FileName)));
            SaveWindowBitmap(window, Path.Combine(outputDirectory, "97-drag-after.png"));

            var thumbnailPanel = FindVisualDescendants<ItemsControl>(window)
                .FirstOrDefault(itemsControl =>
                    itemsControl.IsVisible &&
                    itemsControl.Items.Count > 0 &&
                    itemsControl.Items[0] is PdfPageThumbnailViewModel);
            Console.WriteLine($"[dragtest] thumbPanel found={thumbnailPanel is not null} items={thumbnailPanel?.Items.Count ?? -1}");
            if (thumbnailPanel is null || thumbnailPanel.Items.Count < 3)
            {
                return;
            }

            var thumb0 = (FrameworkElement)thumbnailPanel.ItemContainerGenerator.ContainerFromIndex(0)!;
            var thumb2 = (FrameworkElement)thumbnailPanel.ItemContainerGenerator.ContainerFromIndex(2)!;
            var t0 = thumb0.PointToScreen(new Point(thumb0.ActualWidth / 2, thumb0.ActualHeight / 2));
            var t2 = thumb2.PointToScreen(new Point(thumb2.ActualWidth / 2, thumb2.ActualHeight / 2));
            Console.WriteLine($"[dragtest] thumb0=({t0.X:F0},{t0.Y:F0}) thumb2=({t2.X:F0},{t2.Y:F0})");

            var thumbDragTask = Task.Run(() =>
            {
                Thread.Sleep(400);
                SetCursorPos((int)t0.X, (int)t0.Y);
                Thread.Sleep(150);
                mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(250);
                const int steps = 30;
                for (var step = 1; step <= steps; step++)
                {
                    var x = t0.X + (t2.X - t0.X) * step / steps;
                    var y = t0.Y + (t2.Y - t0.Y) * step / steps;
                    SetCursorPos((int)x, (int)y);
                    Thread.Sleep(50);
                    if (step == 15)
                    {
                        CaptureScreenRegion(screenRect, Path.Combine(outputDirectory, "95-thumb-drag-mid.png"));
                    }
                }

                Thread.Sleep(300);
                mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            });

            var thumbDragDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!thumbDragTask.IsCompleted && DateTime.UtcNow < thumbDragDeadline)
            {
                PumpFrame(window, 200);
            }

            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine("[dragtest] order after thumbnail drag: " + string.Join(" | ", viewModel.Files.Select(file => file.FileName)));
            SaveWindowBitmap(window, Path.Combine(outputDirectory, "94-thumb-drag-after.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureScreenRegion(System.Drawing.Rectangle rect, string path)
    {
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, rect.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"[dragtest] saved {Path.GetFileName(path)} (screen)");
    }

    private static void PumpFrame(Window window, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Normal,
            static (_, _) => { },
            window.Dispatcher);
        timer.Tick += (_, _) =>
        {
            frame.Continue = false;
            timer.Stop();
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SaveWindowBitmap(Window window, string path)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth));
        var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        Console.WriteLine($"[dragtest] saved {Path.GetFileName(path)}");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is T match)
            {
                yield return match;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var childIndex = 0; childIndex < childCount; childIndex++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, childIndex));
            }
        }
    }

    private static void DumpVisualTreeDiagnostics(DependencyObject root)
    {
        var index = 0;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is System.Windows.Controls.Image image &&
                image.Source is BitmapSource source)
            {
                var parent = System.Windows.Media.VisualTreeHelper.GetParent(image) as FrameworkElement;
                var grandparent = parent is not null
                    ? System.Windows.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement
                    : null;
                Console.WriteLine(
                    $"[tree] image{index++}: src={source.PixelWidth}x{source.PixelHeight} " +
                    $"dpi={source.DpiX:F0}x{source.DpiY:F0} fmt={source.Format} " +
                    $"img={image.ActualWidth:F0}x{image.ActualHeight:F0} " +
                    $"parent({parent?.GetType().Name})={parent?.ActualWidth:F0}x{parent?.ActualHeight:F0} " +
                    $"grand({grandparent?.GetType().Name})={grandparent?.ActualWidth:F0}x{grandparent?.ActualHeight:F0}");
            }

            var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var childIndex = 0; childIndex < childCount; childIndex++)
            {
                pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, childIndex));
            }
        }
    }

    private static void EnsureBitmapIsNonBlank(RenderTargetBitmap bitmap, string fileName)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * bitmap.PixelHeight));
        bitmap.CopyPixels(pixels, stride, 0);

        var visiblePixels = 0;
        byte minimumChannel = byte.MaxValue;
        byte maximumChannel = byte.MinValue;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            {
                continue;
            }

            visiblePixels++;
            minimumChannel = Math.Min(
                minimumChannel,
                Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2])));
            maximumChannel = Math.Max(
                maximumChannel,
                Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])));
        }

        var minimumVisiblePixels = checked(bitmap.PixelWidth * bitmap.PixelHeight / 2);
        if (visiblePixels < minimumVisiblePixels || maximumChannel - minimumChannel < 64)
        {
            throw new InvalidOperationException($"UI snapshot {fileName} is blank or has insufficient pixel range.");
        }
    }

    private static void ConfigureQueuedScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        var longName = $"quarterly-consolidation-{new string('x', 96)}.xlsx";
        var inputPath = CreateFixture(fixtureRoot, longName);
        viewModel.AddPaths([inputPath]);
        viewModel.StatusMessage = "已添加 1 个文件";
    }

    private static void ConfigureMixedScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        var processingPath = CreateFixture(fixtureRoot, "processing.docx");
        var completedPath = CreateFixture(fixtureRoot, "completed.xlsx");
        var failedPath = CreateFixture(fixtureRoot, "failed.docx");
        viewModel.AddPaths([processingPath, completedPath, failedPath]);

        var processing = viewModel.Files[0];
        processing.MarkValidating();
        processing.MarkRunning();
        processing.ReportProgress(WorkerProgressMessage.Create(Guid.NewGuid(), "office-export", 2, 5));

        var completed = viewModel.Files[1];
        completed.MarkValidating();
        completed.MarkRunning();
        completed.MarkSucceeded(Path.Combine(fixtureRoot, "completed.pdf"));

        var failed = viewModel.Files[2];
        failed.MarkValidating();
        failed.MarkRunning();
        failed.MarkFailed("OFFICE_COM_FAILURE", "Office 导出失败，请检查源文件。");

        viewModel.IsProcessing = true;
        viewModel.StatusMessage = "正在处理 1 / 3";
    }

    private static void ConfigurePdfCompressionScenario(WorkspaceViewModel viewModel)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Compress);
        viewModel.PdfCompressionStrength = 72;
        viewModel.StatusMessage = "PDF 压缩参数已就绪";
    }

    private static void ConfigureExcelCompressionScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = AssertSingle(
            viewModel.ExcelToolModes,
            static option => option.Mode == ExcelToolMode.Compress);
        viewModel.SelectedExcelImageCompressionProfile = AssertSingle(
            viewModel.ExcelImageCompressionProfiles,
            static option => option.Level == DocPivot.Core.Excel.ExcelImageCompressionLevel.Balanced);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "季度销售汇总.xlsx"),
            CreateFixture(fixtureRoot, "产品图册与报价.xlsx"),
        ]);
        viewModel.StatusMessage = "Excel 兼容性预检已完成";
    }

    private static void ConfigureExcelMergeScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.ExcelOperations);
        viewModel.SelectedExcelToolMode = AssertSingle(
            viewModel.ExcelToolModes,
            static option => option.Mode == ExcelToolMode.Merge);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "华东季度销售.xlsx"),
            CreateFixture(fixtureRoot, "华南季度销售.xlsx"),
        ]);
        viewModel.StatusMessage = "合并名称预览已生成";
    }

    private static void ConfigurePdfToExcelScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfToExcel);
        viewModel.AddPaths([CreateFixture(fixtureRoot, "扫描报表与明细.pdf")]);
        viewModel.StatusMessage = "PDF 表格提取参数已就绪";
    }

    private static void ConfigurePdfVisualSplitScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Split);
        viewModel.SelectedPdfSplitMode = AssertSingle(
            viewModel.PdfSplitModes,
            static option => option.Mode == PdfSplitMode.VisualCuts);
        viewModel.AddPaths([CreateFixture(fixtureRoot, "pdf-split-visual.pdf")]);

        var thumbnails = CreatePdfThumbnailFixtures(fixtureRoot, count: 6);
        for (var index = 0; index < thumbnails.Count; index++)
        {
            viewModel.PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(index + 1, thumbnails[index])
            {
                IsCutBefore = index is 2 or 4,
            });
        }

        viewModel.PdfSplitOutputAsZip = true;
        viewModel.StatusMessage = "PDF 可视化拆分预览已就绪";
    }

    private static void ConfigurePdfMergeVisualScenario(
        WorkspaceViewModel viewModel,
        string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.PdfOperations);
        viewModel.SelectedPdfToolMode = AssertSingle(
            viewModel.PdfToolModes,
            static option => option.Mode == PdfToolMode.Merge);
        var queuedPaths = new[]
        {
            CreateFixture(fixtureRoot, "源文档.pdf"),
            CreateFixture(fixtureRoot, "横向文档.pdf"),
            CreateFixture(fixtureRoot, "旋转文档.pdf"),
            CreateFixture(fixtureRoot, "对照文档.pdf"),
        };
        viewModel.AddPaths(queuedPaths);
        viewModel.PdfMergeOutputName = "合并输出";
        viewModel.StatusMessage = "PDF 合并可视化预览已就绪";

        var repositoryRoot = FindRepositoryRoot();
        var renderDirectory = Path.Combine(fixtureRoot, "merge-render");
        Directory.CreateDirectory(renderDirectory);
        var portraitPdf = Path.Combine(renderDirectory, "portrait.pdf");
        var landscapePdf = Path.Combine(renderDirectory, "landscape.pdf");
        CreateMinimalPdf(portraitPdf, "PORTRAIT", mediaBoxWidth: 300, mediaBoxHeight: 400);
        CreateMinimalPdf(landscapePdf, "LANDSCAPE", mediaBoxWidth: 400, mediaBoxHeight: 300);
        var renderer = new GhostscriptPdfThumbnailRenderer(repositoryRoot);
        AddRenderedMergeCard(
            viewModel,
            renderer,
            portraitPdf,
            renderDirectory,
            queuedPaths[0],
            Path.GetFileName(queuedPaths[0]),
            sourceFileIndex: 0,
            rotation: 0);
        AddRenderedMergeCard(
            viewModel,
            renderer,
            landscapePdf,
            renderDirectory,
            queuedPaths[1],
            Path.GetFileName(queuedPaths[1]),
            sourceFileIndex: 1,
            rotation: 0);
        AddRenderedMergeCard(
            viewModel,
            renderer,
            portraitPdf,
            renderDirectory,
            queuedPaths[2],
            Path.GetFileName(queuedPaths[2]),
            sourceFileIndex: 2,
            rotation: 90);
        // Control card: the synthetic PNGs used by the split scenario, loaded
        // through the exact same PdfPageThumbnailViewModel path.
        var synthetic = CreatePdfThumbnailFixtures(fixtureRoot, count: 1, namePrefix: "merge-control");
        viewModel.PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(
            1,
            synthetic[0],
            sourceFileIndex: 3,
            rotation: 0,
            sourceFilePath: queuedPaths[3],
            sourceFileName: "对照-合成缩略图"));
    }

    private static void AddRenderedMergeCard(
        WorkspaceViewModel viewModel,
        GhostscriptPdfThumbnailRenderer renderer,
        string pdfPath,
        string renderDirectory,
        string sourceFilePath,
        string sourceFileName,
        int sourceFileIndex,
        int rotation)
    {
        var fileDirectory = Path.Combine(renderDirectory, Guid.NewGuid().ToString("N"));
        var rendered = renderer.RenderAsync(pdfPath, 1, fileDirectory).GetAwaiter().GetResult();
        if (rendered.Count != 1)
        {
            throw new InvalidOperationException("The PDF merge thumbnail render produced unexpected files.");
        }

        viewModel.PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(
            1,
            rendered[0],
            sourceFileIndex,
            rotation,
            sourceFilePath,
            sourceFileName));
    }

    private static void CreateMinimalPdf(
        string path,
        string marker,
        double mediaBoxWidth,
        double mediaBoxHeight)
    {
        var escapedMarker = marker
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var content = System.Text.Encoding.ASCII.GetBytes(
            $"BT /F1 18 Tf 24 100 Td ({escapedMarker}) Tj ET\n");
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(
                "<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {mediaBoxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"{mediaBoxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Combine(
                Ascii($"<< /Length {content.Length} >>\nstream\n"),
                content,
                Ascii("endstream")),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        using var stream = new MemoryStream();
        Write(stream, Ascii("%PDF-1.4\n"));
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            Write(stream, Ascii($"{index + 1} 0 obj\n"));
            Write(stream, objects[index]);
            Write(stream, Ascii("\nendobj\n"));
        }

        var xrefOffset = stream.Position;
        Write(stream, Ascii($"xref\n0 {objects.Count + 1}\n"));
        Write(stream, Ascii("0000000000 65535 f \n"));
        foreach (var offset in offsets.Skip(1))
        {
            Write(stream, Ascii(
                $"{offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)} 00000 n \n"));
        }

        Write(stream, Ascii(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n%%EOF\n"));
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(static part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value);

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes);

    private static void ConfigureBatchRenameScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.BatchRename);
        viewModel.RenameSearchText = "季度";
        viewModel.RenameReplacementText = "2026-Q3";
        viewModel.StatusMessage = "重命名预览已更新";
    }

    private static void ConfigureBatchRenameInsertScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedRenameToolMode = AssertSingle(
            viewModel.RenameToolModes,
            static option => option.Mode == RenameToolMode.Insert);
        viewModel.RenameInsertText = "_归档";
        viewModel.SelectedRenameInsertPosition = AssertSingle(
            viewModel.RenamePositions,
            static option => option.Position == RenameInsertPosition.SpecifiedIndex);
        viewModel.RenameInsertIndex = 2;
    }

    private static void ConfigureBatchRenameNumberingScenario(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        ConfigureBatchRenameFiles(viewModel, fixtureRoot);
        viewModel.SelectedRenameToolMode = AssertSingle(
            viewModel.RenameToolModes,
            static option => option.Mode == RenameToolMode.Numbering);
        viewModel.RenameNumberingEnabled = true;
        viewModel.RenameNumberingStart = 1;
        viewModel.RenameNumberingStep = 1;
        viewModel.RenameNumberingPadding = 3;
        viewModel.RenameNumberingPrefix = "DOC-";
        viewModel.SelectedRenameNumberingPosition = AssertSingle(
            viewModel.RenamePositions,
            static option => option.Position == RenameInsertPosition.Beginning);
    }

    private static void ConfigureBatchRenameFiles(WorkspaceViewModel viewModel, string fixtureRoot)
    {
        viewModel.SelectedTool = AssertSingle(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.BatchRename);
        viewModel.AddPaths(
        [
            CreateFixture(fixtureRoot, "季度经营分析报告（华东区域）.docx"),
            CreateFixture(fixtureRoot, "季度预算汇总.xlsx"),
            CreateFixture(fixtureRoot, "季度审阅材料.pdf"),
        ]);
    }

    private static T AssertSingle<T>(
        IEnumerable<T> values,
        Func<T, bool> predicate)
    {
        var matches = values.Where(predicate).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException("UI snapshot scenario selection was ambiguous.");
    }

    private static string CreateFixture(string fixtureRoot, string fileName)
    {
        var path = Path.Combine(fixtureRoot, fileName);
        File.WriteAllText(path, "DocPivot UI snapshot fixture");
        return path;
    }

    private static List<string> CreatePdfThumbnailFixtures(
        string fixtureRoot,
        int count,
        string namePrefix = "pdf-page")
    {
        var paths = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            const int width = 180;
            const int height = 240;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = y * stride + x * 4;
                    var isBorder = x < 2 || x >= width - 2 || y < 2 || y >= height - 2;
                    var isHeader = y is >= 22 and < 34 && x is >= 18 and < 128;
                    var isAccent = y is >= 54 and < 112 && x is >= 18 and < 162;
                    var channel = isBorder ? (byte)174 : (byte)238;
                    pixels[offset] = isAccent ? (byte)(84 + index * 4) : channel;
                    pixels[offset + 1] = isAccent ? (byte)(116 + index * 3) : channel;
                    pixels[offset + 2] = isAccent ? (byte)216 : isHeader ? (byte)156 : channel;
                    pixels[offset + 3] = byte.MaxValue;
                }
            }

            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(fixtureRoot, $"{namePrefix}-{index + 1:D2}.png");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            paths.Add(path);
        }

        return paths;
    }

    private static void PrepareOutputDirectory(string repositoryRoot, string outputDirectory)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedOutput = Path.GetFullPath(outputDirectory);
        if (!normalizedOutput.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean a UI snapshot directory outside the repository.");
        }

        if (Directory.Exists(normalizedOutput))
        {
            Directory.Delete(normalizedOutput, recursive: true);
        }

        Directory.CreateDirectory(normalizedOutput);
    }

    private static void DeleteFixtureDirectory(string fixtureRoot)
    {
        var normalizedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DocPivot.UiSnapshot"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedFixture = Path.GetFullPath(fixtureRoot);
        if (normalizedFixture.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(normalizedFixture))
        {
            Directory.Delete(normalizedFixture, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DocPivot repository root.");
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficeEngineStatus(true, "Microsoft Office", "2024", "x64", null));

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("季度销售明细（华东区域）", 2, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 3, "hidden", false),
            ],
            new ExcelWorkbookCompatibilityReport(
                WorksheetCount: 3,
                HiddenWorksheetCount: 1,
                ChartSheetCount: 0,
                ExternalLinkCount: 0,
                HasVbaProject: false,
                Uses1904DateSystem: false,
                CompressionRisks: ["嵌入式图表", "工作簿级名称"])));

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OfficeConversionResult.Succeeded([outputPath]));
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
    }

    private sealed class FakePdfOperationsClient(int pageCount = 1) : IPdfOperationsClient
    {
        public Task<PdfPreflightResult> PreflightAsync(
            PdfPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PdfPreflightResult.Succeeded(
                request.InputPath,
                pageCount,
                hasSignatureFields: false));

        public Task<OperationExecutionResult> MergeAsync(
            PdfMergeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));

        public Task<OperationExecutionResult> SplitAsync(
            PdfSplitRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([]));

        public Task<OperationExecutionResult> OptimizeAsync(
            PdfOptimizeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
    }

    private sealed class FakeExcelOperationsClient : IExcelOperationsClient
    {
        public Task<OperationExecutionResult> ExecuteAsync(
            ExcelOperationRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.Output]));
    }

    private sealed class FakePdfTableOperationsClient : IPdfTableOperationsClient
    {
        public Task<PdfTableEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfTableEngineStatus(true, true, true, null, null));

        public Task<OperationExecutionResult> ConvertAsync(
            PdfToExcelRequest request,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath]));
    }

    private sealed class FakeShellService : IShellService
    {
        public void OpenFile(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }
}
