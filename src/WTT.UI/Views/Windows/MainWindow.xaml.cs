using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigation;
    private readonly MainViewModel _vm;

    public MainWindow(
        MainViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider,
        IContentDialogService dialogService,
        ISnackbarService snackbarService)
    {
        _vm = viewModel;
        _navigation = navigationService;
        DataContext = viewModel;
        InitializeComponent();

        RootNavigation.SetPageProviderService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);
        dialogService.SetDialogHost(DialogHost);
        snackbarService.SetSnackbarPresenter(SnackbarHost);

        viewModel.ChangePasswordRequested += OnChangePasswordRequested;
        Loaded += OnLoaded;
        Closed += (_, _) => viewModel.ChangePasswordRequested -= OnChangePasswordRequested;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _navigation.Navigate(typeof(Pages.DashboardPage));

    private void OnChangePasswordRequested(object? sender, ChangePasswordViewModel vm)
    {
        var dialog = new ChangePasswordWindow(vm) { Owner = this };
        dialog.ShowDialog();
    }
}
