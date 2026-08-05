using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Tests.Storage;

public sealed class AtomicOutputFileTests
{
    [Fact]
    public void Commit_MovesStagedFileToDestination()
    {
        var root = CreateTestRoot();
        try
        {
            var destination = Path.Combine(root, "result.pdf");
            var staging = AtomicOutputFile.CreateStagingPath(destination, Guid.NewGuid());
            Assert.EndsWith(".pdf", staging, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(staging, "pdf-result");

            AtomicOutputFile.Commit(staging, destination);

            Assert.False(File.Exists(staging));
            Assert.Equal("pdf-result", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Commit_RejectsCrossDirectoryMove()
    {
        var root = CreateTestRoot();
        var other = CreateTestRoot();
        try
        {
            var staging = Path.Combine(root, "staging.tmp");
            var destination = Path.Combine(other, "result.pdf");
            File.WriteAllText(staging, "pdf-result");

            Assert.Throws<InvalidOperationException>(() => AtomicOutputFile.Commit(staging, destination));
            Assert.True(File.Exists(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
