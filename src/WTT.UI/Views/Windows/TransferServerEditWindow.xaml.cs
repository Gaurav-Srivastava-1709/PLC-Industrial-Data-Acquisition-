using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class TransferServerEditWindow : FluentWindow
{
    private readonly TransferServerEditViewModel _vm;

    public TransferServerEditWindow(TransferServerEditViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Closed += (_, _) => Dispatcher.Invoke(Close);
    }

    private void Pwd_Changed(object sender, RoutedEventArgs e) => _vm.Password = PwdBox.Password;
}
