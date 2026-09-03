using System.Windows.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
