using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Runtime;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfWarningTests
{
    private const string Warning = "WARNING: input.pdf: reported number of objects (6) is not one plus the highest object number (6)";
    private const string WarningSummary = "qpdf: operation succeeded with warnings";
    private const string WriteWarningSummary = "qpdf: operation succeeded with warnings; resulting file may have some problems";
    private const string Error = "qpdf: input.pdf: unable to find trailer dictionary while recovering damaged file";

    [Theory]
    [InlineData(0, "", true, true, false)]
    [InlineData(0, Warning, true, true, true)]
    [InlineData(3, Warning + "\n" + WarningSummary, true, true, true)]
    [InlineData(2, "", true, false, false)]
    [InlineData(2, Warning, true, false, true)]
    [InlineData(2, "\r\n " + Warning + "\r\n" + WarningSummary + "\r\n", true, false, true)]
    [InlineData(2, Warning + "\n" + WriteWarningSummary, true, false, true)]
    [InlineData(2, Error, false, false, false)]
    [InlineData(2, Warning + "\n" + Error, false, false, false)]
    [InlineData(2, "qpdf: open input.pdf: Permission denied", false, false, false)]
    [InlineData(1, "", false, false, false)]
    [InlineData(4, Warning, false, false, false)]
    public async Task PreflightAsync_ClassifiesEncryptionProbe(
        int exitCode,
        string standardError,
        bool expectedSuccess,
        bool expectedEncrypted,
        bool expectedWarnings)
    {
        var root = CreateTestRoot();
        try
        {
            var input = CreateInput(root, "input.pdf");
            var runner = new FakeRunner(arguments => arguments.Contains("--is-encrypted")
                ? new ExternalProcessResult(exitCode, "", standardError)
                : SuccessfulInspection(arguments));
            var client = CreateClient(runner);

            var result = await client.PreflightAsync(new PdfPreflightRequest(input, "password"));

            Assert.Equal(expectedSuccess, result.IsSucceeded);
            Assert.Equal(expectedEncrypted, result.IsEncrypted);
            Assert.Equal(expectedWarnings, result.HasStructureWarnings);
            if (expectedSuccess)
            {
                Assert.Equal(1, result.PageCount);
                Assert.Equal(3, runner.Calls.Count);
                Assert.All(runner.Calls.Skip(1), arguments =>
                    Assert.Equal(expectedEncrypted, arguments.Contains("--password=password")));
            }
            else
            {
                Assert.Equal("PDF_PREFLIGHT_FAILED", result.ErrorCode);
                Assert.Null(result.PageCount);
                Assert.Single(runner.Calls);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("--check", 0, "", true, false)]
    [InlineData("--check", 0, Warning, true, true)]
    [InlineData("--check", 3, Warning + "\n" + WarningSummary, true, true)]
    [InlineData("--check", 3, "", true, true)]
    [InlineData("--check", 2, Error, false, false)]
    [InlineData("--check", 2, Warning, false, false)]
    [InlineData("--json", 0, "", true, false)]
    [InlineData("--json", 0, Warning, true, true)]
    [InlineData("--json", 3, Warning + "\n" + WarningSummary, true, true)]
    [InlineData("--json", 3, "", true, true)]
    [InlineData("--json", 2, Error, false, false)]
    [InlineData("--json", 2, Warning, false, false)]
    public async Task PreflightAsync_ClassifiesCheckAndJson(
        string command,
        int exitCode,
        string standardError,
        bool expectedSuccess,
        bool expectedWarnings)
    {
        var root = CreateTestRoot();
        try
        {
            var input = CreateInput(root, "input.pdf");
            var runner = new FakeRunner(arguments =>
            {
                var result = SuccessfulInspection(arguments, pageCount: 2);
                return arguments.Contains(command)
                    ? new ExternalProcessResult(exitCode, result.StandardOutput, standardError)
                    : result;
            });
            var client = CreateClient(runner);

            var result = await client.PreflightAsync(new PdfPreflightRequest(input));

            Assert.Equal(expectedSuccess, result.IsSucceeded);
            Assert.Equal(expectedWarnings, result.HasStructureWarnings);
            Assert.False(result.IsEncrypted);
            if (expectedSuccess)
            {
                Assert.Equal(2, result.PageCount);
                Assert.Null(result.ErrorCode);
            }
            else
            {
                Assert.Equal(command == "--check" ? "PDF_STRUCTURE_INVALID" : "PDF_METADATA_INSPECTION_FAILED", result.ErrorCode);
                Assert.Null(result.PageCount);
            }

            Assert.Equal(command == "--check" && !expectedSuccess ? 2 : 3, runner.Calls.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreflightAsync_JsonWarningDoesNotBypassMetadataValidation()
    {
        var root = CreateTestRoot();
        try
        {
            var input = CreateInput(root, "input.pdf");
            var runner = new FakeRunner(arguments => arguments.Contains("--json")
                ? new ExternalProcessResult(3, "{}", Warning)
                : SuccessfulInspection(arguments));

            var result = await CreateClient(runner).PreflightAsync(new PdfPreflightRequest(input));

            Assert.False(result.IsSucceeded);
            Assert.Equal("PDF_METADATA_INVALID", result.ErrorCode);
            Assert.False(result.HasStructureWarnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("merge", 0)]
    [InlineData("merge", 3)]
    [InlineData("merge", 2)]
    [InlineData("rotate", 0)]
    [InlineData("rotate", 3)]
    [InlineData("rotate", 2)]
    [InlineData("split-native", 0)]
    [InlineData("split-native", 3)]
    [InlineData("split-native", 2)]
    [InlineData("split-selected", 0)]
    [InlineData("split-selected", 3)]
    [InlineData("split-selected", 2)]
    [InlineData("optimize", 0)]
    [InlineData("optimize", 3)]
    [InlineData("optimize", 2)]
    public async Task WriteOperations_AcceptWarningsAndRejectErrors(string operation, int exitCode)
    {
        var root = CreateTestRoot();
        try
        {
            var first = CreateInput(root, "first.pdf");
            var second = CreateInput(root, "second.pdf");
            var output = Path.Combine(root, "output.pdf");
            var writtenPages = new Dictionary<string, int>(StringComparer.Ordinal);
            var runner = new FakeRunner(arguments =>
            {
                if (IsInspection(arguments))
                {
                    return SuccessfulInspection(arguments, writtenPages.GetValueOrDefault(arguments[^1], 1));
                }

                var outputPath = arguments[^1].Replace("%d", "1", StringComparison.Ordinal);
                File.WriteAllText(outputPath, "rewritten PDF");
                writtenPages.Add(outputPath, arguments.Contains(second) ? 2 : 1);
                var isRotation = arguments.Any(argument => argument.StartsWith("--rotate=", StringComparison.Ordinal));
                var commandExitCode = operation != "rotate" || isRotation ? exitCode : 0;
                return new ExternalProcessResult(commandExitCode, "", commandExitCode switch
                {
                    3 => Warning + "\n" + WriteWarningSummary,
                    2 => Error,
                    _ => "",
                });
            });
            var client = CreateClient(runner);

            var result = operation switch
            {
                "merge" => await client.MergeAsync(new PdfMergeRequest([first, second], output)),
                "rotate" => await client.MergeAsync(new PdfMergeRequest([first, second], output, Rotations: [90, 0])),
                "split-native" => await client.SplitAsync(new PdfSplitRequest(first, output, PdfSplitSelection.EveryPage())),
                "split-selected" => await client.SplitAsync(new PdfSplitRequest(first, output, PdfSplitSelection.CustomRange("1"))),
                "optimize" => await client.OptimizeAsync(new PdfOptimizeRequest(first, output)),
                _ => throw new InvalidOperationException("Unknown test operation."),
            };

            Assert.Equal(exitCode != 2, result.IsSucceeded);
            Assert.All(runner.Calls.Where(arguments => !IsInspection(arguments)), arguments => Assert.Contains("--decrypt", arguments));
            if (exitCode == 2)
            {
                Assert.Equal(operation switch
                {
                    "merge" => "PDF_MERGE_FAILED",
                    "rotate" => "PDF_ROTATION_FAILED",
                    "optimize" => "PDF_OPTIMIZATION_FAILED",
                    _ => "PDF_SPLIT_FAILED",
                }, result.ErrorCode);
                Assert.Empty(result.Artifacts);
                Assert.False(File.Exists(output));
            }
            else
            {
                Assert.Single(result.Artifacts);
                Assert.True(File.Exists(result.Artifacts[0]));
                Assert.Contains(runner.Calls, arguments =>
                    arguments.Contains("--json") && writtenPages.ContainsKey(arguments[^1]));
            }

            Assert.All(writtenPages.Keys, path => Assert.False(File.Exists(path)));
            Assert.Equal("%PDF-1.7\n%%EOF", File.ReadAllText(first));
            Assert.Equal("%PDF-1.7\n%%EOF", File.ReadAllText(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing", "PDF_OUTPUT_INVALID")]
    [InlineData("page-count", "PDF_OUTPUT_PAGE_COUNT_MISMATCH")]
    [InlineData("structure", "PDF_OUTPUT_INVALID")]
    public async Task MergeAsync_WarningDoesNotBypassOutputValidation(string outputProblem, string expectedError)
    {
        var root = CreateTestRoot();
        try
        {
            var first = CreateInput(root, "first.pdf");
            var second = CreateInput(root, "second.pdf");
            var output = Path.Combine(root, "output.pdf");
            string? staging = null;
            var runner = new FakeRunner(arguments =>
            {
                if (IsInspection(arguments))
                {
                    if (arguments[^1] == staging && outputProblem == "structure" && arguments.Contains("--check"))
                    {
                        return new ExternalProcessResult(2, "", Error);
                    }

                    return SuccessfulInspection(arguments);
                }

                staging = arguments[^1];
                if (outputProblem != "missing")
                {
                    File.WriteAllText(staging, "rewritten PDF");
                }

                return new ExternalProcessResult(3, "", Warning + "\n" + WriteWarningSummary);
            });

            var result = await CreateClient(runner).MergeAsync(new PdfMergeRequest([first, second], output));

            Assert.False(result.IsSucceeded);
            Assert.Equal(expectedError, result.ErrorCode);
            Assert.False(File.Exists(output));
            Assert.NotNull(staging);
            Assert.False(File.Exists(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static QpdfOperationsClient CreateClient(IQpdfCliRunner runner) =>
        new(new ReadyProvider(), runner, TimeSpan.FromSeconds(5));

    private static bool IsInspection(IReadOnlyList<string> arguments) =>
        arguments.Contains("--is-encrypted") || arguments.Contains("--check") || arguments.Contains("--json");

    private static ExternalProcessResult SuccessfulInspection(IReadOnlyList<string> arguments, int pageCount = 1) =>
        arguments.Contains("--is-encrypted")
            ? new ExternalProcessResult(2, "", "")
            : new ExternalProcessResult(0, arguments.Contains("--json")
                ? "{\"pages\":[" + string.Join(',', Enumerable.Repeat("{}", pageCount)) + "],\"encrypt\":{\"encrypted\":false}}"
                : "", "");

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateInput(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "%PDF-1.7\n%%EOF");
        return path;
    }

    private sealed class ReadyProvider : IQpdfToolProvider
    {
        public Task<QpdfToolStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(QpdfToolStatus.Ready("fake-qpdf", QpdfToolProbe.PinnedVersion));
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ExternalProcessResult> run) : IQpdfCliRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(run(arguments));
        }
    }
}
