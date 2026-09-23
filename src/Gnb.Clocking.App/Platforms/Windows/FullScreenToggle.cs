using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKey = global::Windows.System.VirtualKey;

namespace Gnb.Clocking.App.Platforms.Windows;

// F11 toggles the kiosk window between full screen and a normal window, like a browser.
// The key handler is registered with handledEventsToo so it still fires while the badge
// Entry (a TextBox) has focus and would otherwise swallow the key.
public static class FullScreenToggle
{
    public static void Attach(Microsoft.UI.Xaml.Window window)
    {
        if (window.Content is UIElement content)
        {
            Hook(window, content);
            return;
        }

        // MAUI assigns the window content after OnWindowCreated, so wait for first activation.
        void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (window.Content is not UIElement root)
            {
                return;
            }

            window.Activated -= OnActivated;
            Hook(window, root);
        }

        window.Activated += OnActivated;
    }

    private static void Hook(Microsoft.UI.Xaml.Window window, UIElement root)
    {
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.F11)
            {
                return;
            }

            Toggle(window.AppWindow);
            args.Handled = true;
        }), handledEventsToo: true);
    }

    private static void Toggle(AppWindow appWindow)
    {
        var target = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Overlapped
            : AppWindowPresenterKind.FullScreen;

        appWindow.SetPresenter(target);
    }
}
