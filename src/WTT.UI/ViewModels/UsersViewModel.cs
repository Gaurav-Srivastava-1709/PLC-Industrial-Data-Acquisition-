using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using Wpf.Ui.Controls;

namespace WTT.UI.ViewModels;

public partial class UsersViewModel : ObservableObject
{
    private readonly IUserService _userService;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private ObservableCollection<User> _users = new();

    [ObservableProperty]
    private bool _isLoading;

    public UsersViewModel(IUserService userService, IServiceProvider serviceProvider)
    {
        _userService = userService;
        _serviceProvider = serviceProvider;
    }

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        IsLoading = true;
        try
        {
            var users = await _userService.GetAllUsersAsync();
            Users.Clear();
            foreach (var user in users)
            {
                Users.Add(user);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to load users: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        var editWindow = (Views.Windows.UserEditWindow)_serviceProvider.GetService(typeof(Views.Windows.UserEditWindow))!;
        editWindow.Owner = System.Windows.Application.Current.MainWindow;
        
        if (editWindow.ShowDialog() == true)
        {
            await LoadUsersAsync(); // Reload after add
        }
    }

    [RelayCommand]
    private async Task EditUserAsync(User? user)
    {
        if (user == null) return;

        var editWindow = (Views.Windows.UserEditWindow)_serviceProvider.GetService(typeof(Views.Windows.UserEditWindow))!;
        editWindow.Owner = System.Windows.Application.Current.MainWindow;
        editWindow.SetUserForEdit(user);
        
        if (editWindow.ShowDialog() == true)
        {
            await LoadUsersAsync(); // Reload after edit
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync(User? user)
    {
        if (user == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to delete user '{user.Username}'?",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try
            {
                await _userService.DeleteUserAsync(user.Id);
                Users.Remove(user);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to delete user: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
