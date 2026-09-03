using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace WTT.UI.Views.Windows;

public partial class UserEditWindow : FluentWindow
{
    private readonly ViewModels.UserEditViewModel _viewModel;

    public UserEditWindow(ViewModels.UserEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Wire up close action
        _viewModel.CloseAction = () =>
        {
            DialogResult = _viewModel.DialogResult;
            Close();
        };
    }

    public void SetUserForEdit(Domain.Entities.User user)
    {
        _viewModel.SetUserForEdit(user);
    }
}

public class InverseBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue) return !boolValue;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class PasswordPlaceholderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isEditMode)
        {
            return isEditMode ? "Leave blank to keep current password" : "Enter a secure password";
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
