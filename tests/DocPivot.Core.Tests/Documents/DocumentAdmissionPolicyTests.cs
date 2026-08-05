using DocPivot.Core.Documents;

namespace DocPivot.Core.Tests.Documents;

public sealed class DocumentAdmissionPolicyTests
{
    [Theory]
    [InlineData(DocumentOperation.OfficeToPdf, ".doc")]
    [InlineData(DocumentOperation.OfficeToPdf, "XLSX")]
    [InlineData(DocumentOperation.PdfToExcel, ".PDF")]
    [InlineData(DocumentOperation.ExcelOperations, ".xls")]
    [InlineData(DocumentOperation.PdfOperations, "pdf")]
    [InlineData(DocumentOperation.BatchRename, ".docx")]
    public void Evaluate_AcceptsSupportedFiles(DocumentOperation operation, string extension)
    {
        var result = DocumentAdmissionPolicy.Evaluate(operation, extension, 1024, 0);

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentAdmissionFailure.None, result.Failure);
    }

    [Fact]
    public void Evaluate_RejectsFilesLargerThanLimit()
    {
        var result = DocumentAdmissionPolicy.Evaluate(
            DocumentOperation.PdfOperations,
            ".pdf",
            DocumentLimits.MaximumFileSizeBytes + 1,
            0);

        Assert.False(result.IsAccepted);
        Assert.Equal(DocumentAdmissionFailure.FileTooLarge, result.Failure);
    }

    [Fact]
    public void Evaluate_RejectsWhenBatchIsFull()
    {
        var result = DocumentAdmissionPolicy.Evaluate(
            DocumentOperation.BatchRename,
            ".pdf",
            1024,
            DocumentLimits.MaximumRenameBatchFiles);

        Assert.False(result.IsAccepted);
        Assert.Equal(DocumentAdmissionFailure.BatchLimitReached, result.Failure);
    }

    [Fact]
    public void Evaluate_RenameAllowsMoreThanGeneralBatchLimit()
    {
        var result = DocumentAdmissionPolicy.Evaluate(
            DocumentOperation.BatchRename,
            ".pdf",
            1024,
            DocumentLimits.MaximumBatchFiles + 1);

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Evaluate_RejectsUnsupportedExtension()
    {
        var result = DocumentAdmissionPolicy.Evaluate(DocumentOperation.PdfToExcel, ".docx", 1024, 0);

        Assert.False(result.IsAccepted);
        Assert.Equal(DocumentAdmissionFailure.UnsupportedExtension, result.Failure);
    }
}

