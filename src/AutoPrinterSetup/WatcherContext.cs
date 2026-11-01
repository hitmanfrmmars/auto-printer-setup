using System.Diagnostics;
using System.Net.NetworkInformation;

namespace AutoPrinterSetup;

/// <summary>Tray-only mode: re-checks saved printers on a timer and whenever the network changes.</summary>
public sealed class WatcherContext : ApplicationContext
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(15);

    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer periodic = new() { Interval = (int)CheckInterval.TotalMilliseconds };
    readonly System.Windows.Forms.Timer settle = new() { Interval = (int)SettleDelay.TotalMilliseconds };
    bool checking;

    public WatcherContext()
    {
        ui = SynchronizationContext.Current!;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Check printers now", null, async (_, _) => await CheckAsync(userRequested: true));
        menu.Items.Add("Open Auto Printer Setup", null, (_, _) => OpenMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Auto Printer Setup: watching your printers",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenMainWindow();

        periodic.Tick += async (_, _) => await CheckAsync(userRequested: false);
        settle.Tick += async (_, _) =>
        {
            settle.Stop();
            await CheckAsync(userRequested: false);
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        periodic.Start();
        settle.Start();
    }

    void OnNetworkAddressChanged(object? sender, EventArgs e) => ui.Post(_ =>
    {
        settle.Stop();
        settle.Start();
    }, null);

    async Task CheckAsync(bool userRequested)
    {
        if (checking) return;
        checking = true;
        try
        {
            if (!Autostart.IsNeeded)
            {
                Autostart.Sync();
                ExitThread();
                return;
            }

            var results = await PrinterRepair.CheckAllAsync(CancellationToken.None);
            foreach (var result in results.Where(r => r.Outcome is RepairOutcome.Moved or RepairOutcome.Failed))
            {
                var icon = result.Outcome == RepairOutcome.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info;
                tray.ShowBalloonTip(8000, "Auto Printer Setup", result.Message, icon);
            }
            if (userRequested)
            {
                string summary = string.Join("\n", results.Select(r => r.Message));
                tray.ShowBalloonTip(8000, "Auto Printer Setup", summary.Length > 0 ? summary : "No printers to check.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            if (userRequested) tray.ShowBalloonTip(8000, "Auto Printer Setup", $"Check failed: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            checking = false;
        }
    }

    static void OpenMainWindow() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });

    protected override void ExitThreadCore()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        periodic.Dispose();
        settle.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}
