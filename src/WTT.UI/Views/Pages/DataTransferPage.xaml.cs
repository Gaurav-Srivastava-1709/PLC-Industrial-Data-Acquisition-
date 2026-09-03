using System.Windows.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Pages;

public partial class DataTransferPage : Page
{
    public DataTransferPage(DataTransferViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
