using System.Diagnostics;
using Microsoft.Win32;

namespace AutoPrinterSetup;

/// <summary>Starts the background watcher at sign-in while at least one printer has auto-repair turned on.</summary>
public static class Autostart
{
    public const string WatchArgument = "--watch";
    public const string WatcherMutexName = @"Local\AutoPrinterSetup.Watcher";

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AutoPrinterSetup";

    public static bool IsNeeded => PrinterStore.Load().Any(p => p.AutoRepair);

    public static void Sync()
    {
        bool needed = IsNeeded;
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (needed) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {WatchArgument}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        if (needed && !IsWatcherRunning())
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, WatchArgument) { UseShellExecute = false });
    }

    static bool IsWatcherRunning()
    {
        if (!Mutex.TryOpenExisting(WatcherMutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }
}
