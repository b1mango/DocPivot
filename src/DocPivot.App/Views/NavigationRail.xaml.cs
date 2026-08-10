using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DocPivot.App.Views;

public partial class NavigationRail : UserControl
{
    public event EventHandler? SettingsRequested;

    public NavigationRail()
    {
        InitializeComponent();
    }

    private void OnSettingsCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
