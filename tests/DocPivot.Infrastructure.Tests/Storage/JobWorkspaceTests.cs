using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Tests.Storage;

public sealed class JobWorkspaceTests
{
    [Fact]
    public void Dispose_RemovesOnlyTheJobDirectory()
    {
        var root = CreateTestRoot();
        var sentinel = Path.Combine(root, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        string workspacePath;

        using (var workspace = JobWorkspace.Create(root, Guid.NewGuid()))
        {
            workspacePath = workspace.Path;
            File.WriteAllText(workspace.GetIntermediatePath("page.json"), "{}");
            Assert.True(Directory.Exists(workspacePath));
        }

        Assert.False(Directory.Exists(workspacePath));
        Assert.True(File.Exists(sentinel));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void GetIntermediatePath_RejectsDirectoryTraversal()
    {
        var root = CreateTestRoot();
        try
        {
            using var workspace = JobWorkspace.Create(root, Guid.NewGuid());

            Assert.Throws<ArgumentException>(() => workspace.GetIntermediatePath("..\\outside.txt"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

