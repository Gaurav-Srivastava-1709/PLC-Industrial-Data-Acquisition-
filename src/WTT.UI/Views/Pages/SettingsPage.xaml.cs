using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class SettingsPage : INavigableView<SettingsViewModel>, INavigationAware
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Controls like NumberBox/Toggle consume the mouse wheel,
        // so forward the wheel to the parent ScrollViewer to allow scrolling to the About section.
        SettingsScrollViewer.PreviewMouseWheel += (_, e) =>
        {
            SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        };
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
