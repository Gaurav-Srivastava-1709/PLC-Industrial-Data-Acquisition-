using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WTT.Domain.Exceptions;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

/// <summary>Page lifecycle hooks forwarded by the hosting Page.</summary>
public interface IPageViewModel
{
    Task OnNavigatedToAsync();
    Task OnNavigatedFromAsync();
}

/// <summary>
/// Common plumbing: busy flag, inline error text, safe execution that turns exceptions into
/// user-friendly dialogs (business rules) or logged generic errors (everything else).
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, IPageViewModel
{
    protected ViewModelBase(IDialogService dialogs, ILogger logger)
    {
        Dialogs = dialogs;
        Logger = logger;
    }

    protected IDialogService Dialogs { get; }
    protected ILogger Logger { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Run an operation with busy state and centralised error handling. Returns true on success.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? failureTitle = null, bool inlineErrors = false)
    {
        if (IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WttException ex)
        {
            // Expected, user-actionable problems (validation, permissions, PLC unreachable...).
            if (inlineErrors) ErrorMessage = ex.Message;
            else await Dialogs.ShowErrorAsync(failureTitle ?? "Cannot complete action", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled error in {ViewModel}.", GetType().Name);
            var message = $"An unexpected error occurred: {ex.Message}\n\nDetails have been written to the log file.";
            if (inlineErrors) ErrorMessage = message;
            else await Dialogs.ShowErrorAsync(failureTitle ?? "Unexpected error", message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected static void OnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
