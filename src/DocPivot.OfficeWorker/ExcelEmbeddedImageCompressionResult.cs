namespace DocPivot.OfficeWorker;

internal sealed record ExcelEmbeddedImageCompressionResult(
    int ImageCount,
    int CompressedImageCount,
    long BytesBefore,
    long BytesAfter)
{
    public static ExcelEmbeddedImageCompressionResult Unchanged { get; } = new(0, 0, 0, 0);
}
