using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocPivot.Infrastructure.Pdf;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfOperationsClientIntegrationTests
{
    private static readonly TimeSpan IntegrationTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProbeAndPreflight_UsePinnedRuntimeWithUnicodeAndSpacePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(testRoot, "电子 文档.pdf");
            CreateMinimalPdf(inputPath, ["PREFLIGHT_ONE", "PREFLIGHT_TWO", "PREFLIGHT_THREE"]);
            var probe = new QpdfToolProbe(repositoryRoot);
            var client = new QpdfOperationsClient(repositoryRoot, IntegrationTimeout);

            var status = await probe.ProbeAsync();
            var preflight = await client.PreflightAsync(new PdfPreflightRequest(inputPath));

            Assert.True(status.IsReady, status.ErrorMessage);
            Assert.Equal(QpdfToolProbe.PinnedVersion, status.Version);
            Assert.True(preflight.IsSucceeded, preflight.ErrorMessage);
            Assert.Equal(3, preflight.PageCount);
            Assert.False(preflight.IsEncrypted);
            Assert.False(preflight.HasSignatureFields);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeAndSplit_PreserveOrderAndProduceExpectedPageGroups()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = CreateTestRoot();
        try
        {
            var firstInput = Path.Combine(testRoot, "第一 份.pdf");
            var secondInput = Path.Combine(testRoot, "第二 份.pdf");
            CreateMinimalPdf(
                firstInput,
                ["FIRST_MARKER"],
                title: "PRIMARY_DOCUMENT_TITLE",
                bookmarkTitle: "PRIMARY_BOOKMARK");
            CreateMinimalPdf(
                secondInput,
                ["SECOND_MARKER", "THIRD_MARKER"],
                title: "SECONDARY_DOCUMENT_TITLE",
                bookmarkTitle: "SECONDARY_BOOKMARK");
            var firstHash = GetSha256(firstInput);
            var secondHash = GetSha256(secondInput);
            var client = new QpdfOperationsClient(repositoryRoot, IntegrationTimeout);
            var mergedPath = Path.Combine(testRoot, "合并 结果.pdf");

            var merge = await client.MergeAsync(new PdfMergeRequest(
                [firstInput, secondInput],
                mergedPath));

            Assert.True(merge.IsSucceeded, merge.ErrorMessage);
            Assert.Equal([mergedPath], merge.Artifacts);
            Assert.Equal(3, await GetPageCountAsync(repositoryRoot, mergedPath));
            Assert.Equal(firstHash, GetSha256(firstInput));
            Assert.Equal(secondHash, GetSha256(secondInput));
            var mergedText = await ReadUncompressedPdfAsync(repositoryRoot, mergedPath, testRoot);
            AssertMarkersInOrder(mergedText, "FIRST_MARKER", "SECOND_MARKER", "THIRD_MARKER");
            Assert.Contains("PRIMARY_DOCUMENT_TITLE", mergedText, StringComparison.Ordinal);
            Assert.Contains("PRIMARY_BOOKMARK", mergedText, StringComparison.Ordinal);
            Assert.DoesNotContain("SECONDARY_DOCUMENT_TITLE", mergedText, StringComparison.Ordinal);
            Assert.DoesNotContain("SECONDARY_BOOKMARK", mergedText, StringComparison.Ordinal);
            Assert.Contains(
                merge.Notices,
                static notice => notice.Code == "PDF_MERGE_PRIMARY_DOCUMENT_INFO");

            var grouped = await client.SplitAsync(new PdfSplitRequest(
                mergedPath,
                Path.Combine(testRoot, "分组.pdf"),
                PdfSplitSelection.EveryNPages(2)));
            Assert.True(grouped.IsSucceeded, grouped.ErrorMessage);
            Assert.Equal(
                ["分组-1-2.pdf", "分组-3.pdf"],
                grouped.Artifacts.Select(Path.GetFileName));
            Assert.Equal(2, await GetPageCountAsync(repositoryRoot, grouped.Artifacts[0]));
            Assert.Equal(1, await GetPageCountAsync(repositoryRoot, grouped.Artifacts[1]));

            var everyPage = await client.SplitAsync(new PdfSplitRequest(
                mergedPath,
                Path.Combine(testRoot, "逐页.pdf"),
                PdfSplitSelection.EveryPage()));
            Assert.True(everyPage.IsSucceeded, everyPage.ErrorMessage);
            Assert.Equal(3, everyPage.Artifacts.Count);
            Assert.All(everyPage.Artifacts, path => Assert.True(File.Exists(path)));

            var selectedPath = Path.Combine(testRoot, "选择页.pdf");
            var selected = await client.SplitAsync(new PdfSplitRequest(
                mergedPath,
                selectedPath,
                PdfSplitSelection.CustomRange("3,1")));
            Assert.True(selected.IsSucceeded, selected.ErrorMessage);
            Assert.Equal(2, await GetPageCountAsync(repositoryRoot, selectedPath));
            var selectedText = await ReadUncompressedPdfAsync(repositoryRoot, selectedPath, testRoot);
            AssertMarkersInOrder(selectedText, "THIRD_MARKER", "FIRST_MARKER");

            var oddPath = Path.Combine(testRoot, "奇数页.pdf");
            var odd = await client.SplitAsync(new PdfSplitRequest(
                mergedPath,
                oddPath,
                PdfSplitSelection.OddPages()));
            Assert.True(odd.IsSucceeded, odd.ErrorMessage);
            Assert.Equal(2, await GetPageCountAsync(repositoryRoot, oddPath));
            var oddText = await ReadUncompressedPdfAsync(repositoryRoot, oddPath, testRoot);
            AssertMarkersInOrder(oddText, "FIRST_MARKER", "THIRD_MARKER");

            var evenPath = Path.Combine(testRoot, "偶数页.pdf");
            var even = await client.SplitAsync(new PdfSplitRequest(
                mergedPath,
                evenPath,
                PdfSplitSelection.EvenPages()));
            Assert.True(even.IsSucceeded, even.ErrorMessage);
            Assert.Equal(1, await GetPageCountAsync(repositoryRoot, evenPath));
            var evenText = await ReadUncompressedPdfAsync(repositoryRoot, evenPath, testRoot);
            Assert.Contains("SECOND_MARKER", evenText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIRST_MARKER", evenText, StringComparison.Ordinal);
            Assert.DoesNotContain("THIRD_MARKER", evenText, StringComparison.Ordinal);

            Assert.Empty(Directory.EnumerateFiles(testRoot, ".*.tmp.pdf"));
            Assert.Equal(firstHash, GetSha256(firstInput));
            Assert.Equal(secondHash, GetSha256(secondInput));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Optimize_WhenThereIsNoBenefit_RetainsExactSourceContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = CreateTestRoot();
        try
        {
            var inputPath = Path.Combine(testRoot, "最小 原件.pdf");
            var outputPath = Path.Combine(testRoot, "无损 结果.pdf");
            CreateMinimalPdf(inputPath, ["MINIMAL_SOURCE"]);
            var sourceBytes = File.ReadAllBytes(inputPath);
            var client = new QpdfOperationsClient(repositoryRoot, IntegrationTimeout);

            var result = await client.OptimizeAsync(new PdfOptimizeRequest(inputPath, outputPath));

            Assert.True(result.IsSucceeded, result.ErrorMessage);
            Assert.Equal("false", result.Metrics["optimizationApplied"]);
            Assert.Contains(
                result.Notices,
                static notice => notice.Code == "PDF_NO_COMPRESSION_BENEFIT");
            Assert.Equal(sourceBytes, File.ReadAllBytes(outputPath));
            Assert.Equal(sourceBytes, File.ReadAllBytes(inputPath));

            var lossyOutput = Path.Combine(testRoot, "有损 禁止.pdf");
            var lossy = await client.OptimizeAsync(new PdfOptimizeRequest(
                inputPath,
                lossyOutput,
                CompressionStrength: 1));
            Assert.False(lossy.IsSucceeded);
            Assert.Equal("PDF_LOSSY_COMPRESSION_UNAVAILABLE", lossy.ErrorCode);
            Assert.False(File.Exists(lossyOutput));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Preflight_HandlesEncryptedPasswordsAndOverLimitPdf()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = CreateTestRoot();
        try
        {
            var client = new QpdfOperationsClient(repositoryRoot, IntegrationTimeout);
            var sourcePath = Path.Combine(testRoot, "source.pdf");
            var encryptedPath = Path.Combine(testRoot, "encrypted.pdf");
            CreateMinimalPdf(sourcePath, ["ENCRYPTED_SOURCE"]);
            var encryptResult = await RunQpdfAsync(
                repositoryRoot,
                [
                    sourcePath,
                    "--encrypt",
                    "user-password",
                    "owner-password",
                    "256",
                    "--",
                    encryptedPath,
                ]);
            Assert.Equal(0, encryptResult.ExitCode);

            var encrypted = await client.PreflightAsync(new PdfPreflightRequest(encryptedPath));

            Assert.False(encrypted.IsSucceeded);
            Assert.True(encrypted.IsEncrypted);
            Assert.Equal("PDF_PASSWORD_REQUIRED", encrypted.ErrorCode);

            var wrongPassword = await client.PreflightAsync(new PdfPreflightRequest(
                encryptedPath,
                "wrong-password"));
            Assert.False(wrongPassword.IsSucceeded);
            Assert.True(wrongPassword.IsEncrypted);
            Assert.Equal("PDF_PASSWORD_INVALID", wrongPassword.ErrorCode);

            var correctPassword = await client.PreflightAsync(new PdfPreflightRequest(
                encryptedPath,
                "user-password"));
            Assert.True(correctPassword.IsSucceeded, correctPassword.ErrorMessage);
            Assert.True(correctPassword.IsEncrypted);
            Assert.Equal(1, correctPassword.PageCount);

            var encryptedSplit = await client.SplitAsync(new PdfSplitRequest(
                encryptedPath,
                Path.Combine(testRoot, "encrypted-split.pdf"),
                PdfSplitSelection.CustomRange("1"),
                "user-password"));
            Assert.True(encryptedSplit.IsSucceeded, encryptedSplit.ErrorMessage);
            Assert.Equal(1, await GetPageCountAsync(repositoryRoot, encryptedSplit.Artifacts[0]));

            var permissionOnlyPath = Path.Combine(testRoot, "permission-only.pdf");
            var permissionEncryptResult = await RunQpdfAsync(
                repositoryRoot,
                [
                    sourcePath,
                    "--encrypt",
                    string.Empty,
                    "owner-password",
                    "256",
                    "--",
                    permissionOnlyPath,
                ]);
            Assert.Equal(0, permissionEncryptResult.ExitCode);

            var permissionOnly = await client.PreflightAsync(new PdfPreflightRequest(permissionOnlyPath));
            Assert.True(permissionOnly.IsSucceeded, permissionOnly.ErrorMessage);
            Assert.True(permissionOnly.IsEncrypted);
            Assert.Equal(1, permissionOnly.PageCount);

            var overLimitPath = Path.Combine(testRoot, "1001-pages.pdf");
            CreateMinimalPdf(
                overLimitPath,
                Enumerable.Range(1, 1001).Select(page => $"PAGE_{page}").ToArray());

            var overLimit = await client.PreflightAsync(new PdfPreflightRequest(overLimitPath));

            Assert.False(overLimit.IsSucceeded);
            Assert.Equal("PDF_PAGE_LIMIT_EXCEEDED", overLimit.ErrorCode);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task<int> GetPageCountAsync(string repositoryRoot, string path)
    {
        var result = await RunQpdfAsync(repositoryRoot, ["--show-npages", path]);
        Assert.Equal(0, result.ExitCode);
        return int.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadUncompressedPdfAsync(
        string repositoryRoot,
        string inputPath,
        string outputDirectory)
    {
        var outputPath = Path.Combine(outputDirectory, $"inspect-{Guid.NewGuid():N}.pdf");
        var result = await RunQpdfAsync(
            repositoryRoot,
            [
                inputPath,
                "--qdf",
                "--stream-data=uncompress",
                "--object-streams=disable",
                outputPath,
            ]);
        Assert.Equal(0, result.ExitCode);
        return File.ReadAllText(outputPath, Encoding.Latin1);
    }

    private static Task<DocPivot.Infrastructure.Runtime.ExternalProcessResult> RunQpdfAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments)
    {
        var executablePath = Path.Combine(
            repositoryRoot,
            QpdfToolProbe.RelativeExecutablePath);
        return new QpdfCliRunner().RunAsync(
            executablePath,
            arguments,
            IntegrationTimeout);
    }

    private static void AssertMarkersInOrder(string text, params string[] markers)
    {
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Marker '{marker}' was missing or out of order.");
            previousIndex = index;
        }
    }

    private static void CreateMinimalPdf(
        string path,
        string[] pageMarkers,
        string? title = null,
        string? bookmarkTitle = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageMarkers.Length, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var fontObjectId = 3 + pageMarkers.Length * 2;
        var outlineRootObjectId = bookmarkTitle is null ? 0 : fontObjectId + 1;
        var outlineItemObjectId = bookmarkTitle is null ? 0 : outlineRootObjectId + 1;
        var kids = string.Join(' ', Enumerable.Range(0, pageMarkers.Length)
            .Select(index => $"{3 + index * 2} 0 R"));
        var catalogOutlines = bookmarkTitle is null
            ? string.Empty
            : $" /Outlines {outlineRootObjectId} 0 R /PageMode /UseOutlines";
        var objects = new List<byte[]>
        {
            Ascii($"<< /Type /Catalog /Pages 2 0 R{catalogOutlines} >>"),
            Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pageMarkers.Length} >>"),
        };

        for (var index = 0; index < pageMarkers.Length; index++)
        {
            var pageObjectId = 3 + index * 2;
            var contentObjectId = pageObjectId + 1;
            var marker = EscapePdfString(pageMarkers[index]);
            var content = $"BT /F1 12 Tf 40 100 Td ({marker}) Tj ET\n";
            var contentBytes = Ascii(content);
            objects.Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] " +
                $"/Resources << /Font << /F1 {fontObjectId} 0 R >> >> " +
                $"/Contents {contentObjectId} 0 R >>"));
            objects.Add(Combine(
                Ascii($"<< /Length {contentBytes.Length} >>\nstream\n"),
                contentBytes,
                Ascii("endstream")));
        }

        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        if (bookmarkTitle is not null)
        {
            objects.Add(Ascii(
                $"<< /Type /Outlines /First {outlineItemObjectId} 0 R " +
                $"/Last {outlineItemObjectId} 0 R /Count 1 >>"));
            objects.Add(Ascii(
                $"<< /Title ({EscapePdfString(bookmarkTitle)}) " +
                $"/Parent {outlineRootObjectId} 0 R /Dest [3 0 R /Fit] >>"));
        }

        int? infoObjectId = null;
        if (title is not null)
        {
            infoObjectId = objects.Count + 1;
            objects.Add(Ascii($"<< /Title ({EscapePdfString(title)}) >>"));
        }

        using var stream = new MemoryStream();
        Write(stream, Ascii("%PDF-1.4\n"));
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            Write(stream, Ascii($"{index + 1} 0 obj\n"));
            Write(stream, objects[index]);
            Write(stream, Ascii("\nendobj\n"));
        }

        var xrefOffset = stream.Position;
        Write(stream, Ascii($"xref\n0 {objects.Count + 1}\n"));
        Write(stream, Ascii("0000000000 65535 f \n"));
        foreach (var offset in offsets.Skip(1))
        {
            Write(stream, Ascii($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n"));
        }

        var trailerInfo = infoObjectId is null ? string.Empty : $" /Info {infoObjectId} 0 R";
        Write(stream, Ascii(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailerInfo} >>\n" +
            $"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n"));
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static string EscapePdfString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] Combine(params byte[][] parts)
    {
        var length = parts.Sum(static part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes);

    private static string GetSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

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

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DocPivot.Tests",
            "PDF 中文 空格",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
