namespace DocPivot.Core.Documents;

public static class DocumentAdmissionPolicy
{
    private static readonly Dictionary<DocumentOperation, string[]> SupportedExtensions =
        new Dictionary<DocumentOperation, string[]>
        {
            [DocumentOperation.OfficeToPdf] = [".doc", ".docx", ".xls", ".xlsx"],
            [DocumentOperation.PdfToExcel] = [".pdf"],
            [DocumentOperation.ExcelOperations] = [".xls", ".xlsx"],
            [DocumentOperation.PdfOperations] = [".pdf"],
            [DocumentOperation.BatchRename] = [".doc", ".docx", ".xls", ".xlsx", ".pdf"],
        };

    public static DocumentAdmissionResult Evaluate(
        DocumentOperation operation,
        string extension,
        long fileSizeBytes,
        int currentBatchCount)
    {
        var maximum = operation == DocumentOperation.BatchRename
            ? DocumentLimits.MaximumRenameBatchFiles
            : DocumentLimits.MaximumBatchFiles;
        if (currentBatchCount >= maximum)
        {
            return DocumentAdmissionResult.Rejected(DocumentAdmissionFailure.BatchLimitReached);
        }

        if (fileSizeBytes < 0 || fileSizeBytes > DocumentLimits.MaximumFileSizeBytes)
        {
            return DocumentAdmissionResult.Rejected(DocumentAdmissionFailure.FileTooLarge);
        }

        if (!IsSupported(operation, extension))
        {
            return DocumentAdmissionResult.Rejected(DocumentAdmissionFailure.UnsupportedExtension);
        }

        return DocumentAdmissionResult.Accepted;
    }

    public static bool IsSupported(DocumentOperation operation, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        return SupportedExtensions[operation].Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetSupportedExtensions(DocumentOperation operation) =>
        SupportedExtensions[operation];
}
