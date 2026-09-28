using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

// INavigationAware is required — without it OnNavigatedToAsync is never called
// and no data is loaded on the page (which is why data appeared only on Refresh)
public partial class AlarmsPage : Page, INavigableView<AlarmsViewModel>, INavigationAware
{
    public AlarmsViewModel ViewModel { get; }

    public AlarmsPage(AlarmsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // When the user arrives at the Alarms page, data loads immediately — without needing Refresh
    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    // When the user navigates to another page, the auto-refresh timer stops
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
