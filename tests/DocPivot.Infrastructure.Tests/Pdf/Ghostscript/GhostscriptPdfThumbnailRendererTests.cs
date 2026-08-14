using System.Globalization;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf.Ghostscript;

public sealed class GhostscriptPdfThumbnailRendererTests
{
    [Fact]
    public async Task RenderAsync_UsesCropBoxAndHighResolutionForCompletePages()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "样例 文档.pdf");
            var outputDirectory = Path.Combine(root, "thumbnails");
            GhostscriptTestFiles.CreateMinimalPdf(inputPath, "THUMB");
            var runner = new RecordingRunner((_, arguments, _, _) =>
            {
                var outputPath = GetOutputPattern(arguments)
                    .Replace("%04d", 1.ToString("D4", CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, [1]);
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            });
            var renderer = new GhostscriptPdfThumbnailRenderer(new ReadyProbe(), runner);

            var thumbnails = await renderer.RenderAsync(inputPath, 1, outputDirectory);

            Assert.Single(thumbnails);
            // The preview must match what PDF readers show: the CropBox region,
            // never the full MediaBox (which can hide the visible page content).
            Assert.Contains("-dUseCropBox", runner.Arguments);
            Assert.DoesNotContain("-dUseMediaBox", runner.Arguments);
            Assert.Contains("-r96", runner.Arguments);
            Assert.Equal("-f", runner.Arguments[^2]);
            Assert.Equal(Path.GetFullPath(inputPath), runner.Arguments[^1]);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task RenderAsync_RendersOnlyTheVisibleCropBoxRegion()
    {
        var root = GhostscriptTestFiles.CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(root, "裁切 样例.pdf");
            var outputDirectory = Path.Combine(root, "render");
            GhostscriptTestFiles.CreateMinimalPdf(
                inputPath,
                "CROP",
                mediaBox: "[0 0 300 300]",
                cropBox: "[50 50 150 250]");
            var renderer = new GhostscriptPdfThumbnailRenderer(
                GhostscriptTestFiles.FindRepositoryRoot());

            var thumbnails = await renderer.RenderAsync(inputPath, 1, outputDirectory);

            var thumbnail = Assert.Single(thumbnails);
            var (width, height) = ReadPngSize(thumbnail);
            // CropBox is 100x200pt (ratio 1:2); the MediaBox 300x300 would give 1:1.
            Assert.Equal(2.0, (double)height / width, 0.05);
        }
        finally
        {
            GhostscriptTestFiles.DeleteTestRoot(root);
        }
    }

    private static string GetOutputPattern(IReadOnlyList<string> arguments) =>
        arguments.Single(static argument =>
            argument.StartsWith("-sOutputFile=", StringComparison.Ordinal))
        ["-sOutputFile=".Length..];

    private static (int Width, int Height) ReadPngSize(string path)
    {
        var header = File.ReadAllBytes(path);
        Assert.True(header.Length > 24);
        var width = System.Buffers.Binary.BinaryPrimitives
            .ReadInt32BigEndian(header.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives
            .ReadInt32BigEndian(header.AsSpan(20, 4));
        return (width, height);
    }

    private sealed class ReadyProbe : IGhostscriptRuntimeProbe
    {
        public Task<GhostscriptEngineStatus> ProbeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GhostscriptEngineStatus.Ready(
                "fake-gswin64c.exe",
                GhostscriptPdfOptimizer.PinnedVersion));
        }
    }

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ExternalProcessResult>> handler)
        : IGhostscriptProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            return handler(executablePath, arguments, timeout, cancellationToken);
        }
    }
}
