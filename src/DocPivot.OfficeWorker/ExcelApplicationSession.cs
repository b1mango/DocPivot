using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.OfficeWorker;

internal sealed class ExcelApplicationSession : IDisposable
{
    private readonly List<object> _openWorkbooks = [];
    private dynamic? _application;
    private dynamic? _workbooks;
    private int _ownedProcessId;
    private DateTimeOffset _ownedProcessStartedAt;
    private string? _blankWorkbookTemplatePath;
    private int _blankWorkbookCopyIndex;
    private JobWorkspace? _inputWorkspace;
    private int _inputCopyIndex;
    private bool _isDisposed;

    private ExcelApplicationSession()
    {
    }

    public object Application => _application
        ?? throw new InvalidOperationException("Microsoft Excel is not running.");

    public static ExcelApplicationSession Start(
        Guid jobId,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        var session = new ExcelApplicationSession();
        try
        {
            session._inputWorkspace = JobWorkspace.Create(
                Path.Combine(Path.GetTempPath(), "DocPivot", "office-worker-inputs"),
                jobId);
            session.StartCore(reportProcessId, reportStage);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public object OpenWorkbook(string inputPath, bool readOnly)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var workbookPath = readOnly ? CreateIsolatedInputCopy(inputPath) : inputPath;
        dynamic workbook = _workbooks!.Open(
            Filename: workbookPath,
            UpdateLinks: 0,
            IgnoreReadOnlyRecommended: true,
            AddToMru: false,
            Local: true);
        object trackedWorkbook = workbook;
        _openWorkbooks.Add(trackedWorkbook);
        return trackedWorkbook;
    }

    public object TrackActiveWorkbook()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        dynamic workbook = _application!.ActiveWorkbook
            ?? throw new OfficeWorkerException(
                "EXCEL_WORKBOOK_CREATE_FAILED",
                "Excel did not create a workbook for the copied worksheet.",
                true);
        object trackedWorkbook = workbook;
        _openWorkbooks.Add(trackedWorkbook);
        return trackedWorkbook;
    }

