using System.Runtime.InteropServices;

namespace Gnb.Clocking.App.Platforms.Windows;

/// <summary>
/// The kiosk stays open overnight. Windows' default "turn off the display after 1 hour" suspends
/// the webcam and the process then exits. This asks Windows to leave the display and the system on.
/// </summary>
internal static class KioskStayAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private static int _refusedLogged;

    public static void Hold()
    {
        var previous = SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        if (previous == 0)
        {
            if (Interlocked.Exchange(ref _refusedLogged, 1) == 0)
                KioskSupervisor.Note("display stay-awake was refused");
        }
        else
        {
            Interlocked.Exchange(ref _refusedLogged, 0);
        }

        KioskSupervisor.EnsureWatchingThisProcess();
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
