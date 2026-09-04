using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class UserEditWindow : FluentWindow
{
    private readonly UserEditViewModel _vm;

    public UserEditWindow(UserEditViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Closed += (_, _) => Dispatcher.Invoke(Close);
    }

    private void Pwd_Changed(object sender, RoutedEventArgs e) => _vm.Password = PwdBox.Password;
    private void Confirm_Changed(object sender, RoutedEventArgs e) => _vm.ConfirmPassword = ConfirmBox.Password;
}
