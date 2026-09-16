using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class AdminEnrollmentWindow : Window
{
    private readonly AdminEnrollmentViewModel _viewModel;

    public AdminEnrollmentWindow(AdminEnrollmentViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.Completed += OnCompleted;
        Closed += (_, _) => _viewModel.Completed -= OnCompleted;
    }

    private void Password_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = ((PasswordBox)sender).Password;
    private void ConfirmPassword_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ConfirmPassword = ((PasswordBox)sender).Password;
    private void OnCompleted(object? sender, EventArgs e) { DialogResult = true; Close(); }
}
