using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Pos.Desktop.Tests;

/// <summary>Helpers to drive real WPF controls in tests (STA thread, off-screen windows, keys, typing, clicks).</summary>
public static class Wpf
{
    public static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary>Shows the window off-screen without activating it, so it has a real HWND and input source.</summary>
    public static T ShowOffScreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowState = WindowState.Normal;
        window.Width = 1280;
        window.Height = 720;
        window.Show();
        window.UpdateLayout();
        DoEvents();
        return window;
    }

    /// <summary>Hosts a control in an off-screen window.</summary>
    public static Window Host(FrameworkElement content) => ShowOffScreen(new Window { Content = content });

    public static T Find<T>(FrameworkElement scope, string name) where T : FrameworkElement =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"Control '{name}' not found in {scope.GetType().Name}.");

    /// <summary>Like a real key press: PreviewKeyDown (tunnelling), then KeyDown (bubbling) unless handled.</summary>
    public static void PressKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element) ?? throw new InvalidOperationException("The element is not shown.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(preview);
        if (!preview.Handled)
            element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        DoEvents();
    }

    /// <summary>Types a character the way the keyboard does (PreviewTextInput, then TextInput).</summary>
    public static void TypeChar(UIElement element, string text)
    {
        var composition = new TextComposition(InputManager.Current, element, text);
        var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        element.RaiseEvent(preview);
        if (!preview.Handled)
            element.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
        DoEvents();
    }

    /// <summary>Clicks like UI automation does (runs the button's command).</summary>
    public static void Click(ButtonBase button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        DoEvents();
    }

    public static void Toggle(CheckBox box)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(box);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
        DoEvents();
    }

    /// <summary>Lets the dispatcher run queued work (bindings, async continuations, automation invokes).</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public static bool HasFocus(UIElement element) =>
        ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)), element);
}
