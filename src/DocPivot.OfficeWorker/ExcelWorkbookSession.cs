namespace DocPivot.OfficeWorker;

internal sealed class ExcelWorkbookSession : IDisposable
{
    private ExcelApplicationSession? _applicationSession;
    private object? _workbook;
    private bool _isDisposed;

    private ExcelWorkbookSession()
    {
    }

    public object Workbook => _workbook
        ?? throw new InvalidOperationException("The Excel workbook is not open.");

    public static ExcelWorkbookSession Open(
        string inputPath,
        Guid jobId,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        var session = new ExcelWorkbookSession();
        try
        {
            session.OpenCore(inputPath, jobId, reportProcessId, reportStage);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OpenCore(
        string inputPath,
        Guid jobId,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        _applicationSession = ExcelApplicationSession.Start(jobId, reportProcessId, reportStage);
        _workbook = _applicationSession.OpenWorkbook(inputPath, readOnly: true);
        reportStage("workbook-opened");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _applicationSession?.CloseWorkbook(_workbook);
        _applicationSession?.Dispose();
        _workbook = null;
        _applicationSession = null;
    }
}
