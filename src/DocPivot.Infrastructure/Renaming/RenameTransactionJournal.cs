using System.Security.Cryptography;
using System.Text.Json;
using DocPivot.Core.Documents;

namespace DocPivot.Infrastructure.Renaming;

// The journal is durable before each destructive move. A single pending move
// bridges a process exit between the filesystem operation and its acknowledgement.
internal sealed class RenameTransactionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private JournalState _state;

    private RenameTransactionJournal(string path, JournalState state)
    {
        _path = path;
        _state = state;
    }

    public static RenameTransactionJournal Create(string root, IReadOnlyList<(string Source, string Target, string Stage)> items)
    {
        Directory.CreateDirectory(root);
        var journal = new RenameTransactionJournal(
            Path.Combine(root, $"pending-{Guid.NewGuid():N}.json"),
            new JournalState(1, items.Select(item => new JournalItem(
                item.Source, item.Target, item.Stage, item.Source, Fingerprint(item.Source))).ToArray(), null, false));
        journal.Save();
        return journal;
    }

    public void Move(int index, string source, string destination, Action<string, string> move)
    {
        _state = _state with { Pending = new PendingMove(index, source, destination), Completed = false };
        Save();
        try
        {
            move(source, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If the process is still alive, preserve an exact location before
            // the caller attempts its normal rollback.
            ResolvePending();
            Save();
            throw;
        }
        _state.Items[index] = _state.Items[index] with { Current = destination };
        _state = _state with { Pending = null };
        Save();
    }

    public void Complete()
    {
        // Commit the terminal state before deleting. A denied delete must never
        // turn a completed rename into an interrupted transaction on restart.
        _state = _state with { Completed = true };
        Save();
        try { File.Delete(_path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<string> FindPending(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.GetFiles(root, "pending-*.json").Where(path =>
        {
            try
            {
                return JsonSerializer.Deserialize<JournalState>(File.ReadAllText(path))?.Completed != true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Corrupt or unreadable records must remain visible for recovery.
                return true;
            }
        }).Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> Recover(string root, string path, CancellationToken cancellationToken,
        IRenameFileSystem? fileSystem = null)
    {
        fileSystem ??= PhysicalRenameFileSystem.Instance;
        var fullPath = Path.GetFullPath(path);
        if (!FindPending(root).Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The pending rename journal is unavailable.");
        var state = JsonSerializer.Deserialize<JournalState>(File.ReadAllText(fullPath))
            ?? throw new IOException("The pending rename journal is invalid.");
        if (state.Version != 1 || state.Items is null || state.Items.Length < 1 ||
            state.Items.Length > DocumentLimits.MaximumRenameBatchFiles || state.Items.Any(item => item is null))
            throw new IOException("The pending rename journal is invalid.");
        var journal = new RenameTransactionJournal(fullPath, state);
        journal.ResolvePending();
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in journal._state.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(Path.GetFullPath(item.Original));
            if (!currentPaths.Add(item.Current) || !originalPaths.Add(item.Original) ||
                !string.Equals(directory, Path.GetDirectoryName(Path.GetFullPath(item.Current)), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(directory, Path.GetDirectoryName(Path.GetFullPath(item.Target)), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(directory, Path.GetDirectoryName(Path.GetFullPath(item.Stage)), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Fingerprint(item.Current), item.Sha256, StringComparison.Ordinal))
                throw new IOException("A rename source changed; keep the journal for manual recovery.");
        }
        foreach (var item in journal._state.Items)
            if (Directory.Exists(item.Original) || (File.Exists(item.Original) && !currentPaths.Contains(item.Original)))
                throw new IOException("An original filename is occupied; no files were overwritten.");

        // Stage all changed items first, so name swaps are recoverable as well.
        for (var index = 0; index < journal._state.Items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = journal._state.Items[index];
            if (string.Equals(item.Current, item.Original, StringComparison.Ordinal)) continue;
            var stage = Path.Combine(Path.GetDirectoryName(item.Original)!, $".docpivot-recover-{Guid.NewGuid():N}.tmp");
            journal.Move(index, item.Current, stage, fileSystem.MoveFile);
        }
        for (var index = 0; index < journal._state.Items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = journal._state.Items[index];
            if (!string.Equals(item.Current, item.Original, StringComparison.Ordinal))
                journal.Move(index, item.Current, item.Original, fileSystem.MoveFile);
        }
        journal.Complete();
        return journal._state.Items.Select(item => item.Original).ToArray();
    }

    private void ResolvePending()
    {
        if (_state.Pending is not { } pending) return;
        if (pending.Index < 0 || pending.Index >= _state.Items.Length)
            throw new IOException("The pending rename action is invalid.");
        var sourceExists = File.Exists(pending.Source);
        var destinationExists = File.Exists(pending.Destination);
        if (sourceExists == destinationExists)
            throw new IOException("The interrupted move is ambiguous; keep the journal for manual recovery.");
        var current = destinationExists ? pending.Destination : pending.Source;
        _state.Items[pending.Index] = _state.Items[pending.Index] with { Current = current };
        _state = _state with { Pending = null };
    }

    private void Save()
    {
        var staging = _path + ".tmp";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(staging, _path, overwrite: true);
    }

    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record JournalState(int Version, JournalItem[] Items, PendingMove? Pending, bool Completed);
    private sealed record JournalItem(string Original, string Target, string Stage, string Current, string Sha256);
    private sealed record PendingMove(int Index, string Source, string Destination);
}
