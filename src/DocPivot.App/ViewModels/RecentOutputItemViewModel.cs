using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DocPivot.Infrastructure.Preferences;

namespace DocPivot.App.ViewModels;

public partial class RecentOutputItemViewModel : ObservableObject
{
    private readonly TimeProvider _timeProvider;

    public RecentOutputItemViewModel(RecentOutput output, TimeProvider timeProvider)
    {
        Path = output.Path;
        DisplayName = output.DisplayName;
        CreatedAt = output.CreatedAt;
        IsFolder = output.IsFolder;
        _timeProvider = timeProvider;
        RefreshRelativeTime();
    }

    public string Path { get; }
    public string DisplayName { get; }
    public bool IsFolder { get; }
    public DateTimeOffset CreatedAt { get; }
    public string ToolTip => Path;

    [ObservableProperty]
    private string _relativeTimeText = string.Empty;

    public void RefreshRelativeTime()
    {
        var now = _timeProvider.GetLocalNow();
        var created = TimeZoneInfo.ConvertTime(CreatedAt, _timeProvider.LocalTimeZone);
        var elapsed = now - created;
        var days = (now.Date - created.Date).Days;
        RelativeTimeText = elapsed.TotalMinutes < 1 ? "刚刚"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes} 分钟"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours} 小时"
            : days == 1 ? "昨天"
            : days < 7 ? $"{days} 天"
            : created.ToString("M月d日", CultureInfo.InvariantCulture);
    }
}
