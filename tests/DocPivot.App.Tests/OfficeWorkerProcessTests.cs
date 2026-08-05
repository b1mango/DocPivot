using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Infrastructure.Runtime;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App.Tests;

public sealed class OfficeWorkerProcessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Execute_NullInputReturnsStructuredErrorWithoutUnhandledCrash()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "DocPivot.App.exe");
        Assert.True(File.Exists(appPath), $"Test app host was not found: {appPath}");
        var jobId = Guid.NewGuid();
        var request = $$"""
            {"v":1,"type":"start","jobId":"{{jobId}}","operation":"excel-list-worksheets","input":null}
            """;

        var result = await ExternalProcessRunner.RunAsync(
            appPath,
            ["--office-worker", "--execute", "--timeout-seconds", "10"],
            request,
            TimeSpan.FromSeconds(15));

        Assert.Equal(2, result.ExitCode);
        var error = result.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonSerializer.Deserialize<WorkerErrorMessage>(line, JsonOptions))
            .LastOrDefault(static message => message is not null);
        Assert.NotNull(error);
        Assert.Equal(jobId, error.JobId);
        Assert.Equal("START_MESSAGE_INVALID", error.Code);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdfTableProbe_ThroughAppHostReportsAllOfflineCapabilities()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "DocPivot.App.exe");
        Assert.True(File.Exists(appPath), $"Test app host was not found: {appPath}");
        var client = new PdfTableWorkerClient(
            appPath,
            FindRepositoryRoot(),
            ["--pdf-table-worker"]);

        var status = await client.ProbeAsync();

        Assert.True(status.IsDigitalTextReady, status.ErrorMessage);
        Assert.True(status.IsXlsxReady, status.ErrorMessage);
        Assert.True(status.IsLocalOcrReady, status.ErrorMessage);
        Assert.True(status.IsReady, status.ErrorMessage);
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
