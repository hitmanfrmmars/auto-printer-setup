namespace AutoPrinterSetup;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains(Autostart.WatchArgument, StringComparer.OrdinalIgnoreCase))
        {
            using var mutex = new Mutex(initiallyOwned: true, Autostart.WatcherMutexName, out bool isFirstWatcher);
            if (!isFirstWatcher) return;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Run(new WatcherContext());
            return;
        }

        Application.Run(new MainForm());
    }
}
