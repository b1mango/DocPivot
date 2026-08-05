using System.Security;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Pdf;

public sealed class CompositePdfOperationsClient : IPdfOperationsClient, IPdfEngineStatusProvider
{
    private readonly IPdfOperationsClient _qpdf;
    private readonly IQpdfToolProvider _qpdfProbe;
    private readonly IGhostscriptPdfOptimizer _ghostscript;

    public CompositePdfOperationsClient(string distributionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        _qpdf = new QpdfOperationsClient(distributionRoot);
        _qpdfProbe = new QpdfToolProbe(distributionRoot);
        _ghostscript = new GhostscriptPdfOptimizer(distributionRoot);
    }

    internal CompositePdfOperationsClient(
        IPdfOperationsClient qpdf,
        IQpdfToolProvider qpdfProbe,
        IGhostscriptPdfOptimizer ghostscript)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        ArgumentNullException.ThrowIfNull(qpdfProbe);
        ArgumentNullException.ThrowIfNull(ghostscript);
        _qpdf = qpdf;
        _qpdfProbe = qpdfProbe;
        _ghostscript = ghostscript;
    }

    public async Task<PdfEngineStatus> ProbeEnginesAsync(
        CancellationToken cancellationToken = default)
    {
        var qpdfTask = _qpdfProbe.ProbeAsync(cancellationToken);
        var ghostscriptTask = _ghostscript.ProbeAsync(cancellationToken);
        await Task.WhenAll(qpdfTask, ghostscriptTask).ConfigureAwait(false);
        return new PdfEngineStatus(
            await qpdfTask.ConfigureAwait(false),
            await ghostscriptTask.ConfigureAwait(false));
    }

    public Task<PdfPreflightResult> PreflightAsync(
        PdfPreflightRequest request,
        CancellationToken cancellationToken = default) =>
        _qpdf.PreflightAsync(request, cancellationToken);

    public Task<OperationExecutionResult> MergeAsync(
        PdfMergeRequest request,
        CancellationToken cancellationToken = default) =>
        _qpdf.MergeAsync(request, cancellationToken);

    public Task<OperationExecutionResult> SplitAsync(
        PdfSplitRequest request,
        CancellationToken cancellationToken = default) =>
        _qpdf.SplitAsync(request, cancellationToken);

    public Task<OperationExecutionResult> OptimizeAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.CompressionStrength == 0
            ? _qpdf.OptimizeAsync(request, cancellationToken)
            : OptimizeLossyAsync(request, cancellationToken);
    }

    private async Task<OperationExecutionResult> OptimizeLossyAsync(
        PdfOptimizeRequest request,
        CancellationToken cancellationToken)
    {
        var sourcePreflight = await _qpdf.PreflightAsync(
                new PdfPreflightRequest(request.InputPath, request.Password),
                cancellationToken)
            .ConfigureAwait(false);
        if (!sourcePreflight.IsSucceeded)
        {
            return FromPreflightFailure(sourcePreflight);
        }

        // Signature fields are rewritten on the working copy and the output
        // signature becomes invalid; the source file is never modified. This
        // matches the "force compression" requirement, so signed PDFs are
        // compressed without blocking and the result carries a notice.
        var result = await _ghostscript.OptimizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSucceeded || result.Artifacts.Count == 0)
        {
            return result;
        }

        try
        {
            var outputPreflight = await _qpdf.PreflightAsync(
                    new PdfPreflightRequest(result.Artifacts[0]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!outputPreflight.IsSucceeded ||
                outputPreflight.PageCount != sourcePreflight.PageCount)
            {
                DeleteArtifacts(result.Artifacts);
                return OperationExecutionResult.Failed(
                    "PDF_OUTPUT_PAGE_COUNT_MISMATCH",
                    "Lossy compression did not preserve the complete PDF page set.",
                    false);
            }

            return result;
        }
        catch
        {
            DeleteArtifacts(result.Artifacts);
            throw;
        }
    }

    private static OperationExecutionResult FromPreflightFailure(PdfPreflightResult result) =>
        OperationExecutionResult.Failed(
            result.ErrorCode ?? "PDF_PREFLIGHT_FAILED",
            result.ErrorMessage ?? "PDF preflight failed.",
            result.IsRetryable);

    private static void DeleteArtifacts(IReadOnlyList<string> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            try
            {
                File.Delete(artifact);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Invalid output cleanup is best effort; callers still receive a failed result.
            }
        }
    }
}
