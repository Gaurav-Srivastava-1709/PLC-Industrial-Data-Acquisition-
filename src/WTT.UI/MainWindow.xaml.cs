using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using Wpf.Ui.DependencyInjection;
using WTT.UI.ViewModels;

namespace WTT.UI;

/// <summary>
/// MainWindow: Navigation Shell — Left Sidebar + Content Frame + Title Bar
/// INavigationViewPageProvider lagana zaroori hai WPF-UI 4.x me,
/// bina iske TargetPageType kaam nahi karta (pages blank rehte hain).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(
        IServiceProvider serviceProvider,
        MainViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;

        // MainViewModel se user info aur logout command bind hoga
        DataContext = viewModel;

        // WPF-UI 4.x: PageProvider set karo taki NavigationView pages resolve kar sake DI se
        RootNavigation.SetPageProviderService(pageProvider);

        // NavigationService ko NavigationView control se jodo
        navigationService.SetNavigationControl(RootNavigation);

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // App start hone pe Dashboard page pe navigate karo
        var navigationService = _serviceProvider.GetRequiredService<INavigationService>();
        navigationService.Navigate(typeof(Views.Pages.DashboardPage));
    }
}