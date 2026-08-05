using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Renaming;

namespace DocPivot.App.Tests.Views;

public sealed class MainWindowRenderingTests
{
    [Fact]
    public void MainWindow_QueueRenderingAndProcessingClose_WorkOnStaThread()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var inputPath = Path.Combine(testRoot, "render.xlsx");
        File.WriteAllText(inputPath, "fixture");
        var wordPath = Path.Combine(testRoot, "notes.docx");
        File.WriteAllText(wordPath, "fixture");
        var renamePath = Path.Combine(testRoot, "quarterly-report.pdf");
        File.WriteAllText(renamePath, "fixture");
        Exception? threadException = null;
        var stage = "thread-not-started";
        var bindingErrors = new ConcurrentQueue<string>();
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var bindingTraceSource = PresentationTraceSources.DataBindingSource;
            var originalBindingTraceLevel = bindingTraceSource.Switch.Level;
            using var bindingTraceListener = new RecordingTraceListener(bindingErrors);
            bindingTraceSource.Switch.Level = SourceLevels.Error;
            bindingTraceSource.Listeners.Add(bindingTraceListener);
            try
            {
                stage = "creating-application";
                var application = new App();
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(async () =>
                    {
                        try
                        {
                            var window = new MainWindow(
                                 new FakeFilePickerService(),
                                 new FakeOfficeWorkerClient(),
                                 new FakeShellService(),
                                 batchRenameExecutor: new BatchRenameExecutor())
                            {
                                ShowActivated = false,
                                ShowInTaskbar = false,
                                WindowStartupLocation = WindowStartupLocation.Manual,
                                Left = -10_000,
                                Top = -10_000,
                            };
                            var viewModel = Assert.IsType<WorkspaceViewModel>(window.DataContext);

                            stage = "showing-render-window";
                            window.Show();
                            window.UpdateLayout();
                            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                            {
                                Assert.Same(window.FindResource("WindowFallbackBrush"), window.Background);
                            }

                            var dropZone = Assert.Single(
                                FindVisualChildren<Border>(window),
                                static border => border.Name == "DropZoneShell");

                            stage = "raising-valid-file-drop";
                            var validDrop = CreateDragEventArgs(
                                new DataObject(DataFormats.FileDrop, new[] { inputPath }),
                                dropZone,
                                DragDrop.DropEvent);
                            dropZone.RaiseEvent(validDrop);
                            Assert.True(validDrop.Handled);
                            window.UpdateLayout();

                            var queuedFile = Assert.Single(viewModel.Files);
                            var worksheetSelector = Assert.Single(
                                FindVisualChildren<ComboBox>(window),
                                static comboBox =>
                                    AutomationProperties.GetName(comboBox) == "Excel 导出范围");
                            Assert.True(worksheetSelector.IsEnabled);
                            Assert.Equal(4, worksheetSelector.Items.Count);
                            Assert.Equal("全部工作表", queuedFile.SelectedWorksheetOption.DisplayName);
                            worksheetSelector.SelectedIndex = 2;
                            Assert.Equal("明细 数据", queuedFile.SelectedWorksheetName);
                            queuedFile.MarkValidating();
                            queuedFile.MarkRunning();
                            queuedFile.ReportProgress(
                                WorkerProgressMessage.Create(Guid.NewGuid(), "office-export", 2, 4));
                            window.UpdateLayout();
                            var progressBar = Assert.Single(FindVisualChildren<ProgressBar>(window));
                            Assert.Equal(Visibility.Visible, progressBar.Visibility);
                            Assert.Equal(50d, progressBar.Value, precision: 3);
                            Assert.False(worksheetSelector.IsEnabled);

                            viewModel.AddPaths([wordPath]);
                            window.UpdateLayout();
                            Assert.Equal(2, viewModel.Files.Count);
                            Assert.Single(
                                FindVisualChildren<ComboBox>(window),
                                static comboBox => comboBox.IsVisible);

                            stage = "raising-invalid-drag-events";
                            var dragEnterBubbled = false;
                            var dropBubbled = false;
                            window.AddHandler(
                                DragDrop.DragEnterEvent,
                                new DragEventHandler((_, _) => dragEnterBubbled = true));
                            window.AddHandler(
                                DragDrop.DropEvent,
                                new DragEventHandler((_, _) => dropBubbled = true));
                            var dragEnter = CreateDragEventArgs(
                                new ThrowingDataObject(throwFromGetDataPresent: true),
                                dropZone,
                                DragDrop.DragEnterEvent);
                            dropZone.RaiseEvent(dragEnter);
                            Assert.True(dragEnter.Handled);
                            Assert.Equal(DragDropEffects.None, dragEnter.Effects);
                            Assert.False(dragEnterBubbled);

                            var drop = CreateDragEventArgs(
                                new ThrowingDataObject(throwFromGetDataPresent: false),
                                dropZone,
                                DragDrop.DropEvent);
                            dropZone.RaiseEvent(drop);
                            Assert.True(drop.Handled);
                            Assert.False(dropBubbled);

                            viewModel.IsProcessing = true;
                            window.UpdateLayout();
                            var outputDirectoryTextBox = Assert.Single(
                                FindVisualChildren<TextBox>(window),
                                static textBox => AutomationProperties.GetName(textBox) == "输出目录");
                             Assert.True(outputDirectoryTextBox.IsReadOnly);
                             viewModel.IsProcessing = false;

                             stage = "rendering-batch-rename";
                             viewModel.SelectedTool = Assert.Single(
                                 viewModel.Tools,
                                 static tool => tool.Operation == DocumentOperation.BatchRename);
                             viewModel.RenameSearchText = "quarterly";
                             viewModel.RenameReplacementText = "2026";
                             viewModel.AddPaths([renamePath]);
                             window.Width = 1060;
                             window.Height = 680;
                             window.UpdateLayout();

                             var renameModeSelector = Assert.Single(
                                 FindVisualChildren<ListBox>(window),
                                 static listBox =>
                                     AutomationProperties.GetName(listBox) == "批量重命名方式");
                             Assert.True(renameModeSelector.IsVisible);
                             Assert.Equal(3, renameModeSelector.Items.Count);
                             var renameTextBox = Assert.Single(
                                 FindVisualChildren<TextBox>(window),
                                 static textBox =>
                                     AutomationProperties.GetName(textBox) == "重命名后的文件名");
                             Assert.True(renameTextBox.IsVisible);
                             Assert.False(renameTextBox.IsReadOnly);
                             Assert.Equal("2026-report.pdf", renameTextBox.Text);
                             Assert.True(renameTextBox.ActualWidth >= 100);
                             Assert.False(outputDirectoryTextBox.IsVisible);

                             viewModel.SelectedRenameToolMode = Assert.Single(
                                 viewModel.RenameToolModes,
                                 static option => option.Mode == RenameToolMode.Numbering);
                             window.UpdateLayout();
                             var numberingCheckBox = Assert.Single(
                                 FindVisualChildren<System.Windows.Controls.CheckBox>(window),
                                 static checkBox =>
                                     checkBox.Content is string content &&
                                     content.Contains("启用自动编号", StringComparison.Ordinal));
                             Assert.True(numberingCheckBox.IsVisible);
                             window.Close();

                            stage = "creating-cancel-window";
                            using var cancellationObserved = new ManualResetEventSlim();
                            var cancelingWindow = new MainWindow(
                                new FakeFilePickerService(),
                                new FakeOfficeWorkerClient
                                {
                                    ConvertHandler = async (_, output, _, _, cancellationToken) =>
                                    {
                                        try
                                        {
                                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                                        }
                                        finally
                                        {
                                            cancellationObserved.Set();
                                        }

                                        return OfficeConversionResult.Succeeded([output]);
                                    },
                                },
                                new FakeShellService())
                            {
                                ShowActivated = false,
                                ShowInTaskbar = false,
                                WindowStartupLocation = WindowStartupLocation.Manual,
                                Left = -10_000,
                                Top = -10_000,
                            };
                            var cancelingViewModel = Assert.IsType<WorkspaceViewModel>(cancelingWindow.DataContext);
                            cancelingViewModel.OutputDirectory = Path.Combine(testRoot, "output");
                            cancelingViewModel.AddPaths([inputPath]);
                            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            cancelingWindow.Closed += (_, _) => closed.TrySetResult();

                            stage = "showing-cancel-window";
                            cancelingWindow.Show();
                            var processing = cancelingViewModel.StartProcessingCommand.ExecuteAsync(null);
                            Assert.True(cancelingViewModel.IsProcessing);

                            stage = "closing-cancel-window";
                            cancelingWindow.Close();
                            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

                            stage = "asserting-cancel-window";
                            Assert.True(cancellationObserved.IsSet);
                            Assert.True(processing.IsCompleted);
                            Assert.False(cancelingViewModel.IsProcessing);

                            stage = "creating-discovery-close-window";
                            var discoveryStarted = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            var discoveryCancellationObserved = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            var discoveryWindow = new MainWindow(
                                new FakeFilePickerService(),
                                new FakeOfficeWorkerClient
                                {
                                    WorksheetListHandler = async (_, cancellationToken) =>
                                    {
                                        discoveryStarted.TrySetResult();
                                        try
                                        {
                                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                                        }
                                        finally
                                        {
                                            discoveryCancellationObserved.TrySetResult();
                                        }

                                        return ExcelWorksheetListResult.Succeeded([]);
                                    },
                                },
                                new FakeShellService())
                            {
                                ShowActivated = false,
                                ShowInTaskbar = false,
                                WindowStartupLocation = WindowStartupLocation.Manual,
                                Left = -10_000,
                                Top = -10_000,
                            };
                            var discoveryViewModel = Assert.IsType<WorkspaceViewModel>(discoveryWindow.DataContext);
                            var discoveryClosed = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            discoveryWindow.Closed += (_, _) => discoveryClosed.TrySetResult();
                            discoveryWindow.Show();
                            discoveryViewModel.AddPaths([inputPath]);
                            await discoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                            stage = "closing-discovery-window";
                            discoveryWindow.Close();
                            await discoveryClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));

                            stage = "asserting-discovery-close-window";
                            await discoveryCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
                            await discoveryViewModel.WaitForWorksheetDiscoveryAsync();

                            stage = "creating-timeout-window";
                            var probeStarted = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            var probeCompletion = new TaskCompletionSource<OfficeEngineStatus>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            var timeoutWindow = new MainWindow(
                                new FakeFilePickerService(),
                                new FakeOfficeWorkerClient
                                {
                                    ProbeHandler = _ =>
                                    {
                                        probeStarted.TrySetResult();
                                        return probeCompletion.Task;
                                    },
                                },
                                new FakeShellService(),
                                closeCleanupTimeout: TimeSpan.FromMilliseconds(50))
                            {
                                ShowActivated = false,
                                ShowInTaskbar = false,
                                WindowStartupLocation = WindowStartupLocation.Manual,
                                Left = -10_000,
                                Top = -10_000,
                            };
                            var timeoutClosed = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            timeoutWindow.Closed += (_, _) => timeoutClosed.TrySetResult();

                            stage = "showing-timeout-window";
                            timeoutWindow.Show();
                            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                            stage = "closing-timeout-window";
                            timeoutWindow.Close();
                            await timeoutClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));

