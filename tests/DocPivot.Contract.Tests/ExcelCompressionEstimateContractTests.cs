using System.Text.Json;
using DocPivot.Core.Contracts;

namespace DocPivot.Contract.Tests;

public sealed class ExcelCompressionEstimateContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void EstimateRequest_RoundTripsWorkerFields()
    {
        var request = WorkerExcelCompressionEstimateStartMessage.Create(
            Guid.NewGuid(), "workbook.xlsx", 55, "cache");

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var restored = JsonSerializer.Deserialize<WorkerExcelCompressionEstimateStartMessage>(json, JsonOptions);

        Assert.Equal(request, restored);
        Assert.Equal(OfficeWorkerOperations.EstimateExcelCompression, restored!.Operation);
        Assert.Contains("\"strength\":55", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateResult_RoundTripsByteCounts()
    {
        var result = WorkerExcelCompressionEstimateMessage.Create(Guid.NewGuid(), 10_000, 6_000);

        var restored = JsonSerializer.Deserialize<WorkerExcelCompressionEstimateMessage>(
            JsonSerializer.Serialize(result, JsonOptions), JsonOptions);

        Assert.Equal(result, restored);
        Assert.Equal(WorkerExcelCompressionEstimateMessage.MessageType, restored!.Type);
    }
}
