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
    private static bool _watchingLoop;

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
            // Tab and button clicks must not leave the reader field while this app is in front.
            field.ShouldEndEditing = _ =>
                UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active;
            ClaimSoon();
            Watch();
        });

        if (_watching)
            return;
        _watching = true;
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification, _ =>
        {
            ActivateApp();
            ClaimSoon();
        });
        NSNotificationCenter.DefaultCenter.AddObserver(UIWindow.DidBecomeKeyNotification, _ => ClaimSoon());
    }

    /// <summary>Pull the keyboard back after a click, Tab, or returning from another app.</summary>
    public static void ClaimSoon() => DispatchQueue.MainQueue.DispatchAsync(Claim);

    private static void OnEditingEnded(object? sender, EventArgs e) => ClaimSoon();

    private static void Watch()
    {
        if (_watchingLoop)
            return;
        _watchingLoop = true;
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromMilliseconds(200)), Tick);
    }

    private static void Tick()
    {
        if (UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active)
            Claim();
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromMilliseconds(200)), Tick);
    }

    private static void Claim()
    {
        UITextField? field = _field;
        if (field == null)
            return;
        if (UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
            return;

        UIWindow? window = field.Window ?? KeyWindow();
        if (window == null)
            return;

        if (!window.IsKeyWindow)
            window.MakeKeyAndVisible();

        // Mac keyboard focus is the AppKit key view. UIKit can still say this field is
        // first responder after Tab or a theme click moved the real key view away.
        if (!IsAppKitKeyView(field))
        {
            field.BecomeFirstResponder();
            MakeAppKitFirstResponder(field);
        }
    }

    private static bool IsAppKitKeyView(UITextField field)
    {
        IntPtr nsView = NsView(field);
        IntPtr nsWindow = NsWindow(nsView);
        if (nsView == IntPtr.Zero || nsWindow == IntPtr.Zero)
            return field.IsFirstResponder;

        IntPtr first = IntPtr_objc_msgSend(nsWindow, Selector.GetHandle("firstResponder"));
        if (first == nsView)
            return true;

        if (!Responds(nsView, "currentEditor"))
            return false;
        IntPtr editor = IntPtr_objc_msgSend(nsView, Selector.GetHandle("currentEditor"));
        return editor != IntPtr.Zero && first == editor;
    }

    private static void MakeAppKitFirstResponder(UITextField field)
    {
        IntPtr nsView = NsView(field);
        IntPtr nsWindow = NsWindow(nsView);
        if (nsView == IntPtr.Zero || nsWindow == IntPtr.Zero)
            return;

        if (Responds(nsWindow, "makeFirstResponder:"))
            void_objc_msgSend_IntPtr(nsWindow, Selector.GetHandle("makeFirstResponder:"), nsView);
        if (Responds(nsWindow, "setAutorecalculatesKeyViewLoop:"))
            void_objc_msgSend_bool(nsWindow, Selector.GetHandle("setAutorecalculatesKeyViewLoop:"), false);
        if (Responds(nsView, "setNextKeyView:"))
            void_objc_msgSend_IntPtr(nsView, Selector.GetHandle("setNextKeyView:"), nsView);
    }

    private static IntPtr NsView(UIView view)
    {
        if (view.RespondsToSelector(new Selector("nsView")))
            return IntPtr_objc_msgSend(view.Handle, Selector.GetHandle("nsView"));
        if (view.RespondsToSelector(new Selector("_nsView")))
            return IntPtr_objc_msgSend(view.Handle, Selector.GetHandle("_nsView"));
        return IntPtr.Zero;
    }

    private static bool Responds(IntPtr obj, string selector)
    {
        if (obj == IntPtr.Zero)
            return false;
        IntPtr cls = object_getClass(obj);
        return cls != IntPtr.Zero && class_respondsToSelector(cls, Selector.GetHandle(selector));
    }

    private static IntPtr NsWindow(IntPtr nsView)
    {
        if (nsView == IntPtr.Zero)
            return IntPtr.Zero;
        return IntPtr_objc_msgSend(nsView, Selector.GetHandle("window"));
    }

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

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_respondsToSelector(IntPtr cls, IntPtr sel);
}
