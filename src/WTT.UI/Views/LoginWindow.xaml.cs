using System.Windows;
using WTT.UI.ViewModels;

namespace WTT.UI.Views;

public partial class LoginWindow : Wpf.Ui.Controls.FluentWindow
{
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void txtPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.Password = txtPassword.Password;
        }
    }
}
