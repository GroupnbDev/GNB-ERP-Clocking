using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.App.Platforms.Windows;

/// <summary>
/// A second copy of this exe watches the window. Managed faults are swallowed in
/// <see cref="App"/>, but a native fault still ends the process with no managed stack.
/// The watcher opens the kiosk again. Ctrl+Shift+Q writes a stop file so an update can replace the exe.
/// </summary>
internal static class KioskSupervisor
{
    private const string MutexName = @"Local\GroupNB.Clock.Supervisor";
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;
    private const int ExceptionContinueSearch = 0;
    private const int SmShuttingDown = 0x2000;

    private static readonly NativeFilterDelegate NativeFilter = OnNativeFault;
    private static Mutex? _mutex;

    public static bool Stopping { get; private set; }

    public static bool WatchIfSupervisor()
    {
        if (!IsSupervisorCommand(out var pid))
            return false;

        Watch(pid);
        Environment.Exit(0);
        return true;
    }

    public static void EnsureWatchingThisProcess()
    {
        if (Debugger.IsAttached || Environment.ProcessPath == null)
            return;

        if (SupervisorIsRunning())
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                Arguments = "--supervisor " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            AppendLog("watcher did not start: " + ex.Message);
        }
    }

    public static void RequestStop()
    {
        Stopping = true;
        try
        {
            var path = StopPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppendLog("stop file was not written: " + ex.Message);
        }

        Environment.Exit(0);
    }

    public static bool IsWindowsShuttingDown()
    {
        try
        {
            return GetSystemMetrics(SmShuttingDown) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SuppressCrashDialog()
    {
        _ = SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        _ = SetUnhandledExceptionFilter(Marshal.GetFunctionPointerForDelegate(NativeFilter));
    }

    private static void Watch(int pid)
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog("watcher mutex failed: " + ex.Message);
            return;
        }

        SuppressCrashDialog();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;

        var directory = Path.GetDirectoryName(exe) ?? "";
        Process? process = null;
        try
        {
            if (pid > 0)
                process = Process.GetProcessById(pid);
        }
        catch (Exception)
        {
            process = null;
        }

        var fastExits = 0;
        while (true)
        {
            if (IsWindowsShuttingDown())
            {
                process?.Dispose();
                return;
            }

            if (process == null)
            {
                process = StartWorker(exe, directory);
                if (process == null)
                    return;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                AppendLog("wait failed: " + ex.Message);
                process.Dispose();
                return;
            }

            var uptime = Stopwatch.GetElapsedTime(started);
            var code = 0;
            try
            {
                code = process.ExitCode;
            }
            catch (Exception)
            {
                code = -1;
            }

            process.Dispose();
            process = null;
            if (File.Exists(StopPath()) || IsWindowsShuttingDown())
            {
                AppendLog(FormLine(code, uptime, TimeSpan.Zero, stopped: true));
                TryDeleteStop();
                return;
            }

            if (uptime >= KioskRestartPolicy.HealthyUptime)
                fastExits = 0;
            else
                fastExits++;

            var delay = KioskRestartPolicy.NextDelay(uptime, fastExits);
            AppendLog(FormLine(code, uptime, delay, stopped: false));
            Thread.Sleep(delay);
            if (IsWindowsShuttingDown() || File.Exists(StopPath()))
            {
                TryDeleteStop();
                return;
            }

            process = StartWorker(exe, directory);
            if (process == null)
                return;
        }
    }

    private static Process? StartWorker(string exe, string directory)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = directory,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppendLog("restart failed: " + ex.Message);
            return null;
        }
    }

    private static bool IsSupervisorCommand(out int pid)
    {
        pid = 0;
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--supervisor", StringComparison.OrdinalIgnoreCase))
                continue;

            if (index + 1 < args.Length)
                int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);

            return true;
        }

        return false;
    }

    private static bool SupervisorIsRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string FormLine(int code, TimeSpan uptime, TimeSpan delay, bool stopped)
    {
        var unsigned = unchecked((uint)code);
        var action = stopped ? "stopped" : "restart in " + delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:o} exit {code} (0x{unsigned:X8}) uptime {uptime:hh\\:mm\\:ss} {action}");
    }

    private static void AppendLog(string line)
    {
        try
        {
            var path = LogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // The watcher must keep running even when the disk is full.
        }
    }

    private static void TryDeleteStop()
    {
        try
        {
            File.Delete(StopPath());
        }
        catch (Exception)
        {
            // The next launch treats a leftover stop file as a request to stay down only after an exit.
        }
    }

    private static string StopPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroupNB Clock", "kiosk.stop");

    private static string LogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroupNB Clock", "supervisor.log");

    private static int OnNativeFault(IntPtr exceptionPointers)
    {
        try
        {
            if (exceptionPointers != IntPtr.Zero)
            {
                var record = Marshal.ReadIntPtr(exceptionPointers);
                var code = Marshal.ReadInt32(record);
                AppendLog(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:o} native fault 0x{unchecked((uint)code):X8}"));
            }
        }
        catch (Exception)
        {
            // Falling through lets the process exit so the watcher can open it again.
        }

        return ExceptionContinueSearch;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeFilterDelegate(IntPtr exceptionPointers);
}
