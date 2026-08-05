using System.Text;
using DocPivot.Core.Documents;

namespace DocPivot.Core.Tests.Documents;

public sealed class PdfFileInspectorTests
{
    [Fact]
    public void Inspect_AcceptsPdfHeaderAndEndMarker()
    {
        var path = CreateTemporaryFile("%PDF-1.7\n1 0 obj\nendobj\n%%EOF\n");
        try
        {
            Assert.True(PdfFileInspector.Inspect(path).IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not a pdf file", "PDF_HEADER_INVALID")]
    [InlineData("%PDF-1.7\nmissing eof marker", "PDF_EOF_MARKER_MISSING")]
    public void Inspect_RejectsInvalidPdf(string content, string expectedError)
    {
        var path = CreateTemporaryFile(content);
        try
        {
            var result = PdfFileInspector.Inspect(path);

            Assert.False(result.IsValid);
            Assert.Equal(expectedError, result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryFile(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "result.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }
}
