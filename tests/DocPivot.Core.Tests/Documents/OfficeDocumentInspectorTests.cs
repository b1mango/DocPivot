using System.IO.Compression;
using System.Text;
using DocPivot.Core.Documents;

namespace DocPivot.Core.Tests.Documents;

public sealed class OfficeDocumentInspectorTests
{
    [Theory]
    [InlineData("sample.docx", "word/document.xml", OfficeDocumentKind.WordOpenXml)]
    [InlineData("sample.xlsx", "xl/workbook.xml", OfficeDocumentKind.ExcelOpenXml)]
    public void Inspect_AcceptsExpectedOpenXmlMainPart(
        string fileName,
        string mainPart,
        OfficeDocumentKind expectedKind)
    {
        var path = CreateOpenXmlPackage(fileName, mainPart);
        try
        {
            var result = OfficeDocumentInspector.Inspect(path);

            Assert.True(result.IsValid);
            Assert.Equal(expectedKind, result.Kind);
            Assert.Null(result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_RejectsOpenXmlPackageWhoseMainPartDoesNotMatchExtension()
    {
        var path = CreateOpenXmlPackage("sample.docx", "xl/workbook.xml");
        try
        {
            var result = OfficeDocumentInspector.Inspect(path);

            Assert.False(result.IsValid);
            Assert.Equal("OFFICE_OPEN_XML_TYPE_MISMATCH", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sample.doc", "WordDocument", OfficeDocumentKind.WordBinary)]
    [InlineData("sample.xls", "Workbook", OfficeDocumentKind.ExcelBinary)]
    public void Inspect_AcceptsExpectedCompoundFileDirectoryEntry(
        string fileName,
        string streamName,
        OfficeDocumentKind expectedKind)
    {
        var path = CreateCompoundFile(fileName, streamName);
        try
        {
            var result = OfficeDocumentInspector.Inspect(path);

            Assert.True(result.IsValid);
            Assert.Equal(expectedKind, result.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_RejectsPlainTextWithOfficeExtension()
    {
        var path = GetTemporaryPath("sample.docx");
        try
        {
            File.WriteAllText(path, "not an Office document");

            var result = OfficeDocumentInspector.Inspect(path);

            Assert.False(result.IsValid);
            Assert.Equal("SIGNATURE_MISMATCH", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateOpenXmlPackage(string fileName, string mainPart)
    {
        var path = GetTemporaryPath(fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntry("[Content_Types].xml");
        archive.CreateEntry(mainPart);
        return path;
    }

    private static string CreateCompoundFile(string fileName, string streamName)
    {
        var path = GetTemporaryPath(fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        stream.Write(new byte[512]);
        stream.Write(Encoding.Unicode.GetBytes(streamName + "\0"));
        return path;
    }

    private static string GetTemporaryPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }
}
