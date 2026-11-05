using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.App.Services;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Preferences;

namespace DocPivot.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IOfficeWorkerClient _officeWorkerClient;
    private readonly IUserPreferencesStore? _preferencesStore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotProbing))]
    [NotifyPropertyChangedFor(nameof(ProbeButtonText))]
    private bool _isProbing;

    [ObservableProperty]
    private bool _isWordReady;

    [ObservableProperty]
    private string _wordDetail = "检测中…";

    [ObservableProperty]
    private bool _isExcelReady;

    [ObservableProperty]
    private string _excelDetail = "检测中…";

    [ObservableProperty]
    private string _probeSummary = "正在检测本地 Office 环境…";

    private bool _hasCompletedProbe;

    public bool IsNotProbing => !IsProbing;

    public string ProbeButtonText => IsProbing ? "检测中…" : "重新检测";

    public Task EnsureInitialProbeAsync() =>
        _hasCompletedProbe || IsProbing
            ? Task.CompletedTask
            : ProbeAsync();

    public SettingsViewModel(IOfficeWorkerClient officeWorkerClient, IUserPreferencesStore? preferencesStore = null)
    {
        _officeWorkerClient = officeWorkerClient;
        _preferencesStore = preferencesStore;
        ThemeOptions =
        [
            new(AppTheme.Paper, "纸本", "#FBFAF7", "#B0412D"),
            new(AppTheme.Graphite, "石墨", "#1A1B1E", "#D8AD62"),
        ];
        _selectedTheme = ThemeOptions.First(option => option.Theme == ThemeService.Current);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is null)
        {
            return;
        }

        ThemeService.Apply(value.Theme);
        if (_preferencesStore is not null)
        {
            _preferencesStore.Save(_preferencesStore.Load() with { Theme = value.Theme.ToString() });
        }
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (IsProbing)
        {
            return;
        }

        IsProbing = true;
        try
        {
            var status = await _officeWorkerClient.ProbeAsync();
            IsWordReady = status.IsWordReady;
            IsExcelReady = status.IsExcelReady;
            WordDetail = FormatDetail(status, "Word");
            ExcelDetail = FormatDetail(status, "Excel");

            if (status.IsReady && string.IsNullOrWhiteSpace(status.ErrorMessage))
            {
                ProbeSummary = $"检测完成：本机 {status.Product} {status.Version}（{status.Platform}），Word 与 Excel 均已就绪。";
            }
            else if (status.IsReady)
            {
                ProbeSummary = $"检测完成：{status.ErrorMessage}";
            }
            else
            {
                ProbeSummary = $"未检测到可用的 Microsoft Office：{status.ErrorMessage ?? "请安装 Office 后重试"}";
            }
        }
        catch (Exception ex)
        {
            IsWordReady = false;
            IsExcelReady = false;
            WordDetail = "不可用";
            ExcelDetail = "不可用";
            ProbeSummary = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsProbing = false;
            _hasCompletedProbe = true;
        }
    }

    private static string FormatDetail(OfficeEngineStatus status, string component)
    {
        if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
        {
            return "不可用";
        }

        if (component == "Word" && !status.IsWordReady)
        {
            return "未安装";
        }

        if (component == "Excel" && !status.IsExcelReady)
        {
            return "未安装";
        }

        return string.IsNullOrWhiteSpace(status.Version) ? "就绪" : $"v{status.Version}";
    }
}

public sealed record ThemeOption(AppTheme Theme, string Title, string Paper, string Accent);
