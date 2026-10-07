using System.Windows;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly MainViewModel _vm;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider,
        IContentDialogService dialogService,
        ISnackbarService snackbarService,
        ILogger<MainWindow> logger)
    {
        _vm = viewModel;
        _navigation = navigationService;
        _logger = logger;
        DataContext = viewModel;
        InitializeComponent();

        RootNavigation.SetPageProviderService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);
        dialogService.SetDialogHost(DialogHost);
        snackbarService.SetSnackbarPresenter(SnackbarHost);

        viewModel.ChangePasswordRequested += OnChangePasswordRequested;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            viewModel.ChangePasswordRequested -= OnChangePasswordRequested;
            viewModel.Dispose(); // Clean up timer + event subscriptions
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.TitleBarTint.ApplySlate();
        _logger.LogInformation("MainWindow loaded — navigating to DashboardPage.");
        var ok = _navigation.Navigate(typeof(Pages.DashboardPage));
        _logger.LogInformation("Navigate to DashboardPage returned {Result}.", ok);
    }

    private void OnChangePasswordRequested(object? sender, ChangePasswordViewModel vm)
    {
        var dialog = new ChangePasswordWindow(vm) { Owner = this };
        dialog.ShowDialog();
    }
}
