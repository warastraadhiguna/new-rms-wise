using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;

namespace Pos.Desktop.Tests;

/// <summary>
/// Drives real WPF controls on an STA thread (typing into PasswordBox/TextBox, pressing Enter) and checks
/// that the ViewModel sees it. ViewModel-only tests cannot catch broken bindings.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class UiInteractionTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary>Shows the window off-screen without activating it, so it has a real HWND and input source.</summary>
    private static T ShowOffScreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Control '{name}' not found in {window.GetType().Name}.");

    /// <summary>Clicks like UI automation does (runs the button's command), then lets the dispatcher process it.</summary>
    private static void Click(Button button)
    {
        var invoke = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
        button.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
                found.Add(match);
            found.AddRange(Descendants<T>(child));
        }
        return found;
    }

    private static void PressEnter(UIElement element)
    {
        var source = PresentationSource.FromVisual(element)
                     ?? throw new InvalidOperationException("The element is not shown.");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    [Fact]
    public void Typed_password_reaches_the_view_model_and_enter_logs_in()
    {
        OnSta(() =>
        {
            string? sentPassword = null;
            var api = new FakeNodeApi
            {
                OnLogin = (_, password) => { sentPassword = password; return Samples.Login(); },
                OnCurrentShift = () => Samples.Shift(),
            };
            var navigator = new FakeNavigator();
            var vm = new LoginViewModel(api, new SessionState(), navigator);
            var window = ShowOffScreen(new LoginWindow { DataContext = vm });
            try
            {
                Find<TextBox>(window, "UsernameBox").Text = "kasir1";
                var passwordBox = Find<PasswordBox>(window, "PasswordInput");
                passwordBox.Password = "kasir123";

                Assert.Equal("kasir1", vm.Username);
                Assert.Equal("kasir123", vm.Password);

                PressEnter(passwordBox);

                Assert.Equal("kasir123", sentPassword);
                Assert.Equal(["main"], navigator.Shown);
                Assert.Equal("", passwordBox.Password);   // cleared by the ViewModel, reflected in the control
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Login_window_has_only_username_and_password_and_enter_works_from_username()
    {
        OnSta(() =>
        {
            string? sentPassword = null;
            var api = new FakeNodeApi
            {
                OnLogin = (_, password) => { sentPassword = password; return Samples.Login(); },
                OnCurrentShift = () => null,
            };
            var navigator = new FakeNavigator();
            var vm = new LoginViewModel(api, new SessionState(), navigator);
            var window = ShowOffScreen(new LoginWindow { DataContext = vm });
            try
            {
                // no PIN mode: no radio buttons, exactly one PasswordBox
                Assert.Empty(Descendants<RadioButton>(window));
                Assert.Single(Descendants<PasswordBox>(window));

                Find<PasswordBox>(window, "PasswordInput").Password = "kasir123";
                var username = Find<TextBox>(window, "UsernameBox");
                username.Text = "kasir1";
                PressEnter(username);   // Enter from any box

                Assert.Equal("kasir123", sentPassword);
                Assert.Equal(["open-shift"], navigator.Shown);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Wrong_login_keeps_the_window_and_shows_the_error()
    {
        OnSta(() =>
        {
            var api = new FakeNodeApi
            {
                OnLogin = (_, _) => throw new NodeApiException("Username atau password salah.", "invalid_credentials", 401),
            };
            var vm = new LoginViewModel(api, new SessionState(), new FakeNavigator());
            var window = ShowOffScreen(new LoginWindow { DataContext = vm });
            try
            {
                Find<TextBox>(window, "UsernameBox").Text = "kasir1";
                Find<PasswordBox>(window, "PasswordInput").Password = "salah";
                PressEnter(Find<PasswordBox>(window, "PasswordInput"));

                Assert.Equal("Username atau password salah.", Find<TextBlock>(window, "ErrorText").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Username_has_focus_when_the_login_window_opens()
    {
        OnSta(() =>
        {
            var window = ShowOffScreen(new LoginWindow { DataContext = new LoginViewModel(new FakeNodeApi(), new SessionState(), new FakeNavigator()) });
            try
            {
                Assert.Same(Find<TextBox>(window, "UsernameBox"), FocusManager.GetFocusedElement(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Opening_cash_typed_in_the_box_is_used_and_enter_opens_the_shift()
    {
        OnSta(() =>
        {
            decimal? sentCash = null;
            var api = new FakeNodeApi { OnOpenShift = cash => { sentCash = cash; return Samples.Shift(); } };
            var session = new SessionState();
            session.SetLogin(Samples.Login());
            var navigator = new FakeNavigator();
            var vm = new OpenShiftViewModel(api, session, navigator);
            var window = ShowOffScreen(new OpenShiftWindow { DataContext = vm });
            try
            {
                Assert.Same(Find<TextBox>(window, "OpeningCashBox"), FocusManager.GetFocusedElement(window));

                var box = Find<TextBox>(window, "OpeningCashBox");
                box.Focus();
                foreach (var digit in "250000")
                    Wpf.TypeChar(box, digit.ToString());     // typed like a keyboard: dots appear while typing
                Assert.Equal("250.000", box.Text);
                Assert.Equal("250.000", vm.OpeningCashText);

                PressEnter(box);

                Assert.Equal(250000m, sentCash);
                Assert.Equal(["main"], navigator.Shown);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Cancel_button_on_open_shift_logs_out()
    {
        OnSta(() =>
        {
            var session = new SessionState();
            session.SetLogin(Samples.Login());
            var navigator = new FakeNavigator();
            var window = ShowOffScreen(new OpenShiftWindow { DataContext = new OpenShiftViewModel(new FakeNodeApi(), session, navigator) });
            try
            {
                Click(Find<Button>(window, "CancelButton"));

                Assert.False(session.IsLoggedIn);
                Assert.Equal(["login"], navigator.Shown);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
