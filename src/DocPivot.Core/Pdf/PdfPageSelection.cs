using System.Collections.ObjectModel;

namespace DocPivot.Core.Pdf;

public sealed class PdfPageSelection
{
    internal PdfPageSelection(IReadOnlyList<int> pages, string normalizedExpression)
    {
        Pages = new ReadOnlyCollection<int>(pages.ToArray());
        NormalizedExpression = normalizedExpression;
    }

    public IReadOnlyList<int> Pages { get; }

    public string NormalizedExpression { get; }

    public int Count => Pages.Count;

    public override string ToString() => NormalizedExpression;
}
