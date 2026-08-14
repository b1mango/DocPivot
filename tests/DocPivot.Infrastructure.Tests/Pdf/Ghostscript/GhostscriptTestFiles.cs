using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

internal static class GhostscriptTestFiles
{
    public static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DocPivot.Tests",
            "Ghostscript 中文 空格",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CreateMinimalPdf(
        string path,
        string marker,
        int unusedPaddingBytes = 0,
        string mediaBox = "[0 0 300 200]",
        string? cropBox = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unusedPaddingBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var escapedMarker = marker
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var content = Encoding.ASCII.GetBytes(
            $"BT /F1 12 Tf 40 100 Td ({escapedMarker}) Tj ET\n");
        var pageBoxes = cropBox is null
            ? $"/MediaBox {mediaBox} "
            : $"/MediaBox {mediaBox} /CropBox {cropBox} ";
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(
                "<< /Type /Page /Parent 2 0 R " + pageBoxes +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Combine(
                Ascii($"<< /Length {content.Length} >>\nstream\n"),
                content,
                Ascii("endstream")),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        if (unusedPaddingBytes > 0)
        {
            var padding = Enumerable.Repeat((byte)'X', unusedPaddingBytes).ToArray();
            objects.Add(Combine(
                Ascii($"<< /Length {padding.Length} >>\nstream\n"),
                padding,
                Ascii("\nendstream")));
        }

        using var stream = new MemoryStream();
        Write(stream, Ascii("%PDF-1.4\n"));
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            Write(stream, Ascii($"{index + 1} 0 obj\n"));
            Write(stream, objects[index]);
            Write(stream, Ascii("\nendobj\n"));
        }

        var xrefOffset = stream.Position;
        Write(stream, Ascii($"xref\n0 {objects.Count + 1}\n"));
        Write(stream, Ascii("0000000000 65535 f \n"));
        foreach (var offset in offsets.Skip(1))
        {
            Write(stream, Ascii(
                $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n"));
        }

        Write(stream, Ascii(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n"));
        File.WriteAllBytes(path, stream.ToArray());
    }

    public static string GetSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DocPivot repository root.");
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(static part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes);
}
