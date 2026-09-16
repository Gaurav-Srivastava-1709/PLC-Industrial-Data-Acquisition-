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
        // NumberBox/Toggle jaise controls mouse-wheel kha jate hain,
        // isliye wheel ko parent ScrollViewer ko forward karo taaki About tak scroll ho sake.
        SettingsScrollViewer.PreviewMouseWheel += (_, e) =>
        {
            SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        };
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
