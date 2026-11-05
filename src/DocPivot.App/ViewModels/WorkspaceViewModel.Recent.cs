using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DocPivot.Infrastructure.Preferences;

namespace DocPivot.App.ViewModels;

public partial class WorkspaceViewModel
{
    public ObservableCollection<RecentOutputItemViewModel> RecentOutputs { get; } = [];

    public bool HasRecentOutputs => RecentOutputs.Count > 0;

    private void InitializeRecentOutputs()
    {
        RecentOutputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentOutputs));
        foreach (var output in (_preferencesStore?.Load().RecentOutputs ?? [])
            .OrderByDescending(output => output.CreatedAt)
            .DistinctBy(output => output.Path, StringComparer.OrdinalIgnoreCase)
            .Take(20))
        {
            RecentOutputs.Add(new(output, _timeProvider));
        }
    }

    private void RecordRecentOutputs()
    {
        if (IsBatchRenameTool) return;
        var firstPath = _completedOutputPaths[0];
        var isFolder = _completedOutputPaths.Count > 1;
        var path = isFolder ? Path.GetDirectoryName(firstPath)! : firstPath;
        if (isFolder)
        {
            foreach (var outputPath in _completedOutputPaths.Skip(1))
            {
                var directory = Path.GetDirectoryName(outputPath)!;
                while (!string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) &&
                    !directory.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = Path.GetDirectoryName(path)!;
                }
            }
        }
        var displayName = isFolder
            ? $"{Path.GetFileName(firstPath)} 等 {_completedOutputPaths.Count} 个"
            : Path.GetFileName(firstPath);
        var existing = RecentOutputs.FirstOrDefault(output =>
            string.Equals(output.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentOutputs.Remove(existing);
        RecentOutputs.Insert(0, new(new(path, displayName, _timeProvider.GetLocalNow(), isFolder), _timeProvider));
        while (RecentOutputs.Count > 20) RecentOutputs.RemoveAt(RecentOutputs.Count - 1);
        SaveRecentOutputs();
    }

    private void SaveRecentOutputs()
    {
        if (_preferencesStore is null) return;
        _preferencesStore.Save(_preferencesStore.Load() with
        {
            RecentOutputs = RecentOutputs.Select(output =>
                new RecentOutput(output.Path, output.DisplayName, output.CreatedAt, output.IsFolder)).ToArray(),
        });
    }

    [RelayCommand]
    private void OpenRecentOutput(RecentOutputItemViewModel item)
    {
        if (!CheckRecentOutputExists(item)) return;
        if (item.IsFolder) _shellService.OpenFolder(item.Path);
        else _shellService.OpenFile(item.Path);
    }

    [RelayCommand]
    private void RevealRecentOutput(RecentOutputItemViewModel item)
    {
        if (!CheckRecentOutputExists(item)) return;
        if (item.IsFolder) _shellService.OpenFolder(item.Path);
        else _shellService.RevealInFolder(item.Path);
    }

    private bool CheckRecentOutputExists(RecentOutputItemViewModel item)
    {
        if (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path)) return true;
        RemoveRecentOutput(item);
        StatusMessage = $"文件已不存在：{item.DisplayName}";
        return false;
    }

    [RelayCommand]
    private void RemoveRecentOutput(RecentOutputItemViewModel item)
    {
        RecentOutputs.Remove(item);
        SaveRecentOutputs();
    }

    public void RefreshRecentOutputTimes()
    {
        foreach (var output in RecentOutputs) output.RefreshRelativeTime();
    }
}
