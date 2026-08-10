using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.Infrastructure.Office;

namespace DocPivot.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IOfficeWorkerClient _officeWorkerClient;

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

    public bool IsNotProbing => !IsProbing;

    public string ProbeButtonText => IsProbing ? "检测中…" : "重新检测";

    public SettingsViewModel(IOfficeWorkerClient officeWorkerClient)
    {
        _officeWorkerClient = officeWorkerClient;
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
