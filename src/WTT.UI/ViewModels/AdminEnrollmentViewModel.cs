using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class AdminEnrollmentViewModel : ViewModelBase
{
    private readonly IInitialAdminEnrollmentService _enrollment;

    public AdminEnrollmentViewModel(IInitialAdminEnrollmentService enrollment, IDialogService dialogs, ILogger<AdminEnrollmentViewModel> logger)
        : base(dialogs, logger) => _enrollment = enrollment;

    [ObservableProperty] private string _username = "admin";
    [ObservableProperty] private string _fullName = "System Administrator";
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;

    public event EventHandler? Completed;

    [RelayCommand]
    private async Task EnrollAsync()
    {
        if (Password != ConfirmPassword) { ErrorMessage = "Passwords do not match."; return; }
        var ok = await RunAsync(() => _enrollment.EnrollAsync(new InitialAdminRequest(Username, FullName, Password)), inlineErrors: true);
        if (ok) Completed?.Invoke(this, EventArgs.Empty);
    }
}
