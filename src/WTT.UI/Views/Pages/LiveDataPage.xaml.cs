using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class LiveDataPage : INavigableView<LiveDataViewModel>, INavigationAware
{
    public LiveDataViewModel ViewModel { get; }

    public LiveDataPage(LiveDataViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.WriteRequested += (_, vm) => new WriteTagWindow(vm) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
