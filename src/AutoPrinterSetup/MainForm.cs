using System.Diagnostics;

namespace AutoPrinterSetup;

public sealed class MainForm : Form
{
    sealed record DriverChoice(string Label, string? DriverName, bool UseIppClassDriver)
    {
        public override string ToString() => Label;
    }

    readonly Button scanButton = new() { Text = "Scan network", AutoSize = true };
    readonly ProgressBar progressBar = new() { Width = 200, Visible = false, Margin = new Padding(8, 6, 8, 3) };
    readonly Label statusLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    readonly ListView printerList = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    readonly TextBox nameBox = new() { Dock = DockStyle.Fill };
    readonly ComboBox driverBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button findDriverButton = new() { Text = "Find driver online", AutoSize = true };
    readonly Label driverHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(720, 0) };
    readonly CheckBox autoRepairBox = new() { Text = "Fix this printer automatically if its network address changes (runs in the background)", AutoSize = true, Checked = true };
    readonly Button addButton = new() { Text = "Add printer", AutoSize = true };
    readonly Button testButton = new() { Text = "Print test page", AutoSize = true };
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };

    List<string> drivers = [];
    List<InstalledPrinter> installed = [];
    CancellationTokenSource? scanCts;
    bool busy;

    public MainForm()
    {
        Text = "Auto Printer Setup";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(940, 660);
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;

        printerList.Columns.Add("Printer", 200);
        printerList.Columns.Add("Model", 190);
        printerList.Columns.Add("Address", 110);
        printerList.Columns.Add("Connection", 150);
        printerList.Columns.Add("Status", 240);

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        top.Controls.AddRange([scanButton, progressBar, statusLabel]);

        var driverRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        driverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        driverRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        driverRow.Controls.Add(driverBox, 0, 0);
        driverRow.Controls.Add(findDriverButton, 1, 0);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        actions.Controls.AddRange([addButton, testButton]);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 8) };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.Controls.Add(MakeLabel("Name in Windows:"), 0, 0);
        details.Controls.Add(nameBox, 1, 0);
        details.Controls.Add(MakeLabel("Driver:"), 0, 1);
        details.Controls.Add(driverRow, 1, 1);
        details.Controls.Add(driverHint, 1, 2);
        details.Controls.Add(autoRepairBox, 1, 3);
        details.Controls.Add(actions, 1, 4);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(printerList, 0, 1);
        root.Controls.Add(details, 0, 2);
        root.Controls.Add(MakeLabel("Activity:"), 0, 3);
        root.Controls.Add(logBox, 0, 4);
        Controls.Add(root);

        scanButton.Click += async (_, _) => await ScanAsync();
        printerList.SelectedIndexChanged += (_, _) => ShowSelectedPrinter();
        addButton.Click += async (_, _) => await AddSelectedPrinterAsync();
        testButton.Click += async (_, _) =>
        {
            if (Selected is { } p && PrinterInstaller.FindInstalled(installed, p) is { } existing)
                await PrintTestPageAsync(existing.Name);
        };
        findDriverButton.Click += (_, _) => OpenDriverSearch();
        Shown += async (_, _) => await LoadAndScanAsync();
        FormClosing += (_, _) => scanCts?.Cancel();

        ShowSelectedPrinter();
    }

    DiscoveredPrinter? Selected => printerList.SelectedItems.Count > 0 ? printerList.SelectedItems[0].Tag as DiscoveredPrinter : null;

    static Label MakeLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) };

    async Task LoadAndScanAsync()
    {
        Log("Reading the printers and drivers installed on this PC...");
        await RefreshInstalledAsync();
        Log($"Found {installed.Count} printer(s) and {drivers.Count} printer driver(s).");
        await ScanAsync();
    }

    async Task RefreshInstalledAsync()
    {
        var driversTask = DriverMatcher.GetInstalledDriversAsync();
        var printersTask = PrinterInstaller.GetInstalledPrintersAsync();
        drivers = await driversTask;
        installed = await printersTask;
    }

    async Task ScanAsync()
    {
        if (scanCts != null)
        {
            scanCts.Cancel();
            return;
        }

        var networks = NetworkScanner.GetLocalNetworks();
        if (networks.Count == 0)
        {
            statusLabel.Text = "This PC isn't connected to a network.";
            Log("No connected network found.");
            return;
        }

        scanCts = new CancellationTokenSource();
        printerList.Items.Clear();
        ShowSelectedPrinter();
        scanButton.Text = "Stop";
        progressBar.Value = 0;
        progressBar.Visible = true;
        Log($"Scanning {string.Join(", ", networks)} for printers (ports 631, 9100 and 515)...");

        var progress = new Progress<ScanProgress>(p =>
        {
            progressBar.Maximum = Math.Max(1, p.Total);
            progressBar.Value = Math.Min(p.Done, p.Total);
            statusLabel.Text = $"Checked {p.Done} of {p.Total} addresses, {printerList.Items.Count} printer(s) found";
        });
        var found = new Progress<DiscoveredPrinter>(p =>
        {
            AddListItem(p);
            Log($"Found {p.DisplayName} at {p.Host} ({p.ConnectionLabel}).");
        });

        try
        {
            var printers = await NetworkScanner.ScanAsync(progress, found, scanCts.Token);
            statusLabel.Text = printers.Count == 0
                ? "No printers found. Check the printer is on and connected to the same network."
                : $"Found {printers.Count} printer(s). Select one to add it to Windows.";
            Log($"Scan finished: {printers.Count} printer(s).");
            if (printerList.Items.Count > 0 && printerList.SelectedItems.Count == 0) printerList.Items[0].Selected = true;
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Scan stopped.";
            Log("Scan stopped.");
        }
        finally
        {
            scanCts.Dispose();
            scanCts = null;
            scanButton.Text = "Scan network";
            progressBar.Visible = false;
        }
    }

    void AddListItem(DiscoveredPrinter p)
    {
        var item = new ListViewItem([p.DisplayName, p.Model, p.Host, p.ConnectionLabel, ""]) { Tag = p };
        printerList.Items.Add(item);
        UpdateStatusColumn(item);
    }

    void UpdateStatusColumn(ListViewItem item)
    {
        var p = (DiscoveredPrinter)item.Tag!;
        var existing = PrinterInstaller.FindInstalled(installed, p);
        item.SubItems[4].Text = existing != null ? $"Added as \"{existing.Name}\"" : "Not added yet";
    }

    void ShowSelectedPrinter()
    {
        var p = Selected;
        driverBox.Items.Clear();
        driverHint.Text = "";
        if (p == null)
        {
            nameBox.Text = "";
            UpdateButtons();
            return;
        }

        var existing = PrinterInstaller.FindInstalled(installed, p);
        nameBox.Text = existing?.Name ?? $"{p.DisplayName} (Network)";
        autoRepairBox.Checked = existing == null || PrinterStore.Find(existing.Name)?.AutoRepair != false;

        DriverChoice? ippClass = p is { Protocol: PrinterProtocol.Ipp, NeedsVendorDriver: false }
            ? new DriverChoice("Windows built-in IPP driver (no download needed)", null, true)
            : null;
        if (ippClass != null) driverBox.Items.Add(ippClass);
        foreach (var driver in drivers) driverBox.Items.Add(new DriverChoice(driver, driver, false));

        string? match = DriverMatcher.FindBestMatch(p, drivers);
        if (existing?.DriverName is { } current && SelectDriver(current))
        {
            driverHint.Text = "This printer is already added. Click \"Re-add printer\" to set it up again.";
        }
        else if (match != null && SelectDriver(match))
        {
            driverHint.Text = $"The installed driver \"{match}\" matches this printer's model.";
        }
        else if (ippClass != null)
        {
            driverBox.SelectedItem = ippClass;
            driverHint.Text = "This printer supports driverless printing, so no download is needed.";
        }
        else
        {
            string fallback = DriverMatcher.FindFallback(p);
            if (!SelectDriver(fallback))
            {
                var choice = new DriverChoice($"{fallback} (included with Windows)", fallback, false);
                driverBox.Items.Insert(0, choice);
                driverBox.SelectedItem = choice;
            }
            string model = p.Model.Length > 0 ? p.Model : "this printer";
            driverHint.Text = $"No driver for {model} is installed, so a generic one was picked. Basic printing should work. " +
                              "For all features, install the manufacturer's driver (\"Find driver online\"), then scan again.";
        }

        UpdateButtons();
    }

    bool SelectDriver(string driverName)
    {
        var choice = driverBox.Items.OfType<DriverChoice>()
            .FirstOrDefault(c => string.Equals(c.DriverName, driverName, StringComparison.OrdinalIgnoreCase));
        if (choice == null) return false;
        driverBox.SelectedItem = choice;
        return true;
    }

    void UpdateButtons()
    {
        var p = Selected;
        bool hasPrinter = p != null && !busy;
        bool isInstalled = p != null && PrinterInstaller.FindInstalled(installed, p) != null;
        scanButton.Enabled = !busy;
        nameBox.Enabled = driverBox.Enabled = autoRepairBox.Enabled = hasPrinter;
        addButton.Enabled = hasPrinter;
        addButton.Text = isInstalled ? "Re-add printer" : "Add printer";
        testButton.Enabled = hasPrinter && isInstalled;
        findDriverButton.Enabled = hasPrinter && p!.Model.Length > 0;
    }

    void SetBusy(bool value)
    {
        busy = value;
        UseWaitCursor = value;
        UpdateButtons();
    }

    async Task AddSelectedPrinterAsync()
    {
        if (Selected is not { } p) return;
        string name = PrinterInstaller.SanitizeName(nameBox.Text);
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Type a name for the printer.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (driverBox.SelectedItem is not DriverChoice driver)
        {
            MessageBox.Show(this, "Choose a driver for the printer.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sameName = installed.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var existingForThis = PrinterInstaller.FindInstalled(installed, p);
        if (sameName != null && sameName != existingForThis &&
            MessageBox.Show(this, $"A different printer called \"{name}\" already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var saved = SavedPrinter.From(p, name, driver.DriverName, driver.UseIppClassDriver, autoRepairBox.Checked);
        SetBusy(true);
        try
        {
            Log($"Adding \"{name}\" using {driver.DriverName ?? "the built-in IPP driver"}...");
            var result = await PrinterInstaller.InstallAsync(saved);
            if (!result.Success)
            {
                Log($"Could not add the printer: {result.Message}");
                MessageBox.Show(this, $"Could not add the printer.\n\n{result.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Re-adding under a new name leaves the old copy behind; remove it so there is only one.
            if (existingForThis != null && !existingForThis.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                await PowerShellRunner.RunAsync($"Remove-Printer -Name {PowerShellRunner.Quote(existingForThis.Name)}");
                PrinterStore.Remove(existingForThis.Name);
            }

            Log($"\"{name}\" was added.");
            if (saved.AutoRepair)
            {
                PrinterStore.Upsert(saved);
                Log("Auto-repair is on: a small background helper will fix this printer if its address changes.");
            }
            else
            {
                PrinterStore.Remove(name);
            }
            Autostart.Sync();

            await RefreshInstalledAsync();
            foreach (ListViewItem item in printerList.Items) UpdateStatusColumn(item);
            UpdateButtons();

            if (MessageBox.Show(this, $"\"{name}\" is ready. Print a test page now?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                await PrintTestPageAsync(name);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task PrintTestPageAsync(string printerName)
    {
        SetBusy(true);
        try
        {
            var result = await PrinterInstaller.PrintTestPageAsync(printerName);
            Log(result.Success ? $"Test page sent to \"{printerName}\"." : $"Test page failed: {result.Message}");
            if (!result.Success)
                MessageBox.Show(this, $"The test page could not be printed.\n\n{result.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void OpenDriverSearch()
    {
        if (Selected is not { Model.Length: > 0 } p) return;
        string query = Uri.EscapeDataString($"{p.Model} printer driver Windows download");
        Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={query}") { UseShellExecute = true });
    }

    void Log(string message) => logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
}
