using Wpf.Ui.Abstractions.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class TrendsPage : INavigableView<TrendsViewModel>, INavigationAware
{
    public TrendsViewModel ViewModel { get; }
    private Controls.ScadaTrendPlot? _plot;

    public TrendsPage(TrendsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _plot = FindName("TrendPlot") as Controls.ScadaTrendPlot;
            if (_plot is not null)
                _plot.UserTookManualControl += (_, _) => ViewModel.OnUserManualControl();
        };
        ViewModel.ResetViewRequested += (_, _) => _plot?.ResetView();
    }

    public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();
    public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
}
