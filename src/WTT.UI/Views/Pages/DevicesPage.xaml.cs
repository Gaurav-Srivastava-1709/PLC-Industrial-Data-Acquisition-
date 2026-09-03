using System.Windows.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class DevicesPage : Page
{
    public DevicesPage(DevicesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
