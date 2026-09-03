using System.Windows.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class LiveDataPage : Page
{
    private readonly LiveDataViewModel _viewModel;

    public LiveDataPage(LiveDataViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }
}
