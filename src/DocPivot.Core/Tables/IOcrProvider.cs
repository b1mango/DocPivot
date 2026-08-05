namespace DocPivot.Core.Tables;

public sealed record OcrPageRequest(
    int PageNumber,
    string ImagePath,
    int PixelWidth,
    int PixelHeight,
    int Dpi,
    string Language);

public interface IOcrProvider : IDisposable
{
    Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
        OcrPageRequest request,
        CancellationToken cancellationToken = default);
}
