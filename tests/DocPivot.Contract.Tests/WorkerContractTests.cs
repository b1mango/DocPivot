using System.Text.Json;
using DocPivot.Core.Contracts;

namespace DocPivot.Contract.Tests;

public sealed class WorkerContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ProbeResult_SerializesCanonicalVersionProperty()
    {
        var message = new WorkerProbeResult(
            WorkerProtocol.CurrentVersion,
            WorkerMessageTypes.ProbeResult,
            "office",
            "ready",
            new Dictionary<string, bool> { ["word"] = true });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("probe-result", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("version", out _));
    }

    [Fact]
    public void StartMessage_SerializesRequiredFields()
    {
        var jobId = Guid.NewGuid();
        var message = WorkerStartMessage.Create(jobId, "word-to-pdf", "input.docx", "output.pdf");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(jobId, root.GetProperty("jobId").GetGuid());
        Assert.Equal("word-to-pdf", root.GetProperty("operation").GetString());
        Assert.Equal("start", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("options", out _));
    }

    [Fact]
    public void LegacyStartWithoutOptions_DeserializesAsCompleteWorkbookScope()
    {
        var jobId = Guid.NewGuid();
        var json = $$"""
            {
              "v": 1,
              "type": "start",
              "jobId": "{{jobId}}",
              "operation": "excel-to-pdf",
              "input": "input.xlsx",
              "output": "output.pdf"
            }
            """;

        var message = JsonSerializer.Deserialize<WorkerStartMessage>(json, JsonOptions);

        Assert.NotNull(message);
        Assert.Null(message.Options);
        Assert.Equal("output.pdf", message.Output);
    }

    [Fact]
    public void ExcelStart_WithWorksheetName_SerializesCanonicalOptions()
    {
        var message = WorkerStartMessage.CreateConversion(
            Guid.NewGuid(),
            OfficeWorkerOperations.ExcelToPdf,
            "input.xlsx",
            "output.pdf",
            "华东 明细_2026");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions));
        var options = document.RootElement.GetProperty("options");

        Assert.Equal("华东 明细_2026", options.GetProperty("worksheetName").GetString());
    }

    [Fact]
    public void ExcelWorksheetListRequest_OmitsOutput()
    {
        var message = WorkerStartMessage.CreateExcelWorksheetList(Guid.NewGuid(), "input.xls");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(OfficeWorkerOperations.ListExcelWorksheets, root.GetProperty("operation").GetString());
        Assert.False(root.TryGetProperty("output", out _));
        Assert.False(root.TryGetProperty("options", out _));
    }

    [Fact]
    public void ExcelMergeStart_UsesDedicatedMultiInputContract()
    {
        var message = WorkerExcelToolStartMessage.Create(
            Guid.NewGuid(),
            OfficeWorkerOperations.MergeExcelWorkbooks,
            ["first.xlsx", "second.xls"],
            "merged.xlsx");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions));
        var root = document.RootElement;

        Assert.Equal("excel-merge", root.GetProperty("operation").GetString());
        Assert.Equal(2, root.GetProperty("inputs").GetArrayLength());
        Assert.False(root.TryGetProperty("input", out _));
        Assert.True(root.GetProperty("options").GetProperty("includeHiddenWorksheets").GetBoolean());
        Assert.True(root.GetProperty("options").GetProperty("preserveExternalLinks").GetBoolean());
        Assert.True(root.GetProperty("options").GetProperty("skipUnsafeCompressionSheets").GetBoolean());
        Assert.True(root.GetProperty("options").GetProperty("convertLegacyWorkbookToOpenXml").GetBoolean());
        Assert.Equal(0, root.GetProperty("options").GetProperty("imageCompressionLevel").GetInt32());
    }

    [Fact]
    public void PdfToExcelStart_RoundTripsExtractionOptions()
    {
        var jobId = Guid.NewGuid();
        var message = WorkerPdfToExcelStartMessage.Create(
            jobId,
            "input.pdf",
            "output.xlsx",
            new WorkerPdfToExcelOptions(
                WorkerPdfOcrMode.ForceOcr,
                WorkerPdfWorksheetMode.OneWorksheetPerPage));

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<WorkerPdfToExcelStartMessage>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(jobId, roundTrip.JobId);
        Assert.Equal(PdfTableWorkerOperations.PdfToExcel, roundTrip.Operation);
        Assert.Equal(WorkerPdfOcrMode.ForceOcr, roundTrip.Options.OcrMode);
        Assert.Equal(
            WorkerPdfWorksheetMode.OneWorksheetPerPage,
            roundTrip.Options.WorksheetMode);
        Assert.Equal("chi_sim+eng", roundTrip.Options.OcrLanguage);
    }

    [Fact]
    public void ExcelWorksheetsMessage_RoundTripsOrderedDescriptors()
    {
        var message = WorkerExcelWorksheetsMessage.Create(
            Guid.NewGuid(),
            [
                new ExcelWorksheetDescriptor("总览", 1, "visible", true),
                new ExcelWorksheetDescriptor("内部参数", 2, "very-hidden", false),
            ],
            new ExcelWorkbookCompatibilityReport(
                2,
                1,
                0,
                1,
                false,
                true,
                ["嵌入式图表"]));

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<WorkerExcelWorksheetsMessage>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(WorkerMessageTypes.ExcelWorksheets, roundTrip.Type);
        Assert.Collection(
            roundTrip.Worksheets,
            first =>
            {
                Assert.Equal("总览", first.Name);
                Assert.True(first.IsExportable);
            },
            second =>
            {
                Assert.Equal(2, second.Position);
                Assert.Equal("very-hidden", second.Visibility);
                Assert.False(second.IsExportable);
            });
        Assert.NotNull(roundTrip.Compatibility);
        Assert.Equal(1, roundTrip.Compatibility.ExternalLinkCount);
        Assert.True(roundTrip.Compatibility.Uses1904DateSystem);
        Assert.Equal(["嵌入式图表"], roundTrip.Compatibility.CompressionRisks);
    }

    [Fact]
    public void Result_WithNoticesAndMetrics_RoundTripsOptionalDetails()
    {
        var message = WorkerResultMessage.Succeeded(
            Guid.NewGuid(),
            ["compressed.xlsx"],
            [new WorkerNotice("EXCEL_SHEET_SKIPPED", "Sheet2 was not changed.", "warning")],
            new Dictionary<string, string>
            {
                ["bytesBefore"] = "1024",
                ["bytesAfter"] = "768",
            });

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<WorkerResultMessage>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal("EXCEL_SHEET_SKIPPED", Assert.Single(roundTrip.Notices!).Code);
        Assert.Equal("768", roundTrip.Metrics!["bytesAfter"]);
    }

    [Fact]
    public void Schema_DeclaresCurrentProtocolVersionAndAllMessageTypes()
    {
        var schemaPath = Path.Combine(FindRepositoryRoot(), "schemas", "worker-message.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var definitions = document.RootElement.GetProperty("$defs");

        Assert.Equal(
            WorkerProtocol.CurrentVersion,
            definitions.GetProperty("base").GetProperty("properties").GetProperty("v").GetProperty("const").GetInt32());
        Assert.True(definitions.TryGetProperty("start", out _));
        Assert.True(definitions.TryGetProperty("progress", out _));
        Assert.True(definitions.TryGetProperty("result", out _));
        Assert.True(definitions.TryGetProperty("error", out _));
        Assert.True(definitions.TryGetProperty("probeResult", out _));
        Assert.True(definitions.TryGetProperty("requestOptions", out _));
        Assert.True(definitions.TryGetProperty("excelToolStart", out _));
        Assert.True(definitions.TryGetProperty("excelToolOptions", out _));
        Assert.True(definitions.TryGetProperty("pdfTableStart", out _));
        Assert.True(definitions.TryGetProperty("pdfTableOptions", out _));
        Assert.True(definitions.TryGetProperty("excelWorksheet", out _));
        Assert.True(definitions.TryGetProperty("excelWorksheets", out _));
        Assert.True(definitions.TryGetProperty("notice", out _));
    }

    [Fact]
    public void ProbeResult_GoldenFileDeserializesToCurrentContract()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "worker-probe-result.v1.json");
        var message = JsonSerializer.Deserialize<WorkerProbeResult>(
            File.ReadAllText(fixturePath),
            JsonOptions);

        Assert.NotNull(message);
        Assert.Equal(WorkerProtocol.CurrentVersion, message.V);
        Assert.Equal(WorkerMessageTypes.ProbeResult, message.Type);
        Assert.Equal("office", message.Worker);
        Assert.Equal("ready", message.Status);
        Assert.True(message.Capabilities["word"]);
        Assert.True(message.Capabilities["excel"]);
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
}
