using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace Gnb.Clocking.App;

/// <summary>
/// The RFID reader types into the hidden badge field. On Mac that field is not the
/// keyboard target after launch, so a scan does nothing until the window is clicked and Tab lands on it.
/// </summary>
public static class BadgeEntrySetup
{
    private static UITextField? _field;
    private static bool _watching;
    private static int _attempts;

    public static void Configure()
    {
        EntryHandler.Mapper.AppendToMapping("GroupNbBadge", (handler, view) =>
        {
            UITextField field = handler.PlatformView;
            field.BorderStyle = UITextBorderStyle.None;
            field.BackgroundColor = UIColor.Clear;
            field.AutocorrectionType = UITextAutocorrectionType.No;
            field.SpellCheckingType = UITextSpellCheckingType.No;
            if (_field != null)
                _field.EditingDidEnd -= OnEditingEnded;
            _field = field;
            field.EditingDidEnd += OnEditingEnded;
            ClaimSoon();
        });

        if (_watching)
            return;
        _watching = true;
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification, _ => ClaimSoon());
        NSNotificationCenter.DefaultCenter.AddObserver(UIWindow.DidBecomeKeyNotification, _ => ClaimSoon());
    }

    private static void OnEditingEnded(object? sender, EventArgs e) => ClaimSoon();

    private static void ClaimSoon()
    {
        _attempts = 0;
        DispatchQueue.MainQueue.DispatchAsync(Claim);
    }

    private static void Claim()
    {
        UITextField? field = _field;
        if (field == null || field.IsFirstResponder)
            return;
        if (_attempts++ > 30)
            return;

        ActivateApp();

        UIWindow? window = field.Window ?? KeyWindow();
        if (window == null)
        {
            Retry();
            return;
        }

        if (!window.IsKeyWindow)
            window.MakeKeyAndVisible();

        if (!field.BecomeFirstResponder())
            Retry();
    }

    private static void Retry() =>
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromMilliseconds(200)), Claim);

    private static UIWindow? KeyWindow()
    {
        foreach (UIScene scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene)
                continue;
            foreach (UIWindow window in windowScene.Windows)
            {
                if (window.IsKeyWindow)
                    return window;
            }

            if (windowScene.Windows.Length > 0)
                return windowScene.Windows[0];
        }

        return null;
    }

    /// <summary>Bring GroupNB Clock in front so the reader does not need a click first.</summary>
    private static void ActivateApp()
    {
        try
        {
            IntPtr appClass = Class.GetHandle("NSApplication");
            if (appClass == IntPtr.Zero)
                return;
            IntPtr shared = IntPtr_objc_msgSend(appClass, Selector.GetHandle("sharedApplication"));
            if (shared == IntPtr.Zero)
                return;
            void_objc_msgSend_bool(shared, Selector.GetHandle("activateIgnoringOtherApps:"), true);
        }
        catch (Exception)
        {
            // Window key + first responder still covers the Tab step when activation is unavailable.
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_bool(IntPtr receiver, IntPtr selector, bool arg);
}
