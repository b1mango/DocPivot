using System.Windows.Controls;
using DocPivot.App.ViewModels;

namespace DocPivot.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
    }

    private void OnPdfPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.PdfPassword = passwordBox.Password;
        }
    }
}
