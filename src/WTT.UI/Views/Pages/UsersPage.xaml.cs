using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class UsersPage : INavigableView<UsersViewModel>, INavigationAware
{
    public UsersViewModel ViewModel { get; }

    public UsersPage(UsersViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.EditRequested += (_, vm) => new UserEditWindow(vm) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
