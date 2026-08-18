using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class PdfRuntimeDistributionRootTests
{
    [Fact]
    public void Resolve_FindsRuntimeInConfiguredParentDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DocPivot.Tests",
            "PdfRuntimeRoot",
            Guid.NewGuid().ToString("N"));
        var configuredDirectory = Path.Combine(root, "extracted", "app");
        try
        {
            Directory.CreateDirectory(configuredDirectory);
            var qpdfPath = Path.Combine(root, QpdfToolProbe.RelativeExecutablePath);
            var ghostscriptPath = Path.Combine(
                root,
                GhostscriptPdfOptimizer.RelativeExecutablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(qpdfPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ghostscriptPath)!);
            File.WriteAllText(qpdfPath, string.Empty);
            File.WriteAllText(ghostscriptPath, string.Empty);

            var resolved = PdfRuntimeDistributionRoot.Resolve(configuredDirectory);

            Assert.Equal(Path.GetFullPath(root), resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
