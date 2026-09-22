using System.Windows.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class AlarmsPage : Page
{
    public AlarmsPage(AlarmsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
