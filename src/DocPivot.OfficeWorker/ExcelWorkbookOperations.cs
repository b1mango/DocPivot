using System.Globalization;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Excel;
using DocPivot.Infrastructure.Storage;

namespace DocPivot.OfficeWorker;

internal static class ExcelWorkbookOperations
{
    private const int ExcelOpenXmlWorkbook = 51;
    private const int ExcelSheetVisible = -1;

    public static ExcelToolExecutionResult Merge(
        ExcelApplicationSession session,
        IReadOnlyList<string> inputPaths,
        string outputPath,
        WorkerExcelToolOptions options,
        Guid jobId,
        Action<string> reportStage)
    {
        var notices = new List<WorkerNotice>();
        var sourceSheetNames = new List<string>();
        var sourceSheetVisibilities = new List<bool>();
        object? destinationWorkbook = null;
        string? stagingPath = null;
        bool? uses1904DateSystem = null;
        var externalLinkCount = 0;
        var excludedHiddenCount = 0;

        try
        {
            foreach (var inputPath in inputPaths)
            {
                object? sourceWorkbook = null;
                try
                {
                    sourceWorkbook = session.OpenWorkbook(inputPath, readOnly: true);
                    reportStage("workbook-opened");
                    var facts = ExcelWorkbookInspector.Inspect(sourceWorkbook);
                    EnsureSupportedWorkbook(facts, options, allowChartSheets: false);
                    if (uses1904DateSystem is not null &&
                        uses1904DateSystem.Value != facts.Uses1904DateSystem)
                    {
                        throw new OfficeWorkerException(
                            "EXCEL_DATE_SYSTEM_MISMATCH",
                            "Workbooks using different 1900/1904 date systems cannot be merged safely.");
                    }

                    if (uses1904DateSystem is null)
                    {
                        uses1904DateSystem = facts.Uses1904DateSystem;
                    }

                    var selectedWorksheets = SelectWorksheets(facts, options.IncludeHiddenWorksheets);
                    excludedHiddenCount += facts.Worksheets.Count - selectedWorksheets.Length;
                    reportStage("excel-copying-worksheets");
                    if (destinationWorkbook is null)
                    {
                        destinationWorkbook = CopyWorksheetsToNewWorkbook(
                            session,
                            sourceWorkbook,
                            selectedWorksheets);
                        SetWorkbookDateSystem(destinationWorkbook, uses1904DateSystem.Value);
                    }
                    else
                    {
                        CopyWorksheets(
                            sourceWorkbook,
                            destinationWorkbook,
                            selectedWorksheets);
                    }

                    reportStage("excel-worksheets-copied");
                    sourceSheetNames.AddRange(selectedWorksheets.Select(static worksheet => worksheet.Name));
                    sourceSheetVisibilities.AddRange(
                        selectedWorksheets.Select(static worksheet => worksheet.IsVisible));
                }
                finally
                {
                    session.CloseWorkbook(sourceWorkbook);
                }
            }

            if (sourceSheetNames.Count == 0)
            {
                throw new OfficeWorkerException(
                    "EXCEL_WORKSHEET_REQUIRED",
                    "No supported Excel worksheets were available to merge.");
            }

            if (destinationWorkbook is null)
            {
                throw new OfficeWorkerException(
                    "EXCEL_WORKBOOK_CREATE_FAILED",
                    "Excel did not create the merged workbook.",
                    true);
            }

            var allocatedNames = ExcelWorksheetNameAllocator.Allocate(sourceSheetNames);
            reportStage("excel-renaming-worksheets");
            RenameWorksheets(destinationWorkbook, allocatedNames);
            SetWorksheetVisibilities(destinationWorkbook, sourceSheetVisibilities);
            reportStage("excel-rebinding-chart-series");
            RebindChartSeries(destinationWorkbook);
            stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, jobId);
            reportStage("excel-saving-workbook");
            SaveAsOpenXml(destinationWorkbook, stagingPath);
            session.CloseWorkbook(destinationWorkbook);
            destinationWorkbook = null;
            reportStage("excel-verifying-workbook");
            var verifiedFacts = VerifyWorkbook(
                session,
                stagingPath,
                allocatedNames,
                uses1904DateSystem!.Value);
            externalLinkCount = verifiedFacts.ExternalLinkCount;
            if (!options.PreserveExternalLinks && externalLinkCount > 0)
            {
                throw new OfficeWorkerException(
                    "EXCEL_EXTERNAL_LINKS_INTRODUCED",
                    "Copying the selected worksheets would introduce external workbook links.");
            }

            AtomicOutputFile.Commit(stagingPath, outputPath);
            stagingPath = null;

            AddCompatibilityNotices(notices, externalLinkCount, excludedHiddenCount);
            return new ExcelToolExecutionResult(
                [outputPath],
                notices,
                new Dictionary<string, string>
                {
                    ["inputCount"] = inputPaths.Count.ToString(CultureInfo.InvariantCulture),
                    ["worksheetCount"] = allocatedNames.Count.ToString(CultureInfo.InvariantCulture),
                    ["externalLinkCount"] = externalLinkCount.ToString(CultureInfo.InvariantCulture),
                });
        }
        finally
        {
            session.CloseWorkbook(destinationWorkbook);
            DeleteIfExists(stagingPath);
        }
    }

    public static ExcelToolExecutionResult Split(
        ExcelApplicationSession session,
        string inputPath,
        string outputDirectory,
        WorkerExcelToolOptions options,
        Guid jobId,
        Action<string> reportStage)
    {
        var notices = new List<WorkerNotice>();
        var stagedOutputs = new List<(string StagingPath, string DestinationPath)>();
        object? sourceWorkbook = null;
        try
        {
            sourceWorkbook = session.OpenWorkbook(inputPath, readOnly: true);
            reportStage("workbook-opened");
            var isolatedSourcePath = (string)((dynamic)sourceWorkbook).FullName;
            var facts = ExcelWorkbookInspector.Inspect(sourceWorkbook);
            EnsureSupportedWorkbook(facts, options, allowChartSheets: false);
            var selectedWorksheets = SelectWorksheets(facts, options.IncludeHiddenWorksheets);
            var proposedFileNames = WindowsOutputNamePolicy.Allocate(
                selectedWorksheets.Select(static worksheet => worksheet.Name));
            var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < selectedWorksheets.Length; index++)
            {
                var worksheet = selectedWorksheets[index];
                var destinationPath = AllocateDestinationPath(
                    outputDirectory,
                    proposedFileNames[index],
                    reservedDestinations);
                var stagingPath = AtomicOutputFile.CreateStagingPath(destinationPath, jobId);
                stagedOutputs.Add((stagingPath, destinationPath));

                object? splitWorkbook = null;
                try
                {
                    if (!worksheet.IsVisible)
                    {
                        SetWorksheetVisible(sourceWorkbook, worksheet.Position);
                    }

                    reportStage("excel-copying-split-worksheet");
                    splitWorkbook = CopySingleWorksheetToNewWorkbook(
                        session,
                        sourceWorkbook,
                        worksheet.Position);
                    reportStage("excel-split-workbook-created");
                    SetWorkbookDateSystem(splitWorkbook, facts.Uses1904DateSystem);
                    SetWorksheetVisible(splitWorkbook, 1);
                    var outputSheetName = ExcelWorksheetNameAllocator.Sanitize(worksheet.Name);
                    RenameWorksheets(splitWorkbook, [outputSheetName]);
                    reportStage("excel-rebinding-chart-series");
                    RebindChartSeries(splitWorkbook);
                    RebindSplitSourceLink(splitWorkbook, isolatedSourcePath, inputPath);
                    SaveAsOpenXml(splitWorkbook, stagingPath);
                    session.CloseWorkbook(splitWorkbook);
                    splitWorkbook = null;
                    var allowedExternalLinks = facts.ExternalLinkSources
                        .Append(inputPath)
                        .ToArray();
                    var verifiedFacts = VerifyWorkbook(
                        session,
                        stagingPath,
                        [outputSheetName],
                        facts.Uses1904DateSystem,
                        allowedExternalLinks);
                    if (verifiedFacts.ExternalLinkCount > facts.ExternalLinkCount)
                    {
                        notices.Add(new WorkerNotice(
                            "EXCEL_SPLIT_CROSS_SHEET_LINK_PRESERVED",
                            $"工作表“{worksheet.Name}”中的跨表公式已改为指向原工作簿的外部链接。",
                            "warning"));
                    }
                }
                finally
                {
                    session.CloseWorkbook(splitWorkbook);
                }

                if (!worksheet.IsVisible)
                {
                    notices.Add(new WorkerNotice(
                        "EXCEL_HIDDEN_SHEET_EXPOSED",
                        $"隐藏工作表“{worksheet.Name}”已在独立输出中设为可见。",
                        "warning"));
                }
            }

            var artifacts = AtomicOutputBatch.Commit(stagedOutputs);
            stagedOutputs.Clear();
            AddCompatibilityNotices(notices, facts.ExternalLinkCount, excludedHiddenCount: 0);
            return new ExcelToolExecutionResult(
                artifacts,
                notices,
                new Dictionary<string, string>
                {
                    ["worksheetCount"] = artifacts.Count.ToString(CultureInfo.InvariantCulture),
                    ["externalLinkCount"] = facts.ExternalLinkCount.ToString(CultureInfo.InvariantCulture),
                });
        }
        finally
        {
            session.CloseWorkbook(sourceWorkbook);
            foreach (var stagedOutput in stagedOutputs)
            {
                DeleteIfExists(stagedOutput.StagingPath);
            }
        }
    }

    public static ExcelToolExecutionResult Compress(
        ExcelApplicationSession session,
        string inputPath,
        string outputPath,
        WorkerExcelToolOptions options,
        Guid jobId,
        Action<string> reportStage)
    {
        var notices = new List<WorkerNotice>();
        var stagingPath = AtomicOutputFile.CreateStagingPath(outputPath, jobId);
        string? legacyCopyPath = null;
        object? sourceWorkbook = null;
        object? workingWorkbook = null;
        try
        {
            sourceWorkbook = session.OpenWorkbook(inputPath, readOnly: true);
            reportStage("workbook-opened");
            var sourceFacts = ExcelWorkbookInspector.Inspect(sourceWorkbook);
            EnsureSupportedWorkbook(sourceFacts, options, allowChartSheets: true);
            reportStage("excel-source-inspected");
            session.CloseWorkbook(sourceWorkbook);
            sourceWorkbook = null;

            var isLegacyInput = string.Equals(
                Path.GetExtension(inputPath),
                ".xls",
                StringComparison.OrdinalIgnoreCase);
            var convertsLegacyWorkbook = isLegacyInput && options.ConvertLegacyWorkbookToOpenXml;
            if (!convertsLegacyWorkbook)
            {
                File.Copy(inputPath, stagingPath);
                reportStage("excel-staging-created");
            }
            else
            {
                legacyCopyPath = CreateLegacyCopyPath(outputPath, jobId);
                File.Copy(inputPath, legacyCopyPath);
                object? legacyWorkbook = null;
                try
                {
                    legacyWorkbook = session.OpenWorkbook(legacyCopyPath, readOnly: false);
                    SaveAsOpenXml(legacyWorkbook, stagingPath);
                    reportStage("excel-staging-created");
                }
                finally
                {
                    session.CloseWorkbook(legacyWorkbook);
                    DeleteIfExists(legacyCopyPath);
                    legacyCopyPath = null;
                }
            }

            var cleanedCount = 0;
            var skippedCount = 0;
            var preservesEmbeddedMedia = ExcelEmbeddedImageCompressor.HasEmbeddedMedia(stagingPath);
            if (!preservesEmbeddedMedia)
            {
                workingWorkbook = session.OpenWorkbook(stagingPath, readOnly: false);
                reportStage("excel-working-copy-opened");
                var compressionRisks = ExcelCompressionRiskInspector.Inspect(workingWorkbook);
            reportStage("excel-risks-inspected");
            if (compressionRisks.Count > 0)
            {
                var riskSummary = string.Join("、", compressionRisks);
                notices.Add(new WorkerNotice(
                    "EXCEL_COMPRESSION_RISKS_DETECTED",
                    $"检测到{riskSummary}，将逐表检查引用边界，风险工作表保持原样。",
                    "warning"));
            }

            reportStage("excel-cleaning-worksheets");
            CleanWorksheets(workingWorkbook, options, notices, ref cleanedCount, ref skippedCount);
            reportStage("excel-worksheets-cleaned");

            ((dynamic)workingWorkbook).Save();
            reportStage("excel-working-copy-saved");

                session.CloseWorkbook(workingWorkbook);
                workingWorkbook = null;
            }
            else
            {
                notices.Add(new WorkerNotice(
                    "EXCEL_EMBEDDED_MEDIA_PRESERVED",
                    "已跳过工作簿重写，以保留嵌入图片和绘图关系。",
                    "info"));
                reportStage("excel-embedded-media-preserved");
            }

            var imageCompression = ExcelEmbeddedImageCompressor.Compress(
                stagingPath,
                options.ImageCompressionLevel);
            if (options.ImageCompressionLevel != ExcelImageCompressionLevel.None &&
                !string.Equals(Path.GetExtension(stagingPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                notices.Add(new WorkerNotice(
                    "EXCEL_IMAGE_COMPRESSION_REQUIRES_XLSX",
                    "图片重采样仅适用于 XLSX；当前保留 XLS 格式，图片未处理。",
                    "warning"));
            }
            else if (imageCompression.CompressedImageCount > 0)
            {
                notices.Add(new WorkerNotice(
                    "EXCEL_IMAGES_COMPRESSED",
                    $"已重采样 {imageCompression.CompressedImageCount} 张嵌入图片。",
                    "info"));
            }

            if (ExcelOpenXmlPackageOptimizer.TryOptimize(stagingPath))
            {
                notices.Add(new WorkerNotice(
                    "EXCEL_PACKAGE_RECOMPRESSED",
                    "已重新压缩 XLSX 内部数据包。",
                    "info"));
            }

            VerifyWorkbook(
                session,
                stagingPath,
                sourceFacts.Worksheets.Select(static worksheet => worksheet.Name).ToArray(),
                sourceFacts.Uses1904DateSystem,
                sourceFacts.ExternalLinkSources,
                requireAllExternalLinks: true,
                expectedChartSheetCount: sourceFacts.ChartSheetCount);

            var bytesBefore = new FileInfo(inputPath).Length;
            var bytesAfter = new FileInfo(stagingPath).Length;
            var attemptedCleanedCount = cleanedCount;
            var actualCompressedImageCount = imageCompression.CompressedImageCount;
            var actualImageBytesAfter = imageCompression.BytesAfter;
            var sourceContentRetained = false;
            if (bytesAfter >= bytesBefore && !convertsLegacyWorkbook)
            {
                File.Delete(stagingPath);
                File.Copy(inputPath, stagingPath);
                bytesAfter = bytesBefore;
                cleanedCount = 0;
                actualCompressedImageCount = 0;
                actualImageBytesAfter = imageCompression.BytesBefore;
                sourceContentRetained = true;
                notices.Add(new WorkerNotice(
                    "EXCEL_NO_COMPRESSION_BENEFIT",
                    "未获得可用的压缩收益，输出保留源工作簿内容。",
                    "info"));
            }
            else if (bytesAfter >= bytesBefore)
            {
                notices.Add(new WorkerNotice(
                    "EXCEL_NO_COMPRESSION_BENEFIT",
                    "转换为 .xlsx 后未获得体积收益，但已保留新的兼容格式输出。",
                    "info"));
            }

            AtomicOutputFile.Commit(stagingPath, outputPath);
            AddCompatibilityNotices(notices, sourceFacts.ExternalLinkCount, excludedHiddenCount: 0);
            return new ExcelToolExecutionResult(
                [outputPath],
                notices,
                new Dictionary<string, string>
                {
                    ["bytesBefore"] = bytesBefore.ToString(CultureInfo.InvariantCulture),
                    ["bytesAfter"] = bytesAfter.ToString(CultureInfo.InvariantCulture),
                    ["cleanedWorksheetCount"] = cleanedCount.ToString(CultureInfo.InvariantCulture),
                    ["attemptedCleanedWorksheetCount"] = attemptedCleanedCount.ToString(CultureInfo.InvariantCulture),
                    ["skippedWorksheetCount"] = skippedCount.ToString(CultureInfo.InvariantCulture),
                    ["imageCount"] = imageCompression.ImageCount.ToString(CultureInfo.InvariantCulture),
                    ["compressedImageCount"] = actualCompressedImageCount.ToString(CultureInfo.InvariantCulture),
                    ["imageBytesBefore"] = imageCompression.BytesBefore.ToString(CultureInfo.InvariantCulture),
                    ["imageBytesAfter"] = actualImageBytesAfter.ToString(CultureInfo.InvariantCulture),
                    ["sourceContentRetained"] = sourceContentRetained ? "true" : "false",
                    ["convertedLegacyWorkbook"] = convertsLegacyWorkbook ? "true" : "false",
                });
        }
        finally
        {
            session.CloseWorkbook(workingWorkbook);
            session.CloseWorkbook(sourceWorkbook);
            DeleteIfExists(legacyCopyPath);
            DeleteIfExists(stagingPath);
        }
    }

    private static void CleanWorksheets(
        object workbookValue,
        WorkerExcelToolOptions options,
        List<WorkerNotice> notices,
        ref int cleanedCount,
        ref int skippedCount)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            for (var index = 1; index <= (int)worksheets.Count; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[index];
                    var isVisible = (int)worksheet.Visible == ExcelSheetVisible;
                    if (!options.IncludeHiddenWorksheets && !isVisible)
                    {
                        skippedCount++;
                        notices.Add(new WorkerNotice(
                            "EXCEL_HIDDEN_SHEET_SKIPPED",
                            $"隐藏工作表“{(string)worksheet.Name}”未执行清理。",
                            "info"));
                        continue;
                    }

                    var cleanup = ExcelWorksheetCleaner.Clean(
                        worksheet,
                        options.SkipUnsafeCompressionSheets);
                    if (cleanup.WasCleaned)
                    {
                        cleanedCount++;
                    }
                    else if (cleanup.WasSkipped)
                    {
                        skippedCount++;
                        notices.Add(new WorkerNotice(
                            "EXCEL_SHEET_CLEANUP_SKIPPED",
                            $"工作表“{(string)worksheet.Name}”未清理：{cleanup.SkipReason}。",
                            "warning"));
                    }
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void EnsureSupportedWorkbook(
        ExcelWorkbookFacts facts,
        WorkerExcelToolOptions options,
        bool allowChartSheets)
    {
        if (facts.HasVbaProject)
        {
            throw new OfficeWorkerException(
                "EXCEL_VBA_UNSUPPORTED",
                "A workbook containing VBA cannot be safely written to .xlsx.");
        }

        if (!allowChartSheets && facts.ChartSheetCount > 0)
        {
            throw new OfficeWorkerException(
                "EXCEL_CHART_SHEET_UNSUPPORTED",
                "Chart sheets are not supported by this Excel operation.");
        }

        if (!options.PreserveExternalLinks && facts.ExternalLinkCount > 0)
        {
            throw new OfficeWorkerException(
                "EXCEL_EXTERNAL_LINKS_PRESENT",
                "The workbook contains external links and the job does not allow preserving them.");
        }
    }

    private static ExcelWorksheetFact[] SelectWorksheets(
        ExcelWorkbookFacts facts,
        bool includeHiddenWorksheets)
    {
        var selected = facts.Worksheets
            .Where(worksheet => includeHiddenWorksheets || worksheet.IsVisible)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new OfficeWorkerException(
                "EXCEL_WORKSHEET_REQUIRED",
                "The workbook does not contain a supported worksheet for this operation.");
        }

        if (!selected.Any(static worksheet => worksheet.IsVisible) && includeHiddenWorksheets)
        {
            throw new OfficeWorkerException(
                "EXCEL_VISIBLE_WORKSHEET_REQUIRED",
                "At least one normal worksheet must be visible before hidden worksheets can be copied safely.");
        }

        return selected;
    }

    private static void CopyWorksheets(
        object sourceWorkbookValue,
        object destinationWorkbookValue,
        ExcelWorksheetFact[] selectedWorksheets)
    {
        dynamic sourceWorkbook = sourceWorkbookValue;
        dynamic destinationWorkbook = destinationWorkbookValue;
        dynamic? sourceWorksheets = null;
        dynamic? selectedWorksheetGroup = null;
        dynamic? singleWorksheet = null;
        dynamic? destinationWorksheets = null;
        dynamic? lastDestinationWorksheet = null;
        try
        {
            sourceWorksheets = sourceWorkbook.Worksheets;
            destinationWorksheets = destinationWorkbook.Worksheets;
            lastDestinationWorksheet = destinationWorksheets[(int)destinationWorksheets.Count];
            if (selectedWorksheets.Length == (int)sourceWorksheets.Count)
            {
                sourceWorksheets.Copy(After: lastDestinationWorksheet);
                return;
            }

            if (selectedWorksheets.Length == 1)
            {
                singleWorksheet = sourceWorksheets[selectedWorksheets[0].Position];
                singleWorksheet.Copy(After: lastDestinationWorksheet);
                return;
            }

            var selectedNames = selectedWorksheets
                .Select(static worksheet => (object)worksheet.Name)
                .ToArray();
            selectedWorksheetGroup = sourceWorksheets[selectedNames];
            selectedWorksheetGroup.Copy(After: lastDestinationWorksheet);
        }
        finally
        {
            ComObject.FinalRelease(lastDestinationWorksheet);
            ComObject.FinalRelease(destinationWorksheets);
            ComObject.FinalRelease(singleWorksheet);
            ComObject.FinalRelease(selectedWorksheetGroup);
            ComObject.FinalRelease(sourceWorksheets);
        }
    }

    private static object CopyWorksheetsToNewWorkbook(
        ExcelApplicationSession session,
        object sourceWorkbookValue,
        ExcelWorksheetFact[] selectedWorksheets)
    {
        object? destinationWorkbook = null;
        try
        {
            destinationWorkbook = session.CreateWorkbook();
            CopyWorksheets(sourceWorkbookValue, destinationWorkbook, selectedWorksheets);
            RemoveLeadingWorksheets(destinationWorkbook, count: 1);
            var result = destinationWorkbook;
            destinationWorkbook = null;
            return result;
        }
        catch
        {
            session.CloseWorkbook(destinationWorkbook);
            throw;
        }
    }

    private static object CopySingleWorksheetToNewWorkbook(
        ExcelApplicationSession session,
        object sourceWorkbookValue,
        int sourcePosition)
    {
        object? destinationWorkbook = null;
        dynamic? sourceWorksheets = null;
        dynamic? sourceWorksheet = null;
        dynamic? destinationWorksheets = null;
        dynamic? placeholderWorksheet = null;
        try
        {
            destinationWorkbook = session.CreateWorkbook();
            dynamic sourceWorkbook = sourceWorkbookValue;
            sourceWorksheets = sourceWorkbook.Worksheets;
            sourceWorksheet = sourceWorksheets[sourcePosition];
            dynamic destination = destinationWorkbook;
            destinationWorksheets = destination.Worksheets;
            placeholderWorksheet = destinationWorksheets[1];
            sourceWorksheet.Copy(After: placeholderWorksheet);
            RemoveLeadingWorksheets(destinationWorkbook, count: 1);
            var result = destinationWorkbook;
            destinationWorkbook = null;
            return result;
        }
        catch
        {
            session.CloseWorkbook(destinationWorkbook);
            throw;
        }
        finally
        {
            ComObject.FinalRelease(placeholderWorksheet);
            ComObject.FinalRelease(destinationWorksheets);
            ComObject.FinalRelease(sourceWorksheet);
            ComObject.FinalRelease(sourceWorksheets);
        }
    }

    private static int GetWorksheetCount(object workbookValue)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            return (int)worksheets.Count;
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void RebindChartSeries(object workbookValue)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            for (var worksheetIndex = 1; worksheetIndex <= (int)worksheets.Count; worksheetIndex++)
            {
                dynamic? worksheet = null;
                dynamic? chartObjects = null;
                try
                {
                    worksheet = worksheets[worksheetIndex];
                    chartObjects = worksheet.ChartObjects();
                    for (var chartIndex = 1; chartIndex <= (int)chartObjects.Count; chartIndex++)
                    {
                        dynamic? chartObject = null;
                        dynamic? chart = null;
                        dynamic? seriesCollection = null;
                        try
                        {
                            chartObject = chartObjects[chartIndex];
                            chart = chartObject.Chart;
                            seriesCollection = chart.SeriesCollection();
                            for (var seriesIndex = 1; seriesIndex <= (int)seriesCollection.Count; seriesIndex++)
                            {
                                dynamic? series = null;
                                try
                                {
                                    series = seriesCollection[seriesIndex];
                                    var formula = (string)series.Formula;
                                    series.Formula = formula;
                                }
                                catch (System.Runtime.InteropServices.COMException)
                                {
                                    // Unsupported chart types remain subject to the external-link safety check.
                                }
                                finally
                                {
                                    ComObject.FinalRelease(series);
                                }
                            }
                        }
                        finally
                        {
                            ComObject.FinalRelease(seriesCollection);
                            ComObject.FinalRelease(chart);
                            ComObject.FinalRelease(chartObject);
                        }
                    }
                }
                finally
                {
                    ComObject.FinalRelease(chartObjects);
                    ComObject.FinalRelease(worksheet);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void SetWorksheetVisible(object workbookValue, int worksheetPosition)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        dynamic? worksheet = null;
        try
        {
            worksheets = workbook.Worksheets;
            worksheet = worksheets[worksheetPosition];
            worksheet.Visible = ExcelSheetVisible;
        }
        finally
        {
            ComObject.FinalRelease(worksheet);
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void SetWorksheetVisibilities(
        object workbookValue,
        List<bool> visibilities)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            for (var index = 0; index < visibilities.Count; index++)
            {
                if (!visibilities[index])
                {
                    continue;
                }

                SetWorksheetVisibility(worksheets, index + 1, ExcelSheetVisible);
            }

            for (var index = 0; index < visibilities.Count; index++)
            {
                if (visibilities[index])
                {
                    continue;
                }

                SetWorksheetVisibility(worksheets, index + 1, visibility: 0);
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void SetWorksheetVisibility(object worksheetsValue, int position, int visibility)
    {
        dynamic worksheets = worksheetsValue;
        dynamic? worksheet = null;
        try
        {
            worksheet = worksheets[position];
            worksheet.Visible = visibility;
        }
        finally
        {
            ComObject.FinalRelease(worksheet);
        }
    }

    private static void RebindSplitSourceLink(
        object workbookValue,
        string isolatedSourcePath,
        string originalSourcePath)
    {
        dynamic workbook = workbookValue;
        var linkSources = workbook.LinkSources(1);
        if (linkSources is not Array links)
        {
            return;
        }

        var isolatedSource = NormalizeExternalLinkSource(isolatedSourcePath);
        foreach (var linkValue in links)
        {
            if (linkValue is not string link ||
                !string.Equals(
                    NormalizeExternalLinkSource(link),
                    isolatedSource,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            workbook.ChangeLink(Name: link, NewName: originalSourcePath, Type: 1);
        }
    }

    private static void RemoveLeadingWorksheets(object workbookValue, int count)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            for (var index = 0; index < count; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[1];
                    worksheet.Delete();
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void RemoveTrailingWorksheets(object workbookValue, int count)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            for (var index = 0; index < count; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[(int)worksheets.Count];
                    worksheet.Delete();
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void RenameWorksheets(object workbookValue, IReadOnlyList<string> names)
    {
        dynamic workbook = workbookValue;
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            if ((int)worksheets.Count != names.Count)
            {
                throw new OfficeWorkerException(
                    "EXCEL_WORKSHEET_COUNT_MISMATCH",
                    "Excel copied an unexpected number of worksheets.",
                    true);
            }

            var temporaryNames = Enumerable.Range(0, names.Count)
                .Select(static index => $"__DP_{index}_{Guid.NewGuid():N}"[..31])
                .ToArray();
            for (var index = 0; index < temporaryNames.Length; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[index + 1];
                    worksheet.Name = temporaryNames[index];
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }

            for (var index = 0; index < names.Count; index++)
            {
                dynamic? worksheet = null;
                try
                {
                    worksheet = worksheets[index + 1];
                    worksheet.Name = names[index];
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
        }
    }

    private static void SaveAsOpenXml(object workbookValue, string outputPath)
    {
        dynamic workbook = workbookValue;
        workbook.SaveAs(
            Filename: outputPath,
            FileFormat: ExcelOpenXmlWorkbook,
            CreateBackup: false,
            AddToMru: false,
            Local: true);
    }

    private static ExcelWorkbookFacts VerifyWorkbook(
        ExcelApplicationSession session,
        string path,
        IReadOnlyList<string> expectedWorksheetNames,
        bool expectedUses1904DateSystem,
        IReadOnlyList<string>? allowedExternalLinkSources = null,
        bool requireAllExternalLinks = false,
        int? expectedChartSheetCount = null)
    {
        var inspection = OfficeDocumentInspector.Inspect(path);
        var expectedKind = string.Equals(
            Path.GetExtension(path),
            ".xls",
            StringComparison.OrdinalIgnoreCase)
            ? OfficeDocumentKind.ExcelBinary
            : OfficeDocumentKind.ExcelOpenXml;
        if (!inspection.IsValid || inspection.Kind != expectedKind)
        {
            throw new OfficeWorkerException(
                inspection.ErrorCode ?? "EXCEL_OUTPUT_INVALID",
                "Excel produced an invalid workbook output.",
                true);
        }

        object? workbook = null;
        try
        {
            workbook = session.OpenWorkbook(path, readOnly: true);
            var facts = ExcelWorkbookInspector.Inspect(workbook);
            var actualNames = facts.Worksheets.Select(static worksheet => worksheet.Name).ToArray();
            if (!actualNames.SequenceEqual(expectedWorksheetNames, StringComparer.Ordinal))
            {
                throw new OfficeWorkerException(
                    "EXCEL_OUTPUT_VERIFICATION_FAILED",
                    "The saved workbook did not preserve the planned worksheet order and names.",
                    true);
            }

            if (facts.Uses1904DateSystem != expectedUses1904DateSystem)
            {
                throw new OfficeWorkerException(
                    "EXCEL_DATE_SYSTEM_NOT_PRESERVED",
                    "The saved workbook did not preserve its 1900/1904 date system.",
                    true);
            }

            if (expectedChartSheetCount is not null &&
                facts.ChartSheetCount != expectedChartSheetCount.Value)
            {
                throw new OfficeWorkerException(
                    "EXCEL_CHART_SHEETS_NOT_PRESERVED",
                    "The saved workbook did not preserve its chart sheets.",
                    true);
            }

            if (allowedExternalLinkSources is not null)
            {
                EnsureNoIntroducedExternalLinks(facts, allowedExternalLinkSources);
                if (requireAllExternalLinks)
                {
                    EnsureAllExternalLinksPreserved(facts, allowedExternalLinkSources);
                }
            }

            return facts;
        }
        finally
        {
            session.CloseWorkbook(workbook);
        }
    }

    private static void SetWorkbookDateSystem(object workbookValue, bool uses1904DateSystem)
    {
        dynamic workbook = workbookValue;
        workbook.Date1904 = uses1904DateSystem;
    }

    private static void EnsureNoIntroducedExternalLinks(
        ExcelWorkbookFacts outputFacts,
        IReadOnlyList<string> allowedExternalLinkSources)
    {
        var allowedSources = allowedExternalLinkSources
            .Select(NormalizeExternalLinkSource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (outputFacts.ExternalLinkSources.Any(source =>
                !allowedSources.Contains(NormalizeExternalLinkSource(source))))
        {
            throw new OfficeWorkerException(
                "EXCEL_SPLIT_CROSS_SHEET_REFERENCE",
                "A worksheet references another sheet and cannot be split without creating an external link.");
        }
    }

    private static void EnsureAllExternalLinksPreserved(
        ExcelWorkbookFacts outputFacts,
        IReadOnlyList<string> expectedExternalLinkSources)
    {
        var expectedSources = expectedExternalLinkSources
            .Select(NormalizeExternalLinkSource)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actualSources = outputFacts.ExternalLinkSources
            .Select(NormalizeExternalLinkSource)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!actualSources.SequenceEqual(expectedSources, StringComparer.OrdinalIgnoreCase))
        {
            throw new OfficeWorkerException(
                "EXCEL_EXTERNAL_LINKS_NOT_PRESERVED",
                "The saved workbook did not preserve its external workbook links.",
                true);
        }
    }

    private static string NormalizeExternalLinkSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            source = uri.LocalPath;
        }

        try
        {
            return Path.GetFullPath(source);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return source;
        }
    }

    private static string AllocateDestinationPath(
        string outputDirectory,
        string proposedFileName,
        HashSet<string> reservedDestinations)
    {
        var stem = Path.GetFileNameWithoutExtension(proposedFileName);
        var extension = Path.GetExtension(proposedFileName);
        var candidate = Path.Combine(outputDirectory, proposedFileName);
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate) || !reservedDestinations.Add(candidate))
        {
            candidate = Path.Combine(outputDirectory, $"{stem} ({suffix}){extension}");
            suffix++;
        }

        return candidate;
    }

    private static string CreateLegacyCopyPath(string outputPath, Guid jobId)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $".{stem}.{jobId:N}.source.xls");
    }

    private static void AddCompatibilityNotices(
        List<WorkerNotice> notices,
        int externalLinkCount,
        int excludedHiddenCount)
    {
        if (externalLinkCount > 0)
        {
            notices.Add(new WorkerNotice(
                "EXCEL_EXTERNAL_LINKS_PRESERVED",
                $"已保留 {externalLinkCount} 个外部链接，处理期间未自动更新链接。",
                "warning"));
        }

        if (excludedHiddenCount > 0)
        {
            notices.Add(new WorkerNotice(
                "EXCEL_HIDDEN_SHEETS_EXCLUDED",
                $"按当前设置跳过了 {excludedHiddenCount} 张隐藏工作表。",
                "warning"));
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
