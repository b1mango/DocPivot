using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Pdf;

public interface IPdfEngineStatusProvider
{
    Task<PdfEngineStatus> ProbeEnginesAsync(CancellationToken cancellationToken = default);
}

public sealed record PdfEngineStatus(
    QpdfToolStatus Qpdf,
    GhostscriptEngineStatus Ghostscript)
{
    public bool IsCoreReady => Qpdf.IsReady;

    public bool IsLossyCompressionReady => Qpdf.IsReady && Ghostscript.IsReady;
}
