using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

// INavigationAware lagana zaroori hai — iske bina OnNavigatedToAsync kabhi call nahi hota
// aur page pe data load nahi hota (isliye Refresh dabane pe data aata tha)
public partial class AlarmsPage : Page, INavigableView<AlarmsViewModel>, INavigationAware
{
    public AlarmsViewModel ViewModel { get; }

    public AlarmsPage(AlarmsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // Jab user Alarms page pe aaye, turant data load ho — bina Refresh ke
    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    // Jab user doosre page pe jaaye, auto-refresh timer band ho
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
