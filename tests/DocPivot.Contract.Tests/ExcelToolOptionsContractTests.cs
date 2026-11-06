using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Core.Excel;

namespace DocPivot.Contract.Tests;

public sealed class ExcelToolOptionsContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LegacyOptions_DefaultToAllSheetsSeparateFilesAndLegacyImageLevel()
    {
        const string json = """
            {"includeHiddenWorksheets":true,"preserveExternalLinks":true,
             "skipUnsafeCompressionSheets":true,"convertLegacyWorkbookToOpenXml":true,
             "imageCompressionLevel":2}
            """;

        var options = JsonSerializer.Deserialize<WorkerExcelToolOptions>(json, JsonOptions);

        Assert.NotNull(options);
        Assert.Null(options.SelectedWorksheets);
        Assert.Equal(ExcelSplitOutputMode.SeparateFiles, options.SplitOutputMode);
        Assert.Null(options.CompressionStrength);
        Assert.Equal(ExcelImageCompressionLevel.Balanced, options.ImageCompressionLevel);
    }

    [Fact]
    public void SplitSelectionAndStrength_RoundTripWithoutChangingSelectionOrder()
    {
        var request = WorkerExcelToolStartMessage.Create(
            Guid.NewGuid(),
            OfficeWorkerOperations.SplitExcelWorkbook,
            ["source.xlsx"],
            "exports",
            WorkerExcelToolOptions.SafeDefaults with
            {
                SelectedWorksheets = ["明细", "总览"],
                SplitOutputMode = ExcelSplitOutputMode.SingleWorkbook,
                CompressionStrength = 47,
            });

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var restored = JsonSerializer.Deserialize<WorkerExcelToolStartMessage>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(request.JobId, restored.JobId);
        Assert.Equal(["明细", "总览"], restored.Options.SelectedWorksheets);
        Assert.Equal(ExcelSplitOutputMode.SingleWorkbook, restored.Options.SplitOutputMode);
        Assert.Equal(47, restored.Options.CompressionStrength);
    }
}
