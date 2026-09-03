using System.Windows;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class DeviceEditWindow : FluentWindow
{
    public DeviceEditWindow(DeviceEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OnRequestClose += (s, e) => this.Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