                            stage = "asserting-timeout-window";
                            Assert.False(probeCompletion.Task.IsCompleted);
                            probeCompletion.TrySetResult(
                                new OfficeEngineStatus(true, "Office", "16.0", "x64", null));
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            stage = "completed";
                        }
                        catch (Exception exception)
                        {
                            threadException = exception;
                        }
                        finally
                        {
                            application.Shutdown();
                            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    }));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
            finally
            {
                bindingTraceSource.Listeners.Remove(bindingTraceListener);
                bindingTraceSource.Switch.Level = originalBindingTraceLevel;
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), $"WPF rendering test timed out at stage: {stage}.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF rendering thread did not exit.");
        Assert.Null(threadException);
        Assert.True(
            bindingErrors.IsEmpty,
            $"WPF emitted data-binding errors:{Environment.NewLine}{string.Join(Environment.NewLine, bindingErrors)}");

        Directory.Delete(testRoot, recursive: true);
    }

    private static DragEventArgs CreateDragEventArgs(
        IDataObject data,
        DependencyObject target,
        RoutedEvent routedEvent)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(IDataObject),
                typeof(DragDropKeyStates),
                typeof(DragDropEffects),
                typeof(DependencyObject),
                typeof(Point),
            ],
            modifiers: null);
        Assert.NotNull(constructor);
        var eventArgs = Assert.IsType<DragEventArgs>(constructor.Invoke(
        [
            data,
            DragDropKeyStates.None,
            DragDropEffects.Copy,
            target,
            new Point(),
        ]));
        eventArgs.RoutedEvent = routedEvent;
        return eventArgs;
    }

    private static IEnumerable<TElement> FindVisualChildren<TElement>(DependencyObject parent)
        where TElement : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TElement element)
            {
                yield return element;
            }

            foreach (var descendant in FindVisualChildren<TElement>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Func<CancellationToken, Task<OfficeEngineStatus>> ProbeHandler { get; init; } =
            _ => Task.FromResult(new OfficeEngineStatus(true, "Office", "16.0", "x64", null));

        public Func<string, CancellationToken, Task<ExcelWorksheetListResult>> WorksheetListHandler { get; init; } =
            (_, _) => Task.FromResult(ExcelWorksheetListResult.Succeeded(
            [
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("明细 数据", 2, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 3, "hidden", false),
            ]));

        public Func<
            string,
            string,
            OfficeConversionOptions?,
            IProgress<WorkerProgressMessage>?,
            CancellationToken,
            Task<OfficeConversionResult>> ConvertHandler { get; init; } =
            (_, output, _, _, _) => Task.FromResult(OfficeConversionResult.Succeeded([output]));

        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            ProbeHandler(cancellationToken);

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            WorksheetListHandler(inputPath, cancellationToken);

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            ConvertHandler(inputPath, outputPath, options, progress, cancellationToken);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
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

    private sealed class RecordingTraceListener(ConcurrentQueue<string> messages) : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Enqueue(message);
            }
        }

        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class ThrowingDataObject(bool throwFromGetDataPresent) : IDataObject
    {
        public object GetData(string format, bool autoConvert) => GetData(format);

        public object GetData(string format) =>
            throw new InvalidOperationException("The drag payload became unavailable.");

        public object GetData(Type format) => GetData(format.FullName ?? format.Name);

        public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);

        public bool GetDataPresent(string format) => throwFromGetDataPresent
            ? throw new InvalidOperationException("The drag payload format cannot be inspected.")
            : true;

        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name);

        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop];

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();

        public void SetData(string format, object data) => throw new NotSupportedException();

        public void SetData(Type format, object data) => throw new NotSupportedException();

        public void SetData(object data) => throw new NotSupportedException();
    }
}
