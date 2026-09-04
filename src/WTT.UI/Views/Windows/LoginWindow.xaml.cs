using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class LoginWindow : FluentWindow
{
    public LoginViewModel ViewModel { get; }

    public LoginWindow(LoginViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PasswordChangeRequired += OnPasswordChangeRequired;
        Loaded += (_, _) => UsernameBox.Focus();
        Closed += (_, _) => viewModel.PasswordChangeRequired -= OnPasswordChangeRequired;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Password = PasswordBox.Password;

    private async void OnPasswordChangeRequired(object? sender, ChangePasswordViewModel vm)
    {
        var dialog = new ChangePasswordWindow(vm) { Owner = this };
        dialog.ShowDialog();
        await ViewModel.CompleteForcedChangeAsync(vm.Succeeded);
    }
}
