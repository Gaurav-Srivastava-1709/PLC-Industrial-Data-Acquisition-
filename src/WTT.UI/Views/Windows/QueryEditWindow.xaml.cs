using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class QueryEditWindow : FluentWindow
{
    private readonly QueryEditViewModel _vm;

    public QueryEditWindow(QueryEditViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Closed += (_, _) => Dispatcher.Invoke(Close);
    }

    private void Pwd_Changed(object sender, RoutedEventArgs e) => _vm.Password = PwdBox.Password;
}
