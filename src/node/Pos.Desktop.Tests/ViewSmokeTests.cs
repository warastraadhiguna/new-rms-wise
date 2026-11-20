using System.Windows;
using Pos.Desktop.Services;
using Pos.Desktop.ViewModels;
using Pos.Desktop.Views;

namespace Pos.Desktop.Tests;

/// <summary>Loads every window's XAML with its ViewModel (not shown), so XAML mistakes fail here instead of at a store.</summary>
public class ViewSmokeTests
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
            throw new Xunit.Sdk.XunitException($"Window failed to load: {error}");
    }

    [Fact]
    public void All_windows_load_with_their_view_models()
    {
        OnSta(() =>
        {
            var session = new SessionState();
            session.SetLogin(Samples.Login());
            session.Shift = Samples.Shift();
            var api = new FakeNodeApi();
            var navigator = new FakeNavigator();

            Window[] windows =
            [
                new LoginWindow { DataContext = new LoginViewModel(api, session, navigator) },
                new OpenShiftWindow { DataContext = new OpenShiftViewModel(api, session, navigator) },
                new MainWindow { DataContext = new MainViewModel(session) },
            ];

            foreach (var window in windows)
            {
                window.Measure(new Size(1024, 768));
                window.Arrange(new Rect(0, 0, 1024, 768));
                window.Close();
            }
        });
    }
}