    public object CreateWorkbook()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var workspace = _inputWorkspace
            ?? throw new InvalidOperationException("The Excel input workspace is unavailable.");
        var templatePath = _blankWorkbookTemplatePath;
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            throw new OfficeWorkerException(
                "EXCEL_WORKBOOK_CREATE_FAILED",
                "Excel did not prepare the internal workbook template.",
                true);
        }

        var workbookPath = workspace.GetIntermediatePath(
            $"blank-workbook-{++_blankWorkbookCopyIndex:D2}.xlsx");
        File.Copy(templatePath, workbookPath);
        dynamic workbook = _workbooks!.Open(
            Filename: workbookPath,
            UpdateLinks: 0,
            ReadOnly: false,
            IgnoreReadOnlyRecommended: true,
            AddToMru: false,
            Local: true);
        object trackedWorkbook = workbook;
        _openWorkbooks.Add(trackedWorkbook);
        return trackedWorkbook;
    }

    public void CloseWorkbook(object? workbook, bool saveChanges = false)
    {
        if (workbook is null)
        {
            return;
        }

        try
        {
            ((dynamic)workbook).Close(SaveChanges: saveChanges);
        }
        catch (System.Runtime.InteropServices.InvalidComObjectException)
        {
            // The application-level cleanup remains responsible for terminating Excel.
        }
        finally
        {
            RemoveTrackedWorkbook(workbook);
            ComObject.FinalRelease(workbook);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        for (var index = _openWorkbooks.Count - 1; index >= 0; index--)
        {
            var workbook = _openWorkbooks[index];
            try
            {
                ((dynamic)workbook).Close(SaveChanges: false);
            }
            catch
            {
                // The worker process is the final isolation boundary for COM cleanup.
            }

            ComObject.FinalRelease(workbook);
        }

        _openWorkbooks.Clear();
        try
        {
            _application?.Quit();
        }
        catch
        {
            // The parent process owns timeout and process-tree cleanup.
        }

        ComObject.FinalRelease(_workbooks);
        ComObject.FinalRelease(_application);
        _workbooks = null;
        _application = null;
        OfficeProcessIdentity.TryTerminate(
            _ownedProcessId,
            "EXCEL",
            _ownedProcessStartedAt);
        _ownedProcessId = 0;
        try
        {
            _inputWorkspace?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The worker process is the final cleanup boundary for a locked temporary copy.
        }

        _inputWorkspace = null;
    }

    private void StartCore(
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        _ownedProcessStartedAt = DateTimeOffset.UtcNow;
        using var isolatedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "EXCEL.EXE",
            Arguments = "/x /safe /automation",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new OfficeWorkerException(
            "EXCEL_START_FAILED",
            "Microsoft Excel could not be started.",
            true);
        _ownedProcessId = isolatedProcess.Id;
        reportProcessId(_ownedProcessId);
        reportStage("excel-isolated-process-started");
        ExcelWindowVisibility.HideAllWindows(_ownedProcessId);

        try
        {
            _ = isolatedProcess.WaitForInputIdle(milliseconds: 10_000);
        }
        catch (InvalidOperationException)
        {
            throw new OfficeWorkerException(
                "EXCEL_START_FAILED",
                "Microsoft Excel exited before automation became ready.",
                true);
        }

        ExcelWindowVisibility.HideAllWindows(_ownedProcessId);
        _application = ExcelProcessApplicationBinder.Bind(
            _ownedProcessId,
            isolatedProcess,
            TimeSpan.FromSeconds(15));
        reportStage("excel-created");
        ExcelWindowVisibility.HideAllWindows(_ownedProcessId);
        var automationProcessId = OfficeProcessIdentity.GetProcessId((nint)(int)_application.Hwnd);
        if (automationProcessId != _ownedProcessId)
        {
            ComObject.FinalRelease(_application);
            _application = null;
            throw new OfficeWorkerException(
                "EXCEL_ISOLATION_FAILED",
                "Microsoft Excel automation did not bind to the isolated process.",
                true);
        }

        _application.Visible = false;
        _application.DisplayAlerts = false;
        _application.AutomationSecurity = 3;
        _application.AskToUpdateLinks = false;
        _application.EnableEvents = false;
        _application.ScreenUpdating = false;
        reportStage("excel-configured");
        reportStage("excel-identity");

        _workbooks = _application.Workbooks;
        PrepareBlankWorkbookTemplate(reportStage);
    }

    private void PrepareBlankWorkbookTemplate(Action<string> reportStage)
    {
        dynamic? startupWorkbook = null;
        try
        {
            if ((int)_workbooks!.Count < 1)
            {
                throw new OfficeWorkerException(
                    "EXCEL_WORKBOOK_CREATE_FAILED",
                    "Excel did not create its startup workbook.",
                    true);
            }

            startupWorkbook = _workbooks[1];
            var workspace = _inputWorkspace
                ?? throw new InvalidOperationException("The Excel input workspace is unavailable.");
            _blankWorkbookTemplatePath = workspace.GetIntermediatePath("blank-workbook-template.xlsx");
            startupWorkbook.Close(SaveChanges: false);
            ExcelBlankWorkbookTemplate.Create(_blankWorkbookTemplatePath);
            if (!File.Exists(_blankWorkbookTemplatePath))
            {
                throw new OfficeWorkerException(
                    "EXCEL_WORKBOOK_CREATE_FAILED",
                    "Excel did not save the internal workbook template.",
                    true);
            }

            reportStage("excel-template-prepared");
        }
        finally
        {
            ComObject.FinalRelease(startupWorkbook);
        }
    }

    private void RemoveTrackedWorkbook(object workbook)
    {
        for (var index = _openWorkbooks.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_openWorkbooks[index], workbook))
            {
                continue;
            }

            _openWorkbooks.RemoveAt(index);
            return;
        }
    }

    private string CreateIsolatedInputCopy(string inputPath)
    {
        var workspace = _inputWorkspace
            ?? throw new InvalidOperationException("The Excel input workspace is unavailable.");
        var extension = Path.GetExtension(inputPath);
        var fileName = $"input-{++_inputCopyIndex:D2}{extension}";
        var copyPath = workspace.GetIntermediatePath(fileName);
        File.Copy(inputPath, copyPath);
        return copyPath;
    }
}

internal static class ExcelProcessApplicationBinder
{
    private const uint NativeObjectId = 0xfffffff0;
    private static readonly Guid DispatchInterfaceId = new("00020400-0000-0000-C000-000000000046");

    public static object Bind(int processId, Process process, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new OfficeWorkerException(
                    "EXCEL_START_FAILED",
                    "Microsoft Excel exited before automation became ready.",
                    true);
            }

