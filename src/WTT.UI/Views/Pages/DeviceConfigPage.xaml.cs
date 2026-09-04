using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class DeviceConfigPage : INavigableView<DeviceConfigViewModel>, INavigationAware
{
    public DeviceConfigViewModel ViewModel { get; }

    public DeviceConfigPage(DeviceConfigViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.TagEditRequested += (_, vm) => Show(new TagEditWindow(vm));
        viewModel.AddressEditRequested += (_, vm) => Show(new AddressEditWindow(vm));
        viewModel.QueryEditRequested += (_, vm) => Show(new QueryEditWindow(vm));
        viewModel.ServerEditRequested += (_, vm) => Show(new TransferServerEditWindow(vm));
    }

    private void Show(Window dialog)
    {
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
