using System.Windows;
using System.Windows.Input;
using WTT.UI.ViewModels;

namespace WTT.UI.Views.Windows;

public partial class LoginWindow : Window
{
    public LoginViewModel ViewModel { get; }

    public LoginWindow(LoginViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PasswordChangeRequired += OnPasswordChangeRequired;
        // Deferred so the field really gets the caret once the window is activated.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => UsernameBox.Focus()));
        Closed += (_, _) => viewModel.PasswordChangeRequired -= OnPasswordChangeRequired;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Password = PasswordBox.Password;

    /// <summary>Eye button: shows the password as plain text and back again. UI only — the value still flows through the view model.</summary>
    private void RevealPassword_Click(object sender, RoutedEventArgs e)
    {
        var revealing = PlainPasswordBox.Visibility == Visibility.Collapsed;
        if (revealing)
        {
            PasswordBox.Visibility = Visibility.Collapsed;
            PlainPasswordBox.Visibility = Visibility.Visible;
            EyeShowIcon.Visibility = Visibility.Collapsed;
            EyeHideIcon.Visibility = Visibility.Visible;
            RevealPasswordButton.ToolTip = "Hide password";
            PlainPasswordBox.CaretIndex = PlainPasswordBox.Text.Length;
            PlainPasswordBox.Focus();
        }
        else
        {
            // Copy back so the two fields stay in step the next time the password is revealed.
            PasswordBox.Password = PlainPasswordBox.Text;
            PlainPasswordBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            EyeHideIcon.Visibility = Visibility.Collapsed;
            EyeShowIcon.Visibility = Visibility.Visible;
            RevealPasswordButton.ToolTip = "Show password";
            PasswordBox.Focus();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The window has no OS title bar, so the illustration panel is the drag handle.</summary>
    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        // DragMove is only valid while the mouse is not already captured by another element.
        if (Mouse.Captured is not null) return;
        DragMove();
    }

    private async void OnPasswordChangeRequired(object? sender, ChangePasswordViewModel vm)
    {
        var dialog = new ChangePasswordWindow(vm) { Owner = this };
        dialog.ShowDialog();
        await ViewModel.CompleteForcedChangeAsync(vm.Succeeded);
    }
}
