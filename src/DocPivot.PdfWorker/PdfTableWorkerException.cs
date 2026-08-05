namespace DocPivot.PdfWorker;

public sealed class PdfTableWorkerException : Exception
{
    public PdfTableWorkerException(
        string errorCode,
        string message,
        bool isRetryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
    }

    public string ErrorCode { get; }

    public bool IsRetryable { get; }
}
