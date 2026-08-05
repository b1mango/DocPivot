using DocPivot.Infrastructure.Storage;

namespace DocPivot.Infrastructure.Tests.Storage;

public sealed class AtomicOutputBatchTests
{
    [Fact]
    public void Commit_MovesAllFilesInOrderAndReturnsDestinations()
    {
        var root = CreateTestRoot();
        try
        {
            var stagingOne = CreateStagedFile(root, ".one.tmp", "first");
            var stagingTwo = CreateStagedFile(root, ".two.tmp", "second");
            var destinationOne = Path.Combine(root, "one.xlsx");
            var destinationTwo = Path.Combine(root, "two.xlsx");

            var destinations = AtomicOutputBatch.Commit(
            [
                (stagingOne, destinationOne),
                (stagingTwo, destinationTwo),
            ]);

            Assert.Equal([destinationOne, destinationTwo], destinations);
            Assert.False(File.Exists(stagingOne));
            Assert.False(File.Exists(stagingTwo));
            Assert.Equal("first", File.ReadAllText(destinationOne));
            Assert.Equal("second", File.ReadAllText(destinationTwo));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Commit_WhenLaterStagingFileIsMissing_PerformsNoMoves()
    {
        var root = CreateTestRoot();
        try
        {
            var stagingOne = CreateStagedFile(root, ".one.tmp", "first");
            var missingStaging = Path.Combine(root, ".missing.tmp");
            var destinationOne = Path.Combine(root, "one.xlsx");
            var destinationTwo = Path.Combine(root, "two.xlsx");

            Assert.Throws<FileNotFoundException>(() => AtomicOutputBatch.Commit(
            [
                (stagingOne, destinationOne),
                (missingStaging, destinationTwo),
            ]));

            Assert.True(File.Exists(stagingOne));
            Assert.False(File.Exists(destinationOne));
            Assert.False(File.Exists(destinationTwo));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Commit_WhenLaterDestinationExists_PerformsNoMoves()
    {
        var root = CreateTestRoot();
        try
        {
            var stagingOne = CreateStagedFile(root, ".one.tmp", "first");
            var stagingTwo = CreateStagedFile(root, ".two.tmp", "second");
            var destinationOne = Path.Combine(root, "one.xlsx");
            var destinationTwo = CreateStagedFile(root, "two.xlsx", "existing");

            Assert.Throws<IOException>(() => AtomicOutputBatch.Commit(
            [
                (stagingOne, destinationOne),
                (stagingTwo, destinationTwo),
            ]));

            Assert.True(File.Exists(stagingOne));
            Assert.True(File.Exists(stagingTwo));
            Assert.False(File.Exists(destinationOne));
            Assert.Equal("existing", File.ReadAllText(destinationTwo));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Commit_RejectsCrossDirectoryPairBeforeMovingFiles()
    {
        var root = CreateTestRoot();
        var otherRoot = CreateTestRoot();
        try
        {
            var staging = CreateStagedFile(root, ".one.tmp", "first");
            var destination = Path.Combine(otherRoot, "one.xlsx");

            Assert.Throws<InvalidOperationException>(() =>
                AtomicOutputBatch.Commit([(staging, destination)]));

            Assert.True(File.Exists(staging));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteTestRoot(root);
            DeleteTestRoot(otherRoot);
        }
    }

    [Fact]
    public void Commit_RejectsCaseInsensitiveDuplicatePathsBeforeMovingFiles()
    {
        var root = CreateTestRoot();
        try
        {
            var stagingOne = CreateStagedFile(root, ".one.tmp", "first");
            var stagingTwo = CreateStagedFile(root, ".two.tmp", "second");
            var destination = Path.Combine(root, "result.xlsx");
            var duplicateDestination = Path.Combine(root, "RESULT.XLSX");

            Assert.Throws<InvalidOperationException>(() => AtomicOutputBatch.Commit(
            [
                (stagingOne, destination),
                (stagingTwo, duplicateDestination),
            ]));

            Assert.True(File.Exists(stagingOne));
            Assert.True(File.Exists(stagingTwo));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Commit_WhenSecondMoveFails_RemovesCommittedDestinationAndKeepsUnattemptedStaging()
    {
        var root = CreateTestRoot();
        try
        {
            var stagingOne = CreateStagedFile(root, ".one.tmp", "first");
            var stagingTwo = CreateStagedFile(root, ".two.tmp", "second");
            var destinationOne = Path.Combine(root, "one.xlsx");
            var destinationTwo = Path.Combine(root, "two.xlsx");
            var fileSystem = new FailOnMoveFileSystem(moveNumberToFail: 2);

            Assert.Throws<IOException>(() => AtomicOutputBatch.Commit(
            [
                (stagingOne, destinationOne),
                (stagingTwo, destinationTwo),
            ], fileSystem));

            Assert.False(File.Exists(stagingOne));
            Assert.False(File.Exists(destinationOne));
            Assert.True(File.Exists(stagingTwo));
            Assert.Equal("second", File.ReadAllText(stagingTwo));
            Assert.False(File.Exists(destinationTwo));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateStagedFile(string root, string fileName, string contents)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailOnMoveFileSystem(int moveNumberToFail) : IAtomicOutputBatchFileSystem
    {
        private int _moveCount;

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void MoveFile(string stagingPath, string destinationPath)
        {
            _moveCount++;
            if (_moveCount == moveNumberToFail)
            {
                throw new IOException("Injected move failure.");
            }

            File.Move(stagingPath, destinationPath);
        }

        public void DeleteFile(string path) => File.Delete(path);
    }
}
