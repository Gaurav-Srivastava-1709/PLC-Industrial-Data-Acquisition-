using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class ChangePasswordWindow : FluentWindow
{
    private readonly ChangePasswordViewModel _vm;

    public ChangePasswordWindow(ChangePasswordViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Completed += (_, _) => Dispatcher.Invoke(Close);
        Loaded += (_, _) => CurrentBox.Focus();
    }

    private void Current_Changed(object sender, RoutedEventArgs e) => _vm.CurrentPassword = CurrentBox.Password;
    private void New_Changed(object sender, RoutedEventArgs e) => _vm.NewPassword = NewBox.Password;
    private void Confirm_Changed(object sender, RoutedEventArgs e) => _vm.ConfirmPassword = ConfirmBox.Password;
}
