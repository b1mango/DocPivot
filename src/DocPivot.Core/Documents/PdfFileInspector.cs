using System.Text;

namespace DocPivot.Core.Documents;

public static class PdfFileInspector
{
    private static readonly byte[] Header = "%PDF-"u8.ToArray();
    private static readonly byte[] EndOfFileMarker = Encoding.ASCII.GetBytes("%%EOF");

    public static PdfFileInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists || file.Length < Header.Length + EndOfFileMarker.Length)
        {
            return PdfFileInspection.Invalid("PDF_OUTPUT_MISSING_OR_EMPTY");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[Header.Length];
        if (stream.Read(header) != header.Length || !header.SequenceEqual(Header))
        {
            return PdfFileInspection.Invalid("PDF_HEADER_INVALID");
        }

        var tailLength = (int)Math.Min(2_048, stream.Length);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        _ = stream.Read(tail);

        return tail.AsSpan().IndexOf(EndOfFileMarker) >= 0
            ? PdfFileInspection.Valid()
            : PdfFileInspection.Invalid("PDF_EOF_MARKER_MISSING");
    }
}
