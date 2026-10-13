using System.Diagnostics;
using Microsoft.Win32;

namespace Gnb.Clocking.App.Platforms.Windows;

/// <summary>
/// A published kiosk opens again when this Windows user signs in, including after a reboot.
/// Debug builds are skipped so a <c>dotnet run</c> does not replace the installed clock.
/// </summary>
internal static class KioskAutoStart
{
    private const string ValueName = "GroupNB Clock";

    public static void RegisterCurrentExe()
    {
        if (Debugger.IsAttached)
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
                return;

            var desired = "\"" + exe + "\"";
            if (string.Equals(key.GetValue(ValueName) as string, desired, StringComparison.OrdinalIgnoreCase))
                return;

            key.SetValue(ValueName, desired);
        }
        catch (Exception ex)
        {
            KioskSupervisor.Note("sign-in start was not registered: " + ex.Message);
        }
    }
}
