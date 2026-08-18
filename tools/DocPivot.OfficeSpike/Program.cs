using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Excel;
using DocPivot.Infrastructure.Office;
using DocPivot.OfficeWorker;

namespace DocPivot.OfficeSpike;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var scopeArgument = args.SingleOrDefault(static argument =>
            argument.StartsWith("--scope=", StringComparison.OrdinalIgnoreCase));
        var excelQaArgument = args.SingleOrDefault(static argument =>
            argument.StartsWith("--excel-qa=", StringComparison.OrdinalIgnoreCase));
        var scope = scopeArgument?["--scope=".Length..] ?? "All";
        if (!string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scope, "ExcelTools", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scope, "ExcelPreservation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scope, "ExcelSession", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unsupported Office spike scope: {scope}");
            return 64;
        }

        var excelQaPath = excelQaArgument?["--excel-qa=".Length..];
        if (!string.IsNullOrWhiteSpace(excelQaPath))
        {
            excelQaPath = Path.GetFullPath(excelQaPath);
            if (!File.Exists(excelQaPath))
            {
                Console.Error.WriteLine($"Excel QA workbook is missing: {excelQaPath}");
                return 66;
            }
        }

        var positionalArguments = args
            .Where(static argument =>
                !argument.StartsWith("--scope=", StringComparison.OrdinalIgnoreCase) &&
                !argument.StartsWith("--excel-qa=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var timeoutSeconds = positionalArguments.Length >= 1 &&
            int.TryParse(positionalArguments[0], out var parsedTimeout)
            ? parsedTimeout
            : 60;
        var root = FindRepositoryRoot();
        var outputRoot = Path.Combine(root, "artifacts", "office-spike");
        PrepareOutputDirectory(root, outputRoot);
        var appPath = positionalArguments.Length >= 2
            ? Path.GetFullPath(positionalArguments[1])
            : Path.Combine(
                root,
                "src",
                "DocPivot.App",
                "bin",
                "Debug",
                "net10.0-windows",
                "DocPivot.exe");

        var cases = new[]
        {
            new SpikeCase("word", ".docx", "word-minimal"),
            new SpikeCase("excel", ".xlsx", "excel-minimal"),
        };

        var failed = false;
        var client = new OfficeWorkerClient(
            appPath,
            ["--office-worker"],
            TimeSpan.FromSeconds(timeoutSeconds));
        var excelTemplatePath = Path.Combine(outputRoot, "excel-multi-sheet.xlsx");
        var excelTemplateSource = Path.Combine(
            root,
            "tests",
            "fixtures",
            "office",
            "excel-multi-sheet");
        CreateOpenXmlPackage(excelTemplateSource, excelTemplatePath);
        if (string.Equals(scope, "ExcelSession", StringComparison.OrdinalIgnoreCase))
        {
            using var session = ExcelApplicationSession.Start(
                Guid.NewGuid(),
                static _ => { },
                static _ => { });
            var workbook = session.OpenWorkbook(excelQaPath ?? excelTemplatePath, readOnly: true);
            session.CloseWorkbook(workbook);
            Console.WriteLine("excel-session: isolated startup, workbook open, and cleanup passed");
            return 0;
        }

        if (string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var spikeCase in cases)
            {
                var inputPath = Path.Combine(outputRoot, spikeCase.Name + spikeCase.Extension);
                var outputPath = Path.Combine(outputRoot, spikeCase.Name + ".pdf");
                var sourceDirectory = Path.Combine(
                    root,
                    "tests",
                    "fixtures",
                    "office",
                    spikeCase.SourceDirectory);
                CreateOpenXmlPackage(sourceDirectory, inputPath);

                OfficeConversionResult result;
                try
                {
                    result = await client.ConvertAsync(
                        inputPath,
                        outputPath,
                        null,
                        new InlineProgress<WorkerProgressMessage>(message =>
                            Console.WriteLine($"{spikeCase.Name}: {message.Stage} {message.Current}/{message.Total}")));
                }
                catch (TimeoutException exception)
                {
                    Console.Error.WriteLine($"{spikeCase.Name}: {exception.Message}");
                    failed = true;
                    continue;
                }

                if (!result.IsSucceeded)
                {
                    Console.Error.WriteLine($"{spikeCase.Name}: {result.ErrorCode} {result.ErrorMessage}");
                    failed = true;
                    continue;
                }

                var inspection = PdfFileInspector.Inspect(outputPath);
                if (!inspection.IsValid)
                {
                    Console.Error.WriteLine($"{spikeCase.Name}: {inspection.ErrorCode}");
                    failed = true;
                }
            }
        }

        if (string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase) &&
            !await RunExcelWorksheetScopeSpikeAsync(outputRoot, client))
        {
            failed = true;
        }

        if (!string.Equals(scope, "ExcelPreservation", StringComparison.OrdinalIgnoreCase) &&
            !await RunExcelToolsSpikeAsync(outputRoot, client))
        {
            failed = true;
        }

        if (string.Equals(scope, "ExcelPreservation", StringComparison.OrdinalIgnoreCase) &&
            excelQaPath is null)
        {
            Console.Error.WriteLine("ExcelPreservation scope requires --excel-qa.");
            return 64;
        }

        if (excelQaPath is not null &&
            !await RunExcelPreservationSpikeAsync(outputRoot, client, excelQaPath))
        {
            failed = true;
        }

        if (failed)
        {
            Console.Error.WriteLine("One or more Office conversion spike cases failed.");
            return 2;
        }

        Console.WriteLine($"Office conversion spike passed: {outputRoot}");
        return 0;
    }

    private static async Task<bool> RunExcelWorksheetScopeSpikeAsync(
        string outputRoot,
        OfficeWorkerClient client)
    {
        var inputPath = Path.Combine(outputRoot, "excel-multi-sheet.xlsx");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(inputPath));

        try
        {
            var worksheetList = await client.ListExcelWorksheetsAsync(inputPath);
            if (!worksheetList.IsSucceeded)
            {
                Console.Error.WriteLine(
                    $"excel-scope-list: {worksheetList.ErrorCode} {worksheetList.ErrorMessage}");
                return false;
            }

            var expected = new[]
            {
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("明细 数据", 2, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 3, "hidden", false),
            };
            if (!worksheetList.Worksheets.SequenceEqual(expected))
            {
                Console.Error.WriteLine(
                    "excel-scope-list: worksheet names, order, visibility, or exportability did not match.");
                return false;
            }

            var completeWorkbookOutput = Path.Combine(outputRoot, "excel-multi-all.pdf");
            var completeWorkbookResult = await client.ConvertAsync(inputPath, completeWorkbookOutput);
            if (!IsValidPdfResult(
                    completeWorkbookResult,
                    completeWorkbookOutput,
                    "excel-scope-all",
                    expectedPageCount: 2))
            {
                return false;
            }

            var selectedWorksheetOutput = Path.Combine(outputRoot, "excel-multi-details.pdf");
            var selectedWorksheetResult = await client.ConvertAsync(
                inputPath,
                selectedWorksheetOutput,
                new OfficeConversionOptions("明细 数据"));
            if (!IsValidPdfResult(
                    selectedWorksheetResult,
                    selectedWorksheetOutput,
                    "excel-scope-selected",
                    expectedPageCount: 1))
            {
                return false;
            }

            var missingOutput = Path.Combine(outputRoot, "excel-multi-missing.pdf");
            var missingResult = await client.ConvertAsync(
                inputPath,
                missingOutput,
                new OfficeConversionOptions("不存在的工作表"));
            if (missingResult.IsSucceeded ||
                missingResult.ErrorCode != "EXCEL_WORKSHEET_NOT_FOUND" ||
                File.Exists(missingOutput))
            {
                Console.Error.WriteLine("excel-scope-missing: expected EXCEL_WORKSHEET_NOT_FOUND without output.");
                return false;
            }

            var hiddenOutput = Path.Combine(outputRoot, "excel-multi-hidden.pdf");
            var hiddenResult = await client.ConvertAsync(
                inputPath,
                hiddenOutput,
                new OfficeConversionOptions("内部参数"));
            if (hiddenResult.IsSucceeded ||
                hiddenResult.ErrorCode != "EXCEL_WORKSHEET_HIDDEN" ||
                File.Exists(hiddenOutput))
            {
                Console.Error.WriteLine("excel-scope-hidden: expected EXCEL_WORKSHEET_HIDDEN without output.");
                return false;
            }

            var finalHash = SHA256.HashData(File.ReadAllBytes(inputPath));
            if (!sourceHash.AsSpan().SequenceEqual(finalHash))
            {
                Console.Error.WriteLine("excel-scope-source: source workbook was modified.");
                return false;
            }

            Console.WriteLine("excel-scope: worksheet discovery and scoped export passed");
            return true;
        }
        catch (TimeoutException exception)
        {
            Console.Error.WriteLine($"excel-scope: {exception.Message}");
            return false;
        }
    }

    private static bool IsValidPdfResult(
        OfficeConversionResult result,
        string outputPath,
        string caseName,
        int expectedPageCount)
    {
        if (!result.IsSucceeded)
        {
            Console.Error.WriteLine($"{caseName}: {result.ErrorCode} {result.ErrorMessage}");
            return false;
        }

        var inspection = PdfFileInspector.Inspect(outputPath);
        if (!inspection.IsValid)
        {
            Console.Error.WriteLine($"{caseName}: {inspection.ErrorCode}");
            return false;
        }

        var actualPageCount = CountPdfPageObjects(File.ReadAllBytes(outputPath));
        if (actualPageCount != expectedPageCount)
        {
            Console.Error.WriteLine(
                $"{caseName}: expected {expectedPageCount} PDF page object(s), found {actualPageCount}.");
            return false;
        }

        return true;
    }

    private static async Task<bool> RunExcelToolsSpikeAsync(
        string outputRoot,
        OfficeWorkerClient client)
    {
        var firstInput = Path.Combine(outputRoot, "excel-multi-sheet.xlsx");
        var secondInput = Path.Combine(outputRoot, "excel-multi-sheet-copy.xlsx");
        File.Copy(firstInput, secondInput);
        var firstHash = SHA256.HashData(File.ReadAllBytes(firstInput));
        var secondHash = SHA256.HashData(File.ReadAllBytes(secondInput));
        var mergedOutput = Path.Combine(outputRoot, "excel-tools-merged.xlsx");

        try
        {
            var progress = new InlineProgress<WorkerProgressMessage>(message =>
                Console.WriteLine($"excel-tools: {message.Stage} {message.Current}/{message.Total}"));
            var mergeResult = await client.ExecuteAsync(
                ExcelOperationRequest.Merge([firstInput, secondInput], mergedOutput),
                progress);
            if (!IsSuccessfulExcelToolResult(mergeResult, "excel-tools-merge"))
            {
                return false;
            }

            var expectedMergedWorksheets = new[]
            {
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("明细 数据", 2, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 3, "hidden", false),
                new ExcelWorksheetDescriptor("总览 (2)", 4, "visible", true),
                new ExcelWorksheetDescriptor("明细 数据 (2)", 5, "visible", true),
                new ExcelWorksheetDescriptor("内部参数 (2)", 6, "hidden", false),
            };
            var mergedWorksheets = await client.ListExcelWorksheetsAsync(mergedOutput);
            if (!mergedWorksheets.IsSucceeded ||
                !mergedWorksheets.Worksheets.SequenceEqual(expectedMergedWorksheets))
            {
                Console.Error.WriteLine(
                    "excel-tools-merge: worksheet order, deterministic names, or visibility did not match.");
                return false;
            }

            var splitDirectory = Path.Combine(outputRoot, "excel-tools-split");
            var splitResult = await client.ExecuteAsync(
                ExcelOperationRequest.Split(mergedOutput, splitDirectory),
                progress);
            if (!IsSuccessfulExcelToolResult(splitResult, "excel-tools-split") ||
                splitResult.Artifacts.Count != expectedMergedWorksheets.Length)
            {
                return false;
            }

            for (var index = 0; index < splitResult.Artifacts.Count; index++)
            {
                var splitWorksheets = await client.ListExcelWorksheetsAsync(splitResult.Artifacts[index]);
                var worksheet = splitWorksheets.Worksheets.SingleOrDefault();
                if (!splitWorksheets.IsSucceeded ||
                    worksheet is null ||
                    worksheet.Name != expectedMergedWorksheets[index].Name ||
                    worksheet.Visibility != "visible")
                {
                    Console.Error.WriteLine(
                        $"excel-tools-split: invalid single-sheet output at index {index}.");
                    return false;
                }
            }

            var compressedOutput = Path.Combine(outputRoot, "excel-tools-compressed.xlsx");
            var compressResult = await client.ExecuteAsync(
                ExcelOperationRequest.Compress(firstInput, compressedOutput),
                progress);
            if (!IsSuccessfulExcelToolResult(compressResult, "excel-tools-compress"))
            {
                return false;
            }

            var compressedWorksheets = await client.ListExcelWorksheetsAsync(compressedOutput);
            if (!compressedWorksheets.IsSucceeded ||
                !compressedWorksheets.Worksheets.SequenceEqual(expectedMergedWorksheets[..3]))
            {
                Console.Error.WriteLine("excel-tools-compress: workbook could not be reopened with original sheets.");
                return false;
            }

            if (!firstHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(firstInput))) ||
                !secondHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(secondInput))))
            {
                Console.Error.WriteLine("excel-tools-source: an input workbook was modified.");
                return false;
            }

            if (!await RunExcelDataSafetySpikeAsync(outputRoot, client, firstInput, secondInput, progress))
            {
                return false;
            }

            Console.WriteLine("excel-tools: merge, split, compression, and source immutability passed");
            return true;
        }
        catch (TimeoutException exception)
        {
            Console.Error.WriteLine($"excel-tools: {exception.Message}");
            return false;
        }
    }

    private static async Task<bool> RunExcelDataSafetySpikeAsync(
        string outputRoot,
        OfficeWorkerClient client,
        string templateInput,
        string secondTemplateInput,
        IProgress<WorkerProgressMessage> progress)
    {
        var dateInputOne = Path.Combine(outputRoot, "excel-date-1904-one.xlsx");
        var dateInputTwo = Path.Combine(outputRoot, "excel-date-1904-two.xlsx");
        File.Copy(templateInput, dateInputOne);
        File.Copy(secondTemplateInput, dateInputTwo);
        SetWorkbookDateSystemFlag(dateInputOne, uses1904DateSystem: true);
        SetWorkbookDateSystemFlag(dateInputTwo, uses1904DateSystem: true);
        var dateOutput = Path.Combine(outputRoot, "excel-date-1904-merged.xlsx");
        var dateResult = await client.ExecuteAsync(
            ExcelOperationRequest.Merge([dateInputOne, dateInputTwo], dateOutput),
            progress);
        if (!IsSuccessfulExcelToolResult(dateResult, "excel-safety-date-system") ||
            !WorkbookUses1904DateSystem(dateOutput))
        {
            Console.Error.WriteLine("excel-safety-date-system: merged output did not preserve Date1904.");
            return false;
        }

        var formulaInput = Path.Combine(outputRoot, "excel-cross-sheet-formula.xlsx");
        File.Copy(templateInput, formulaInput);
        AddCrossSheetFormula(formulaInput);
        var formulaMergeOutput = Path.Combine(outputRoot, "excel-cross-sheet-merged.xlsx");
        var formulaMergeResult = await client.ExecuteAsync(
            ExcelOperationRequest.Merge(
                [formulaInput, secondTemplateInput],
                formulaMergeOutput,
                new ExcelOperationOptions(
                    IncludeHiddenWorksheets: false,
                    PreserveExternalLinks: false,
                    SkipUnsafeCompressionSheets: true,
                    ConvertLegacyWorkbookToOpenXml: true,
                    ImageCompressionLevel: ExcelImageCompressionLevel.None)),
            progress);
        var formulas = ReadWorksheetFormulas(formulaMergeOutput);
        if (!IsSuccessfulExcelToolResult(formulaMergeResult, "excel-safety-group-copy") ||
            HasExternalLinkParts(formulaMergeOutput) ||
            !formulas.Any(static formula =>
                formula.Contains("明细 数据", StringComparison.Ordinal) &&
                !formula.Contains('[', StringComparison.Ordinal)))
        {
            Console.Error.WriteLine(
                "excel-safety-group-copy: visible worksheets did not retain an internal cross-sheet formula.");
            return false;
        }

        var formulaSplitDirectory = Path.Combine(outputRoot, "excel-cross-sheet-split");
        var formulaSplitResult = await client.ExecuteAsync(
            ExcelOperationRequest.Split(formulaInput, formulaSplitDirectory),
            progress);
        var splitFormulas = formulaSplitResult.Artifacts
            .Where(File.Exists)
            .SelectMany(ReadWorksheetFormulas)
            .ToArray();
        if (!IsSuccessfulExcelToolResult(formulaSplitResult, "excel-safety-split") ||
            formulaSplitResult.Artifacts.Count != 3 ||
            !formulaSplitResult.Artifacts.All(File.Exists) ||
            !formulaSplitResult.Notices.Any(static notice =>
                notice.Code == "EXCEL_SPLIT_CROSS_SHEET_LINK_PRESERVED") ||
            !formulaSplitResult.Artifacts.Any(HasExternalLinkParts) ||
            !splitFormulas.Any(static formula => formula.Contains('[', StringComparison.Ordinal)) ||
            !formulaSplitResult.Artifacts.Any(path =>
                HasExternalLinkTarget(path, Path.GetFileName(formulaInput))))
        {
            Console.Error.WriteLine(
                "excel-safety-split: cross-sheet formulas were not preserved as links to the source workbook.");
            return false;
        }

        var riskyCompressionInput = Path.Combine(outputRoot, "excel-compression-risk.xlsx");
        File.Copy(templateInput, riskyCompressionInput);
        AddEmbeddedChart(riskyCompressionInput);
        AddWorkbookDefinedName(riskyCompressionInput);
        var riskyCompressionOutput = Path.Combine(outputRoot, "excel-compression-risk-output.xlsx");
        var riskyCompressionResult = await client.ExecuteAsync(
            ExcelOperationRequest.Compress(riskyCompressionInput, riskyCompressionOutput),
            progress);
        var riskNotice = riskyCompressionResult.Notices.SingleOrDefault(static notice =>
            notice.Code == "EXCEL_COMPRESSION_RISKS_DETECTED");
        var cleanupNotice = riskyCompressionResult.Notices.SingleOrDefault(static notice =>
            notice.Code == "EXCEL_SHEET_CLEANUP_SKIPPED");
        var hasConsistentCleanedCount =
            riskyCompressionResult.Metrics.TryGetValue("sourceContentRetained", out var retained) &&
            riskyCompressionResult.Metrics.TryGetValue("cleanedWorksheetCount", out var cleanedCount) &&
            cleanedCount == (retained == "true" ? "0" : "2");
        if (!IsSuccessfulExcelToolResult(riskyCompressionResult, "excel-safety-compression") ||
            riskNotice is null ||
            !riskNotice.Message.Contains("工作簿级名称", StringComparison.Ordinal) ||
            !riskNotice.Message.Contains("嵌入式图表", StringComparison.Ordinal) ||
            cleanupNotice is null ||
            !cleanupNotice.Message.Contains("总览", StringComparison.Ordinal) ||
            !riskyCompressionResult.Metrics.TryGetValue("attemptedCleanedWorksheetCount", out var attemptedCount) ||
            attemptedCount != "2" ||
            !riskyCompressionResult.Metrics.TryGetValue("skippedWorksheetCount", out var skippedCount) ||
            skippedCount != "1" ||
            !hasConsistentCleanedCount ||
            !HasEmbeddedChartParts(riskyCompressionOutput) ||
            !WorkbookHasDefinedName(riskyCompressionOutput, "DocPivotFarRange"))
        {
            Console.Error.WriteLine(
                "excel-safety-compression: unsafe sheets were not isolated from safe cleanup or key objects were lost.");
            return false;
        }

        Console.WriteLine("excel-safety: date system, formula links, and compression guards passed");
        return true;
    }

    private static async Task<bool> RunExcelPreservationSpikeAsync(
        string outputRoot,
        OfficeWorkerClient client,
        string qaWorkbookPath)
    {
        var sourcePath = Path.Combine(outputRoot, "excel-preservation-source.xlsx");
        var secondSourcePath = Path.Combine(outputRoot, "excel-preservation-source-copy.xlsx");
        File.Copy(qaWorkbookPath, sourcePath);
        File.Copy(qaWorkbookPath, secondSourcePath);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
        var secondSourceHash = SHA256.HashData(File.ReadAllBytes(secondSourcePath));
        using var signatureSession = ExcelApplicationSession.Start(
            Guid.NewGuid(),
            static _ => { },
            static _ => { });
        var sourceSignatures = ReadWorkbookSignatures(sourcePath, signatureSession);
        if (sourceSignatures.Count != 2 ||
            sourceSignatures[0].ChartCount < 1 ||
            sourceSignatures[1].ShapeCount < 1)
        {
            Console.Error.WriteLine("excel-preservation-source: the QA workbook lacks required chart or image coverage.");
            return false;
        }

        var progress = new InlineProgress<WorkerProgressMessage>(message =>
            Console.WriteLine($"excel-preservation: {message.Stage} {message.Current}/{message.Total}"));
        var mergedPath = Path.Combine(outputRoot, "excel-preservation-merged.xlsx");
        var mergeResult = await client.ExecuteAsync(
            ExcelOperationRequest.Merge([sourcePath, secondSourcePath], mergedPath),
            progress);
        if (!IsSuccessfulExcelToolResult(mergeResult, "excel-preservation-merge"))
        {
            return false;
        }

        var mergedSignatures = ReadWorkbookSignatures(mergedPath, signatureSession);
        if (mergedSignatures.Count != 4 ||
            !SignaturesMatch(sourceSignatures[0], mergedSignatures[0], "excel-preservation-merge-sheet-1") ||
            !SignaturesMatch(sourceSignatures[1], mergedSignatures[1], "excel-preservation-merge-sheet-2"))
        {
            return false;
        }

        var splitDirectory = Path.Combine(outputRoot, "excel-preservation-split");
        var splitResult = await client.ExecuteAsync(
            ExcelOperationRequest.Split(mergedPath, splitDirectory),
            progress);
        if (!IsSuccessfulExcelToolResult(splitResult, "excel-preservation-split") ||
            splitResult.Artifacts.Count != 4)
        {
            return false;
        }

        for (var index = 0; index < sourceSignatures.Count; index++)
        {
            var splitSignatures = ReadWorkbookSignatures(
                splitResult.Artifacts[index],
                signatureSession);
            if (splitSignatures.Count != 1 ||
                !SplitSignaturesMatch(
                    sourceSignatures[index],
                    splitSignatures[0],
                    $"excel-preservation-split-sheet-{index + 1}"))
            {
                return false;
            }
        }

        var compressedPath = Path.Combine(outputRoot, "excel-preservation-compressed.xlsx");
        var compressResult = await client.ExecuteAsync(
            ExcelOperationRequest.Compress(sourcePath, compressedPath),
            progress);
        if (!IsSuccessfulExcelToolResult(compressResult, "excel-preservation-compress") ||
            !WorkbookSignaturesMatch(
                sourceSignatures,
                ReadWorkbookSignatures(compressedPath, signatureSession),
                "excel-preservation-compress"))
        {
            return false;
        }

        var balancedPath = Path.Combine(outputRoot, "excel-preservation-balanced.xlsx");
        var balancedOptions = new ExcelOperationOptions(
            IncludeHiddenWorksheets: true,
            PreserveExternalLinks: true,
            SkipUnsafeCompressionSheets: true,
            ConvertLegacyWorkbookToOpenXml: true,
            ImageCompressionLevel: ExcelImageCompressionLevel.Balanced);
        var balancedResult = await client.ExecuteAsync(
            ExcelOperationRequest.Compress(sourcePath, balancedPath, balancedOptions),
            progress);
        if (!IsSuccessfulExcelToolResult(balancedResult, "excel-preservation-balanced") ||
            !balancedResult.Metrics.TryGetValue("compressedImageCount", out var compressedImageCountText) ||
            !int.TryParse(compressedImageCountText, CultureInfo.InvariantCulture, out var compressedImageCount) ||
            compressedImageCount < 1 ||
            GetEmbeddedMediaBytes(balancedPath) >= GetEmbeddedMediaBytes(sourcePath) ||
            !WorkbookSignaturesMatch(
                sourceSignatures,
                ReadWorkbookSignatures(balancedPath, signatureSession),
                "excel-preservation-balanced"))
        {
            Console.Error.WriteLine(
                "excel-preservation-balanced: embedded media did not shrink or workbook objects changed.");
            return false;
        }

        var legacySourcePath = Path.Combine(outputRoot, "excel-preservation-source.xls");
        CreateLegacyWorkbook(sourcePath, legacySourcePath);
        var legacyHash = SHA256.HashData(File.ReadAllBytes(legacySourcePath));
        var legacySignatures = ReadWorkbookSignatures(legacySourcePath, signatureSession);
        var preserveLegacyOptions = balancedOptions with
        {
            ConvertLegacyWorkbookToOpenXml = false,
            ImageCompressionLevel = ExcelImageCompressionLevel.None,
        };
        var preservedLegacyPath = Path.Combine(outputRoot, "excel-preservation-preserved.xls");
        var preservedLegacyResult = await client.ExecuteAsync(
            ExcelOperationRequest.Compress(
                legacySourcePath,
                preservedLegacyPath,
                preserveLegacyOptions),
            progress);
        if (!IsSuccessfulExcelToolResult(preservedLegacyResult, "excel-preservation-xls-preserve") ||
            !WorkbookSignaturesMatch(
                legacySignatures,
                ReadWorkbookSignatures(preservedLegacyPath, signatureSession),
                "excel-preservation-xls-preserve"))
        {
            return false;
        }

        var convertedLegacyPath = Path.Combine(outputRoot, "excel-preservation-converted.xlsx");
        var convertedLegacyResult = await client.ExecuteAsync(
            ExcelOperationRequest.Compress(
                legacySourcePath,
                convertedLegacyPath,
                preserveLegacyOptions with { ConvertLegacyWorkbookToOpenXml = true }),
            progress);
        if (!IsSuccessfulExcelToolResult(convertedLegacyResult, "excel-preservation-xls-convert") ||
            !convertedLegacyResult.Metrics.TryGetValue("convertedLegacyWorkbook", out var converted) ||
            converted != "true" ||
            !WorkbookSignaturesMatch(
                legacySignatures,
                ReadWorkbookSignatures(convertedLegacyPath, signatureSession),
                "excel-preservation-xls-convert"))
        {
            return false;
        }

        if (!sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))) ||
            !secondSourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(secondSourcePath))) ||
            !legacyHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(legacySourcePath))))
        {
            Console.Error.WriteLine("excel-preservation-source: a QA source workbook was modified.");
            return false;
        }

        Console.WriteLine(
            "excel-preservation: formulas, styles, dimensions, charts, images, image compression, and XLS paths passed");
        return true;
    }

    private static List<ExcelWorksheetSignature> ReadWorkbookSignatures(
        string path,
        ExcelApplicationSession session)
    {
        object? workbookValue = null;
        dynamic? worksheets = null;
        try
        {
            workbookValue = session.OpenWorkbook(path, readOnly: true);
            dynamic workbook = workbookValue;
            worksheets = workbook.Worksheets;
            var signatures = new List<ExcelWorksheetSignature>((int)worksheets.Count);
            for (var index = 1; index <= (int)worksheets.Count; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[index];
                    signatures.Add(ReadWorksheetSignature(worksheet));
                }
                finally
                {
                    FinalReleaseComObject(worksheet);
                }
            }

            return signatures;
        }
        finally
        {
            FinalReleaseComObject(worksheets);
            session.CloseWorkbook(workbookValue);
        }
    }

    private static ExcelWorksheetSignature ReadWorksheetSignature(dynamic worksheet)
    {
        dynamic? usedRange = null;
        dynamic? usedRows = null;
        dynamic? usedColumns = null;
        dynamic? cells = null;
        dynamic? chartObjects = null;
        dynamic? shapes = null;
        try
        {
            usedRange = worksheet.UsedRange;
            usedRows = usedRange.Rows;
            usedColumns = usedRange.Columns;
            var rowCount = (int)usedRows.Count;
            var columnCount = (int)usedColumns.Count;
            if ((long)rowCount * columnCount > 2_000)
            {
                throw new InvalidDataException("Excel QA used range is unexpectedly large.");
            }

            cells = usedRange.Cells;
            var cellSignatures = new List<string>(rowCount * columnCount);
            var mergeAreas = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 1; row <= rowCount; row++)
            {
                for (var column = 1; column <= columnCount; column++)
                {
                    dynamic? cell = null;
                    dynamic? interior = null;
                    dynamic? font = null;
                    dynamic? mergeArea = null;
                    try
                    {
                        cell = cells[row, column];
                        interior = cell.Interior;
                        font = cell.Font;
                        var isMerged = cell.MergeCells is bool merged && merged;
                        if (isMerged)
                        {
                            mergeArea = cell.MergeArea;
                            mergeAreas.Add((string)mergeArea.Address);
                        }

                        cellSignatures.Add(string.Join(
                            '\u001F',
                            (string)cell.Address,
                            ToInvariantString(cell.Formula),
                            ToInvariantString(cell.Value2),
                            ToInvariantString(cell.NumberFormat),
                            ToInvariantString(interior.Color),
                            ToInvariantString(font.Color),
                            ToInvariantString(font.Bold),
                            ToInvariantString(font.Size),
                            ToInvariantString(cell.HorizontalAlignment),
                            ToInvariantString(cell.VerticalAlignment),
                            ReadBorderSignature(cell)));
                    }
                    finally
                    {
                        FinalReleaseComObject(mergeArea);
                        FinalReleaseComObject(font);
                        FinalReleaseComObject(interior);
                        FinalReleaseComObject(cell);
                    }
                }
            }

            var rowHeights = new List<string>(rowCount);
            for (var index = 1; index <= rowCount; index++)
            {
                dynamic? row = null;
                try
                {
                    row = usedRows[index];
                    rowHeights.Add(ToInvariantString(row.RowHeight));
                }
                finally
                {
                    FinalReleaseComObject(row);
                }
            }

            var columnWidths = new List<string>(columnCount);
            for (var index = 1; index <= columnCount; index++)
            {
                dynamic? column = null;
                try
                {
                    column = usedColumns[index];
                    columnWidths.Add(ToInvariantString(column.ColumnWidth));
                }
                finally
                {
                    FinalReleaseComObject(column);
                }
            }

            chartObjects = worksheet.ChartObjects();
            var chartTitles = new List<string>((int)chartObjects.Count);
            for (var index = 1; index <= (int)chartObjects.Count; index++)
            {
                dynamic? chartObject = null;
                dynamic? chart = null;
                dynamic? chartTitle = null;
                try
                {
                    chartObject = chartObjects[index];
                    chart = chartObject.Chart;
                    if ((bool)chart.HasTitle)
                    {
                        chartTitle = chart.ChartTitle;
                        chartTitles.Add((string)chartTitle.Text);
                    }
                    else
                    {
                        chartTitles.Add(string.Empty);
                    }
                }
                finally
                {
                    FinalReleaseComObject(chartTitle);
                    FinalReleaseComObject(chart);
                    FinalReleaseComObject(chartObject);
                }
            }

            shapes = worksheet.Shapes;
            return new ExcelWorksheetSignature(
                (string)worksheet.Name,
                (int)worksheet.Visible,
                (string)usedRange.Address,
                cellSignatures,
                rowHeights,
                columnWidths,
                mergeAreas.Order(StringComparer.Ordinal).ToArray(),
                chartTitles,
                (int)shapes.Count);
        }
        finally
        {
            FinalReleaseComObject(shapes);
            FinalReleaseComObject(chartObjects);
            FinalReleaseComObject(cells);
            FinalReleaseComObject(usedColumns);
            FinalReleaseComObject(usedRows);
            FinalReleaseComObject(usedRange);
        }
    }

    private static string ReadBorderSignature(dynamic cell)
    {
        dynamic? borders = null;
        try
        {
            borders = cell.Borders;
            var signatures = new List<string>(4);
            foreach (var edge in new[] { 7, 8, 9, 10 })
            {
                dynamic? border = null;
                try
                {
                    border = borders[edge];
                    signatures.Add($"{ToInvariantString(border.LineStyle)}:{ToInvariantString(border.Weight)}");
                }
                finally
                {
                    FinalReleaseComObject(border);
                }
            }

            return string.Join(',', signatures);
        }
        finally
        {
            FinalReleaseComObject(borders);
        }
    }

    private static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool WorkbookSignaturesMatch(
        IReadOnlyList<ExcelWorksheetSignature> expected,
        IReadOnlyList<ExcelWorksheetSignature> actual,
        string caseName)
    {
        if (expected.Count != actual.Count)
        {
            Console.Error.WriteLine($"{caseName}: worksheet count changed.");
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!SignaturesMatch(expected[index], actual[index], $"{caseName}-sheet-{index + 1}"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SignaturesMatch(
        ExcelWorksheetSignature expected,
        ExcelWorksheetSignature actual,
        string caseName)
    {
        var matches = expected.Name == actual.Name &&
            expected.Visibility == actual.Visibility &&
            expected.UsedRange == actual.UsedRange &&
            expected.CellSignatures.SequenceEqual(actual.CellSignatures, StringComparer.Ordinal) &&
            expected.RowHeights.SequenceEqual(actual.RowHeights, StringComparer.Ordinal) &&
            expected.ColumnWidths.SequenceEqual(actual.ColumnWidths, StringComparer.Ordinal) &&
            expected.MergeAreas.SequenceEqual(actual.MergeAreas, StringComparer.Ordinal) &&
            expected.ChartTitles.SequenceEqual(actual.ChartTitles, StringComparer.Ordinal) &&
            expected.ShapeCount == actual.ShapeCount;
        if (!matches)
        {
            Console.Error.WriteLine($"{caseName}: formulas, styles, dimensions, merges, charts, or shapes changed.");
        }

        return matches;
    }

    private static bool SplitSignaturesMatch(
        ExcelWorksheetSignature expected,
        ExcelWorksheetSignature actual,
        string caseName)
    {
        var matches = expected.Name == actual.Name &&
            expected.Visibility == actual.Visibility &&
            expected.UsedRange == actual.UsedRange &&
            SplitCellSignaturesMatch(expected.CellSignatures, actual.CellSignatures) &&
            expected.RowHeights.SequenceEqual(actual.RowHeights, StringComparer.Ordinal) &&
            expected.ColumnWidths.SequenceEqual(actual.ColumnWidths, StringComparer.Ordinal) &&
            expected.MergeAreas.SequenceEqual(actual.MergeAreas, StringComparer.Ordinal) &&
            expected.ChartTitles.SequenceEqual(actual.ChartTitles, StringComparer.Ordinal) &&
            expected.ShapeCount == actual.ShapeCount;
        if (!matches)
        {
            Console.Error.WriteLine(
                $"{caseName}: split output changed values, styles, dimensions, merges, charts, shapes, or formula targets.");
        }

        return matches;
    }

    private static bool SplitCellSignaturesMatch(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var expectedParts = expected[index].Split('\u001F');
            var actualParts = actual[index].Split('\u001F');
            if (expectedParts.Length != actualParts.Length || expectedParts.Length < 2)
            {
                return false;
            }

            for (var part = 0; part < expectedParts.Length; part++)
            {
                if (part == 1)
                {
                    continue;
                }

                if (!string.Equals(expectedParts[part], actualParts[part], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!SplitFormulaMatches(expectedParts[1], actualParts[1]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SplitFormulaMatches(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = expected.IndexOf('!');
        if (separator < 0 || !actual.Contains('[', StringComparison.Ordinal))
        {
            return false;
        }

        var sheetName = expected[..separator]
            .TrimStart('=')
            .Trim('\'');
        return actual.Contains(sheetName, StringComparison.Ordinal) &&
            actual.EndsWith(expected[separator..], StringComparison.Ordinal);
    }

    private static long GetEmbeddedMediaBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries
            .Where(static entry => entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase))
            .Sum(static entry => entry.Length);
    }

    private static void CreateLegacyWorkbook(string sourcePath, string destinationPath)
    {
        dynamic? application = null;
        dynamic? workbooks = null;
        dynamic? workbook = null;
        try
        {
            var applicationType = Type.GetTypeFromProgID("Excel.Application")
                ?? throw new InvalidOperationException("Microsoft Excel is not available.");
            application = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("Microsoft Excel could not be started.");
            application.Visible = false;
            application.DisplayAlerts = false;
            application.AutomationSecurity = 3;
            application.AskToUpdateLinks = false;
            application.EnableEvents = false;
            workbooks = application.Workbooks;
            workbook = workbooks.Open(
                Filename: sourcePath,
                UpdateLinks: 0,
                IgnoreReadOnlyRecommended: true,
                AddToMru: false,
                Local: true);
            workbook.SaveAs(
                Filename: destinationPath,
                FileFormat: 56,
                CreateBackup: false,
                AddToMru: false,
                Local: true);
        }
        finally
        {
            try
            {
                workbook?.Close(SaveChanges: false);
            }
            finally
            {
                try
                {
                    application?.Quit();
                }
                finally
                {
                    FinalReleaseComObject(workbook);
                    FinalReleaseComObject(workbooks);
                    FinalReleaseComObject(application);
                }
            }
        }
    }

    private static bool IsSuccessfulExcelToolResult(
        DocPivot.Infrastructure.Operations.OperationExecutionResult result,
        string caseName)
    {
        if (result.IsSucceeded && result.Artifacts.All(File.Exists))
        {
            return true;
        }

        Console.Error.WriteLine($"{caseName}: {result.ErrorCode} {result.ErrorMessage}");
        return false;
    }

    private static int CountPdfPageObjects(ReadOnlySpan<byte> pdf)
    {
        ReadOnlySpan<byte> typeToken = "/Type"u8;
        ReadOnlySpan<byte> pageToken = "/Page"u8;
        var pageCount = 0;
        var searchOffset = 0;

        while (searchOffset < pdf.Length)
        {
            var relativeTypeIndex = pdf[searchOffset..].IndexOf(typeToken);
            if (relativeTypeIndex < 0)
            {
                break;
            }

            var pageTokenStart = searchOffset + relativeTypeIndex + typeToken.Length;
            while (pageTokenStart < pdf.Length && IsPdfWhitespace(pdf[pageTokenStart]))
            {
                pageTokenStart++;
            }

            var pageTokenEnd = pageTokenStart + pageToken.Length;
            if (pageTokenEnd <= pdf.Length &&
                pdf[pageTokenStart..pageTokenEnd].SequenceEqual(pageToken) &&
                (pageTokenEnd == pdf.Length || IsPdfDelimiter(pdf[pageTokenEnd])))
            {
                pageCount++;
            }

            searchOffset = pageTokenStart < pdf.Length
                ? pageTokenStart + 1
                : pdf.Length;
        }

        return pageCount;
    }

    private static bool IsPdfWhitespace(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsPdfDelimiter(byte value) =>
        IsPdfWhitespace(value) ||
        value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
            (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or
            (byte)'/' or (byte)'%';

    private static void CreateOpenXmlPackage(string sourceDirectory, string destinationPath)
    {
        using var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void SetWorkbookDateSystemFlag(string path, bool uses1904DateSystem) =>
        UpdateOpenXmlPart(
            path,
            "xl/workbook.xml",
            document =>
            {
                var root = document.Root ?? throw new InvalidDataException("Workbook XML has no root element.");
                var ns = root.Name.Namespace;
                var workbookProperties = root.Element(ns + "workbookPr");
                if (workbookProperties is null)
                {
                    workbookProperties = new XElement(ns + "workbookPr");
                    root.AddFirst(workbookProperties);
                }

                workbookProperties.SetAttributeValue("date1904", uses1904DateSystem ? "1" : "0");
            });

    private static bool WorkbookUses1904DateSystem(string path)
    {
        var document = ReadOpenXmlPart(path, "xl/workbook.xml");
        var root = document.Root ?? throw new InvalidDataException("Workbook XML has no root element.");
        var value = (string?)root.Element(root.Name.Namespace + "workbookPr")?.Attribute("date1904");
        return value is "1" or "true";
    }

    private static void AddCrossSheetFormula(string path) =>
        UpdateOpenXmlPart(
            path,
            "xl/worksheets/sheet1.xml",
            document =>
            {
                var root = document.Root ?? throw new InvalidDataException("Worksheet XML has no root element.");
                var ns = root.Name.Namespace;
                var row = root
                    .Element(ns + "sheetData")?
                    .Elements(ns + "row")
                    .Single(static element => (string?)element.Attribute("r") == "1")
                    ?? throw new InvalidDataException("Worksheet XML has no first row.");
                row.Add(
                    new XElement(
                        ns + "c",
                        new XAttribute("r", "C1"),
                        new XElement(ns + "f", "'明细 数据'!B1"),
                        new XElement(ns + "v", "202")));
            });

    private static void AddWorkbookDefinedName(string path) =>
        UpdateOpenXmlPart(
            path,
            "xl/workbook.xml",
            document =>
            {
                var root = document.Root ?? throw new InvalidDataException("Workbook XML has no root element.");
                var ns = root.Name.Namespace;
                var definedNames = root.Element(ns + "definedNames");
                if (definedNames is null)
                {
                    definedNames = new XElement(ns + "definedNames");
                    root.Element(ns + "sheets")?.AddAfterSelf(definedNames);
                }

                definedNames.Add(
                    new XElement(
                        ns + "definedName",
                        new XAttribute("name", "DocPivotFarRange"),
                        "'总览'!$XFD$1048576"));
            });

    private static List<string> ReadWorksheetFormulas(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var formulas = new List<string>();
        foreach (var entry in archive.Entries.Where(static entry =>
                     entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".xml", StringComparison.Ordinal)))
        {
            using var entryStream = entry.Open();
            var document = XDocument.Load(entryStream);
            if (document.Root is not null)
            {
                formulas.AddRange(document.Descendants(document.Root.Name.Namespace + "f").Select(static formula => formula.Value));
            }
        }

        return formulas;
    }

    private static bool HasExternalLinkParts(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries.Any(static entry =>
            entry.FullName.StartsWith("xl/externalLinks/", StringComparison.Ordinal));
    }

    private static bool HasExternalLinkTarget(string path, string expectedFileName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries.Where(static entry =>
                     entry.FullName.StartsWith("xl/externalLinks/_rels/", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var entryStream = entry.Open();
            var document = XDocument.Load(entryStream);
            if (document.Descendants().Any(element =>
                    string.Equals(
                        Path.GetFileName((string?)element.Attribute("Target")),
                        expectedFileName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEmbeddedChartParts(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries.Any(static entry =>
            entry.FullName.StartsWith("xl/charts/", StringComparison.Ordinal) &&
            entry.FullName.EndsWith(".xml", StringComparison.Ordinal));
    }

    private static bool WorkbookHasDefinedName(string path, string name)
    {
        var document = ReadOpenXmlPart(path, "xl/workbook.xml");
        var root = document.Root ?? throw new InvalidDataException("Workbook XML has no root element.");
        return root
            .Descendants(root.Name.Namespace + "definedName")
            .Any(element => string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal));
    }

    private static XDocument ReadOpenXmlPart(string path, string partName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.GetEntry(partName)
            ?? throw new InvalidDataException($"Open XML part is missing: {partName}");
        using var entryStream = entry.Open();
        return XDocument.Load(entryStream);
    }

    private static void UpdateOpenXmlPart(
        string path,
        string partName,
        Action<XDocument> update)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        var entry = archive.GetEntry(partName)
            ?? throw new InvalidDataException($"Open XML part is missing: {partName}");
        XDocument document;
        using (var entryStream = entry.Open())
        {
            document = XDocument.Load(entryStream);
        }

        update(document);
        entry.Delete();
        var replacement = archive.CreateEntry(partName, CompressionLevel.Optimal);
        using var replacementStream = replacement.Open();
        document.Save(replacementStream, SaveOptions.DisableFormatting);
    }

    private static void AddEmbeddedChart(string path)
    {
        dynamic? application = null;
        dynamic? workbooks = null;
        dynamic? workbook = null;
        dynamic? worksheets = null;
        dynamic? worksheet = null;
        dynamic? chartObjects = null;
        dynamic? chartObject = null;
        dynamic? chart = null;
        dynamic? sourceRange = null;
        try
        {
            var applicationType = Type.GetTypeFromProgID("Excel.Application")
                ?? throw new InvalidOperationException("Microsoft Excel is not available.");
            application = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("Microsoft Excel could not be started.");
            application.Visible = false;
            application.DisplayAlerts = false;
            application.AutomationSecurity = 3;
            application.EnableEvents = false;
            workbooks = application.Workbooks;
            workbook = workbooks.Open(
                Filename: path,
                UpdateLinks: 0,
                ReadOnly: false,
                IgnoreReadOnlyRecommended: true,
                AddToMru: false,
                Local: true);
            worksheets = workbook.Worksheets;
            worksheet = worksheets[1];
            chartObjects = worksheet.ChartObjects();
            chartObject = chartObjects.Add(180, 16, 240, 140);
            chart = chartObject.Chart;
            sourceRange = worksheet.Range["A1", "B1"];
            chart.SetSourceData(Source: sourceRange);
            workbook.Save();
        }
        finally
        {
            try
            {
                workbook?.Close(SaveChanges: false);
            }
            finally
            {
                try
                {
                    application?.Quit();
                }
                finally
                {
                    FinalReleaseComObject(sourceRange);
                    FinalReleaseComObject(chart);
                    FinalReleaseComObject(chartObject);
                    FinalReleaseComObject(chartObjects);
                    FinalReleaseComObject(worksheet);
                    FinalReleaseComObject(worksheets);
                    FinalReleaseComObject(workbook);
                    FinalReleaseComObject(workbooks);
                    FinalReleaseComObject(application);
                }
            }
        }
    }

    private static void FinalReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void PrepareOutputDirectory(string root, string outputRoot)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedOutput = Path.GetFullPath(outputRoot);
        if (!normalizedOutput.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean an Office spike directory outside the repository.");
        }

        if (Directory.Exists(normalizedOutput))
        {
            Directory.Delete(normalizedOutput, recursive: true);
        }

        Directory.CreateDirectory(normalizedOutput);
    }

    private static string FindRepositoryRoot()
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

    private sealed record SpikeCase(string Name, string Extension, string SourceDirectory);

    private sealed record ExcelWorksheetSignature(
        string Name,
        int Visibility,
        string UsedRange,
        IReadOnlyList<string> CellSignatures,
        IReadOnlyList<string> RowHeights,
        IReadOnlyList<string> ColumnWidths,
        IReadOnlyList<string> MergeAreas,
        IReadOnlyList<string> ChartTitles,
        int ShapeCount)
    {
        public int ChartCount => ChartTitles.Count;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
