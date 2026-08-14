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
            ReadOnly: readOnly,
            IgnoreReadOnlyRecommended: true,
            AddToMru: false,
            Local: true);
        object trackedWorkbook = workbook;
        _openWorkbooks.Add(trackedWorkbook);
        KeepApplicationHidden();
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
        KeepApplicationHidden();
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
        // COM activation always starts a new Excel process whose main window is
        // never shown, so nothing can flash on the user's screen — unlike
        // launching EXCEL.EXE directly, which shows its frame before automation
        // can hide it.
        var applicationType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new OfficeWorkerException(
                "EXCEL_NOT_INSTALLED",
                "Microsoft Excel is not available.");
        _application = Activator.CreateInstance(applicationType)
            ?? throw new OfficeWorkerException(
                "EXCEL_START_FAILED",
                "Microsoft Excel could not be started.",
                true);
        reportStage("excel-created");
        _application.Visible = false;
        _application.DisplayAlerts = false;
        _application.AutomationSecurity = 3;
        _application.AskToUpdateLinks = false;
        _application.EnableEvents = false;
        _application.ScreenUpdating = false;
        reportStage("excel-configured");

        _ownedProcessId = ResolveOwnedProcessId();
        if (_ownedProcessId <= 0)
        {
            throw new OfficeWorkerException(
                "EXCEL_START_FAILED",
                "Microsoft Excel automation could not identify its process.",
                true);
        }

        reportProcessId(_ownedProcessId);
        ExcelWindowVisibility.HideAllWindows(_ownedProcessId);
        reportStage("excel-identity");

        _workbooks = _application.Workbooks;
        PrepareBlankWorkbookTemplate(reportStage);
    }

    private int ResolveOwnedProcessId()
    {
        try
        {
            return OfficeProcessIdentity.GetProcessId((nint)(int)_application!.Hwnd);
        }
        catch (COMException)
        {
            // Fall back to a temporary workbook window below.
        }

        dynamic? workbook = null;
        dynamic? window = null;
        try
        {
            workbook = _application!.Workbooks.Add();
            window = _application.ActiveWindow;
            return window is null
                ? 0
                : OfficeProcessIdentity.GetProcessId((nint)(int)window.Hwnd);
        }
        catch (COMException)
        {
            return 0;
        }
        finally
        {
            if (workbook is not null)
            {
                try
                {
                    workbook.Close(SaveChanges: false);
                }
                catch (COMException)
                {
                    // The process-level cleanup remains responsible for Excel.
                }
            }

            ComObject.FinalRelease(window);
            ComObject.FinalRelease(workbook);
        }
    }

    private void KeepApplicationHidden()
    {
        try
        {
            if (_application is not null)
            {
                _application.Visible = false;
                _application.DisplayAlerts = false;
                _application.ScreenUpdating = false;
            }
        }
        catch (COMException)
        {
            // Window enumeration below remains the final visual isolation guard.
        }

        ExcelWindowVisibility.HideAllWindows(_ownedProcessId);
    }

    private void PrepareBlankWorkbookTemplate(Action<string> reportStage)
    {
        dynamic? startupWorkbook = null;
        try
        {
            if ((int)_workbooks!.Count >= 1)
            {
                startupWorkbook = _workbooks[1];
                startupWorkbook.Close(SaveChanges: false);
            }
            else
            {
                dynamic createdWorkbook = _workbooks.Add();
                try
                {
                    createdWorkbook.Close(SaveChanges: false);
                }
                finally
                {
                    ComObject.FinalRelease(createdWorkbook);
                }
            }

            var workspace = _inputWorkspace
                ?? throw new InvalidOperationException("The Excel input workspace is unavailable.");
            _blankWorkbookTemplatePath = workspace.GetIntermediatePath("blank-workbook-template.xlsx");
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
