using Microsoft.Maui.Handlers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKey = global::Windows.System.VirtualKey;
using WinTextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace Gnb.Clocking.App;

/// <summary>
/// The RFID reader types into the hidden badge field. A packaged .exe does not keep that
/// field as the keyboard target, so a scan is ignored until something focuses it again.
/// This claims it whenever the window is in front, and types into it when focus is elsewhere.
/// </summary>
public static class BadgeEntrySetup
{
    private static WinTextBox? _field;
    private static Entry? _badge;
    private static Microsoft.UI.Xaml.Window? _window;
    private static DispatcherQueueTimer? _timer;
    private static bool _active = true;
    private static bool _keysHooked;

    public static void ClaimSoon()
    {
        DispatcherQueue? queue = _field?.DispatcherQueue ?? _window?.DispatcherQueue;
        queue?.TryEnqueue(Claim);
    }

    public static void AttachWindow(Microsoft.UI.Xaml.Window window)
    {
        _window = window;
        window.Activated += (_, args) =>
        {
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (window.Content is UIElement content)
                HookKeys(content);
            if (_active)
                ClaimSoon();
        };

        if (window.Content is UIElement ready)
            HookKeys(ready);
    }

    public static void Configure()
    {
        EntryHandler.Mapper.AppendToMapping("GroupNbBadge", (handler, view) =>
        {
            WinTextBox field = handler.PlatformView;
            field.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            field.Background = null;
            field.IsSpellCheckEnabled = false;
            field.IsTextPredictionEnabled = false;
            field.AcceptsReturn = false;
            field.IsTabStop = true;
            if (_field != null)
                _field.LostFocus -= OnLostFocus;
            _field = field;
            _badge = view as Entry;
            field.LostFocus += OnLostFocus;
            ClaimSoon();
            Watch();
        });
    }

    private static void HookKeys(UIElement root)
    {
        if (_keysHooked)
            return;
        _keysHooked = true;

        // Before the hidden box sees the key. A focused box and this handler must not both
        // record the same digit, and a box that never receives keys must still record the tap.
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            Entry? badge = _badge;
            if (badge == null)
                return;

            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                return;
            }

            char? character = KeyToChar(args.Key);
            if (character == null)
                return;

            badge.Text = (badge.Text ?? string.Empty) + character.Value;
            args.Handled = true;
        }), handledEventsToo: true);
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e) => ClaimSoon();

    private static void Watch()
    {
        if (_timer != null)
            return;

        DispatcherQueue? queue = _field?.DispatcherQueue;
        if (queue == null)
            return;

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(400);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Claim();
        _timer.Start();
    }

    private static void Claim()
    {
        WinTextBox? field = _field;
        if (field == null || !_active)
            return;
        if (field.FocusState != FocusState.Unfocused)
            return;

        field.Focus(FocusState.Programmatic);
    }

    private static char? KeyToChar(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return (char)('0' + (key - VirtualKey.Number0));
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return (char)('0' + (key - VirtualKey.NumberPad0));
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
            return (char)('A' + (key - VirtualKey.A));
        return null;
    }
}
