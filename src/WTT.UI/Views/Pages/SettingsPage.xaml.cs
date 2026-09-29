using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;
using WTT.UI.Views.Windows;

namespace WTT.UI.Views.Pages;

public partial class SettingsPage : INavigableView<SettingsViewModel>, INavigationAware
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.ChangePasswordRequested += OnChangePasswordRequested;
        Unloaded += (_, _) => ViewModel.ChangePasswordRequested -= OnChangePasswordRequested;
        // Controls like NumberBox/Toggle consume the mouse wheel,
        // so forward the wheel to the parent ScrollViewer to allow scrolling to the About section.
        SettingsScrollViewer.PreviewMouseWheel += (_, e) =>
        {
            SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        };
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
