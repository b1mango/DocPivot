using System.Globalization;

namespace DocPivot.Infrastructure.Pdf.Ghostscript;

public interface IPdfThumbnailRenderer
{
    Task<IReadOnlyList<string>> RenderAsync(
        string inputPath,
        int pageCount,
        string outputDirectory,
        string? password = null,
        CancellationToken cancellationToken = default);
}

public sealed class GhostscriptPdfThumbnailRenderer : IPdfThumbnailRenderer
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(5);

    private readonly GhostscriptRuntimeProbe _probe;
    private readonly GhostscriptProcessRunner _runner;

    public GhostscriptPdfThumbnailRenderer(string distributionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionRoot);
        var runner = new GhostscriptProcessRunner();
        _runner = runner;
        _probe = new GhostscriptRuntimeProbe(distributionRoot, runner, ProbeTimeout);
    }

    public async Task<IReadOnlyList<string>> RenderAsync(
        string inputPath,
        int pageCount,
        string outputDirectory,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        if (password?.Length > 1024)
        {
            throw new ArgumentException("The PDF password is too long.", nameof(password));
        }

        var sourcePath = Path.GetFullPath(inputPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The PDF input does not exist.", sourcePath);
        }

        var destinationDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var engine = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!engine.IsReady || engine.ExecutablePath is null)
        {
            throw new InvalidOperationException(
                engine.ErrorMessage ?? "The local PDF preview engine is unavailable.");
        }

        var outputPattern = Path.Combine(destinationDirectory, "page-%04d.png");
        var arguments = new List<string>
        {
            "-q",
            "-dSAFER",
            "-dBATCH",
            "-dNOPAUSE",
            "-dNOPROMPT",
            "-dUseCropBox",
            "-sDEVICE=png16m",
            "-dTextAlphaBits=4",
            "-dGraphicsAlphaBits=2",
            "-r48",
            "-dFirstPage=1",
            $"-dLastPage={pageCount.ToString(CultureInfo.InvariantCulture)}",
            $"-sOutputFile={outputPattern}",
        };
        if (password is not null)
        {
            arguments.Add($"-sPDFPassword={password}");
        }

        arguments.Add("-f");
        arguments.Add(sourcePath);
        var result = await _runner.RunAsync(
                engine.ExecutablePath,
                arguments,
                RenderTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Ghostscript could not render the PDF page preview.");
        }

        var thumbnails = Directory
            .EnumerateFiles(destinationDirectory, "page-*.png", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (thumbnails.Length != pageCount)
        {
            throw new InvalidOperationException(
                $"The PDF preview produced {thumbnails.Length} pages; expected {pageCount}.");
        }

        return thumbnails;
    }
}
