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
    private static long _lastCheckAt;
    private static long _lastSpawnAt;

    private static TimeSpan? Since(long timestamp, long now) =>
        timestamp == 0 ? null : Stopwatch.GetElapsedTime(timestamp, now);

    public static bool Stopping { get; private set; }

    public static bool WatchIfSupervisor()
    {
        if (!IsSupervisorCommand(out var pid))
            return false;

        Watch(pid);
        Environment.Exit(0);
        return true;
    }

    /// <summary>
    /// Called from start-up and every camera watchdog tick; <see cref="SupervisorSpawnPolicy"/> keeps it to a
    /// mutex check every 30 s and at most one launch a minute, so a slow-starting watcher is not launched again.
    /// </summary>
    public static void EnsureWatchingThisProcess()
    {
        if (Debugger.IsAttached || Environment.ProcessPath == null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!SupervisorSpawnPolicy.ShouldCheck(Since(Interlocked.Read(ref _lastCheckAt), now)))
            return;

        Interlocked.Exchange(ref _lastCheckAt, now);
        if (SupervisorIsRunning())
            return;

        if (!SupervisorSpawnPolicy.MaySpawn(Since(Interlocked.Read(ref _lastSpawnAt), now)))
            return;

        Interlocked.Exchange(ref _lastSpawnAt, now);
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

    /// <summary>
    /// A new launch is a request to run. A stop file left from Ctrl+Shift+Q must not make the
    /// watcher refuse the next crash recovery.
    /// </summary>
    public static void ClearStopRequest()
    {
        try
        {
            File.Delete(StopPath());
        }
        catch (Exception)
        {
            // The watcher still honors a file that could not be removed.
        }
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
            if (ShouldStandDown())
            {
                process?.Dispose();
                TryDeleteStop();
                return;
            }

            if (process == null || HasExited(process))
            {
                process?.Dispose();
                process = StartWorker(exe, directory);
                if (process == null)
                {
                    fastExits++;
                    var delay = KioskRestartPolicy.NextDelay(TimeSpan.Zero, fastExits);
                    AppendLog("restart failed; retry in " + delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s");
                    if (!Pause(delay))
                        return;

                    continue;
                }
            }

            var watchedSince = Stopwatch.GetTimestamp();
            var killed = false;
            while (!HasExited(process))
            {
                if (ShouldStandDown())
                {
                    process.Dispose();
                    TryDeleteStop();
                    return;
                }

                try
                {
                    if (process.WaitForExit(5_000))
                        break;
                }
                catch (Exception ex)
                {
                    AppendLog("wait failed: " + ex.Message);
                    process.Dispose();
                    process = null;
                    break;
                }

                if (!IsUnresponsive(process))
                    continue;

                AppendLog(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:o} kiosk stopped answering; restarting"));
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    AppendLog("kill failed: " + ex.Message);
                }

                try
                {
                    process.WaitForExit(10_000);
                }
                catch (Exception)
                {
                    // Fall through and only relaunch when the process is actually gone.
                }

                if (!HasExited(process))
                {
                    AppendLog("kiosk was still running after kill");
                    continue;
                }

                killed = true;
                break;
            }

            if (process == null)
            {
                if (!Pause(TimeSpan.FromSeconds(2)))
                    return;

                continue;
            }

            var uptime = ProcessUptime(process, watchedSince);
            var code = 0;
            try
            {
                code = killed ? -1 : process.ExitCode;
            }
            catch (Exception)
            {
                code = -1;
            }

            process.Dispose();
            process = null;
            if (ShouldStandDown())
            {
                AppendLog(FormLine(code, uptime, TimeSpan.Zero, stopped: true));
                TryDeleteStop();
                return;
            }

            if (uptime >= KioskRestartPolicy.HealthyUptime)
                fastExits = 0;
            else
                fastExits++;

            var restartDelay = KioskRestartPolicy.NextDelay(uptime, fastExits);
            AppendLog(FormLine(code, uptime, restartDelay, stopped: false));
            if (!Pause(restartDelay))
                return;
        }
    }

    public static void Note(string line) => AppendLog(line);

    private static bool ShouldStandDown() => IsWindowsShuttingDown() || File.Exists(StopPath());

    private static bool Pause(TimeSpan delay)
    {
        var left = delay;
        while (left > TimeSpan.Zero)
        {
            if (ShouldStandDown())
            {
                TryDeleteStop();
                return false;
            }

            var slice = left > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
            Thread.Sleep(slice);
            left -= slice;
        }

        return !ShouldStandDown();
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsUnresponsive(Process process)
    {
        TimeSpan uptime;
        try
        {
            uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return false;
        }

        var beat = KioskHeartbeat.ReadUtc();
        TimeSpan? age = beat == null ? null : DateTimeOffset.UtcNow - beat.Value;
        return KioskLivenessPolicy.IsUnresponsive(uptime, age);
    }

    private static TimeSpan ProcessUptime(Process process, long watchedSince)
    {
        try
        {
            var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            if (uptime > TimeSpan.Zero)
                return uptime;
        }
        catch (Exception)
        {
            // StartTime is unavailable for some exits. The time spent waiting is the fallback.
        }

        return Stopwatch.GetElapsedTime(watchedSince);
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
