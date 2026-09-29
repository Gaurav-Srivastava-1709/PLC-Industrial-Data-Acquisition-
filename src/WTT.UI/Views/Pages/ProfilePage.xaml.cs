using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class ProfilePage : INavigableView<ProfileViewModel>, INavigationAware
{
    public ProfileViewModel ViewModel { get; }

    public ProfilePage(ProfileViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.ChangePasswordRequested += OnChangePasswordRequested;
        Unloaded += (_, _) => ViewModel.ChangePasswordRequested -= OnChangePasswordRequested;
    }

    private void OnChangePasswordRequested(object? sender, ChangePasswordViewModel vm)
    {
        var owner = Window.GetWindow(this);
        var dialog = new ChangePasswordWindow(vm) { Owner = owner };
        dialog.ShowDialog();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
