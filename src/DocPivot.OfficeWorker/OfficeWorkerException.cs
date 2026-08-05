namespace DocPivot.OfficeWorker;

internal sealed class OfficeWorkerException : Exception
{
    public OfficeWorkerException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
