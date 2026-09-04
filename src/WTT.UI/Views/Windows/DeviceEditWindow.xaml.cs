using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class DeviceEditWindow : FluentWindow
{
    public DeviceEditWindow(DeviceEditViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.Closed += (_, _) => Dispatcher.Invoke(Close);
    }
}
