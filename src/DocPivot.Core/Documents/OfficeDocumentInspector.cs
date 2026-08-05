using System.IO.Compression;
using System.Text;

namespace DocPivot.Core.Documents;

public static class OfficeDocumentInspector
{
    private static readonly byte[] CompoundFileHeader =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] WordDocumentStreamName = Encoding.Unicode.GetBytes("WordDocument\0");
    private static readonly byte[] ExcelWorkbookStreamName = Encoding.Unicode.GetBytes("Workbook\0");
    private static readonly byte[] ExcelBookStreamName = Encoding.Unicode.GetBytes("Book\0");

    public static OfficeDocumentInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return OfficeDocumentInspection.Invalid("INPUT_NOT_FOUND");
        }

        if (file.Length > DocumentLimits.MaximumFileSizeBytes)
        {
            return OfficeDocumentInspection.Invalid("INPUT_TOO_LARGE");
        }

        return file.Extension.ToLowerInvariant() switch
        {
            ".doc" => InspectCompoundFile(file.FullName, OfficeDocumentKind.WordBinary),
            ".xls" => InspectCompoundFile(file.FullName, OfficeDocumentKind.ExcelBinary),
            ".docx" => InspectOpenXmlPackage(file.FullName, OfficeDocumentKind.WordOpenXml),
            ".xlsx" => InspectOpenXmlPackage(file.FullName, OfficeDocumentKind.ExcelOpenXml),
            _ => OfficeDocumentInspection.Invalid("UNSUPPORTED_EXTENSION"),
        };
    }

    private static OfficeDocumentInspection InspectCompoundFile(
        string path,
        OfficeDocumentKind expectedKind)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[CompoundFileHeader.Length];
        if (stream.Read(header) != header.Length || !header.SequenceEqual(CompoundFileHeader))
        {
            return OfficeDocumentInspection.Invalid("SIGNATURE_MISMATCH");
        }

        var hasExpectedDirectoryEntry = expectedKind switch
        {
            OfficeDocumentKind.WordBinary => ContainsPattern(stream, WordDocumentStreamName),
            OfficeDocumentKind.ExcelBinary =>
                ContainsPattern(stream, ExcelWorkbookStreamName) ||
                ContainsPattern(stream, ExcelBookStreamName),
            _ => false,
        };

        return hasExpectedDirectoryEntry
            ? OfficeDocumentInspection.Valid(expectedKind)
            : OfficeDocumentInspection.Invalid("OFFICE_BINARY_TYPE_MISMATCH");
    }

    private static OfficeDocumentInspection InspectOpenXmlPackage(
        string path,
        OfficeDocumentKind expectedKind)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entryNames = archive.Entries
                .Select(static entry => entry.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mainPart = expectedKind switch
            {
                OfficeDocumentKind.WordOpenXml => "word/document.xml",
                OfficeDocumentKind.ExcelOpenXml => "xl/workbook.xml",
                _ => string.Empty,
            };

            return entryNames.Contains("[Content_Types].xml") && entryNames.Contains(mainPart)
                ? OfficeDocumentInspection.Valid(expectedKind)
                : OfficeDocumentInspection.Invalid("OFFICE_OPEN_XML_TYPE_MISMATCH");
        }
        catch (InvalidDataException)
        {
            return OfficeDocumentInspection.Invalid("SIGNATURE_MISMATCH");
        }
    }

    private static bool ContainsPattern(FileStream stream, ReadOnlySpan<byte> pattern)
    {
        stream.Position = 0;
        var buffer = new byte[(64 * 1024) + pattern.Length - 1];
        var carry = 0;

        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            var length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(pattern) >= 0)
            {
                return true;
            }

            if (read == 0)
            {
                return false;
            }

            carry = Math.Min(pattern.Length - 1, length);
            buffer.AsSpan(length - carry, carry).CopyTo(buffer);
        }
    }
}
