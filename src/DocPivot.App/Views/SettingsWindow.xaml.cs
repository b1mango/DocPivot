using System.Windows;
using DocPivot.App.ViewModels;
using DocPivot.Infrastructure.Office;

namespace DocPivot.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(IOfficeWorkerClient officeWorkerClient)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(officeWorkerClient);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.ProbeCommand.ExecuteAsync(null);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
