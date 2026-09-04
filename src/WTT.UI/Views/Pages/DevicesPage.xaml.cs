using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class DevicesPage : INavigableView<DevicesViewModel>, INavigationAware
{
    public DevicesViewModel ViewModel { get; }

    public DevicesPage(DevicesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.EditRequested += OnEditRequested;
    }

    private void OnEditRequested(object? sender, DeviceEditViewModel vm)
    {
        var dlg = new DeviceEditWindow(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync()
    {
        ViewModel.EditRequested -= OnEditRequested;
        return ViewModel.OnNavigatedFromAsync();
    }
}