            var nativeWindow = FindNativeObjectWindow(processId);
            if (nativeWindow != 0 && TryGetApplication(nativeWindow, processId, out var application))
            {
                return application!;
            }

            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new OfficeWorkerException(
            "EXCEL_ISOLATION_FAILED",
            "Microsoft Excel automation did not expose the isolated process.",
            true);
    }

    private static nint FindNativeObjectWindow(int processId)
    {
        nint result = 0;
        _ = EnumWindows((window, parameter) =>
        {
            if (OfficeProcessIdentity.GetProcessId(window) != processId ||
                !HasWindowClass(window, "XLMAIN"))
            {
                return true;
            }

            EnumChildWindows(window, (child, childParameter) =>
            {
                if (!HasWindowClass(child, "EXCEL7"))
                {
                    return true;
                }

                result = child;
                return false;
            }, 0);
            return result == 0;
        }, 0);
        return result;
    }

    private static bool TryGetApplication(
        nint nativeWindow,
        int expectedProcessId,
        out object? application)
    {
        application = null;
        object? nativeObject = null;
        try
        {
            var dispatchInterfaceId = DispatchInterfaceId;
            var result = AccessibleObjectFromWindow(
                nativeWindow,
                NativeObjectId,
                ref dispatchInterfaceId,
                out nativeObject);
            if (result < 0 || nativeObject is null)
            {
                return false;
            }

            application = ((dynamic)nativeObject).Application;
            var actualProcessId = OfficeProcessIdentity.GetProcessId(
                (nint)(int)((dynamic)application).Hwnd);
            if (actualProcessId == expectedProcessId)
            {
                return true;
            }

            ComObject.FinalRelease(application);
            application = null;
            return false;
        }
        catch (COMException)
        {
            ComObject.FinalRelease(application);
            application = null;
            return false;
        }
        finally
        {
            ComObject.FinalRelease(nativeObject);
        }
    }

    private static unsafe bool HasWindowClass(nint window, string expectedClassName)
    {
        Span<char> className = stackalloc char[64];
        fixed (char* buffer = className)
        {
            var length = GetClassName(window, buffer, className.Length);
            return length > 0 && expectedClassName.AsSpan().SequenceEqual(className[..length]);
        }
    }

    private delegate bool EnumWindowProcedure(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProcedure callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowProcedure callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern unsafe int GetClassName(
        nint window,
        char* className,
        int maximumCount);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint window,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object? nativeObject);
}

internal static class ExcelBlankWorkbookTemplate
{
    private static readonly XNamespace ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace PackageRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Create(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteXmlEntry(
            archive,
            "[Content_Types].xml",
            new XDocument(
                new XElement(
                    ContentTypesNamespace + "Types",
                    new XElement(
                        ContentTypesNamespace + "Default",
                        new XAttribute("Extension", "rels"),
                        new XAttribute(
                            "ContentType",
                            "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(
                        ContentTypesNamespace + "Default",
                        new XAttribute("Extension", "xml"),
                        new XAttribute("ContentType", "application/xml")),
                    new XElement(
                        ContentTypesNamespace + "Override",
                        new XAttribute("PartName", "/xl/workbook.xml"),
                        new XAttribute(
                            "ContentType",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                    new XElement(
                        ContentTypesNamespace + "Override",
                        new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                        new XAttribute(
                            "ContentType",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
        WriteXmlEntry(
            archive,
            "_rels/.rels",
            new XDocument(
                new XElement(
                    PackageRelationshipsNamespace + "Relationships",
                    CreateRelationship(
                        "rId1",
                        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
                        "xl/workbook.xml"))));
        WriteXmlEntry(
            archive,
            "xl/workbook.xml",
            new XDocument(
                new XElement(
                    SpreadsheetNamespace + "workbook",
                    new XAttribute(XNamespace.Xmlns + "r", OfficeRelationshipsNamespace),
                    new XElement(
                        SpreadsheetNamespace + "sheets",
                        new XElement(
                            SpreadsheetNamespace + "sheet",
                            new XAttribute("name", "Sheet1"),
                            new XAttribute("sheetId", "1"),
                            new XAttribute(OfficeRelationshipsNamespace + "id", "rId1"))))));
        WriteXmlEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            new XDocument(
                new XElement(
                    PackageRelationshipsNamespace + "Relationships",
                    CreateRelationship(
                        "rId1",
                        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                        "worksheets/sheet1.xml"))));
        WriteXmlEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            new XDocument(
                new XElement(
                    SpreadsheetNamespace + "worksheet",
                    new XElement(SpreadsheetNamespace + "sheetData"))));
    }

    private static XElement CreateRelationship(string id, string type, string target)
    {
        return new XElement(
            PackageRelationshipsNamespace + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type", type),
            new XAttribute("Target", target));
    }

    private static void WriteXmlEntry(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }
}
