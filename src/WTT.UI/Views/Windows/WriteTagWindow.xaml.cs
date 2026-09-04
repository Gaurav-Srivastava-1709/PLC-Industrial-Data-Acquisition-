using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class WriteTagWindow : FluentWindow
{
    public WriteTagWindow(WriteTagViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.Closed += (_, _) => Dispatcher.Invoke(Close);
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }
}
