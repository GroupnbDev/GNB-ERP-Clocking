using System.Runtime.InteropServices;
using Gnb.Clocking.App.Platforms.Windows;
using Microsoft.UI.Dispatching;

namespace Gnb.Clocking.App.WinUI;

/// <summary>
/// Replaces the XAML-generated entry point (<c>DISABLE_XAML_GENERATED_MAIN</c> in the csproj) so the
/// <c>--supervisor</c> watcher returns before XAML, WinUI and MAUI start. The watcher used to block inside
/// <see cref="App"/>'s constructor, keeping a second full UI runtime resident next to the kiosk.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        if (KioskSupervisor.WatchIfSupervisor())
            return;

        // Same steps as the generated Main.
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
