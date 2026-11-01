namespace AutoPrinterSetup;

public enum RepairOutcome { Ok, Moved, NotFound, Failed, Forgotten }

public sealed record RepairResult(SavedPrinter Printer, RepairOutcome Outcome, string Message);

/// <summary>Checks saved printers and re-points them when the router hands the print server a new IP address.</summary>
public static class PrinterRepair
{
    public static async Task<List<RepairResult>> CheckAllAsync(CancellationToken ct)
    {
        var results = new List<RepairResult>();
        var watched = PrinterStore.Load().Where(p => p.AutoRepair).ToList();
        if (watched.Count == 0) return results;

        var installed = await PrinterInstaller.GetInstalledPrintersAsync();
        foreach (var printer in watched)
        {
            ct.ThrowIfCancellationRequested();
            if (!installed.Any(i => i.Name.Equals(printer.WindowsName, StringComparison.OrdinalIgnoreCase)))
            {
                PrinterStore.Remove(printer.WindowsName);
                results.Add(new(printer, RepairOutcome.Forgotten, $"\"{printer.WindowsName}\" was removed from Windows, so it is no longer watched."));
                continue;
            }

            if (await IsStillThereAsync(printer, ct))
            {
                results.Add(new(printer, RepairOutcome.Ok, $"\"{printer.WindowsName}\" is reachable at {printer.Host}."));
                continue;
            }

            string? newHost = await FindNewHostAsync(printer, ct);
            if (newHost == null)
            {
                results.Add(new(printer, RepairOutcome.NotFound, $"\"{printer.WindowsName}\" can't be reached. It may be switched off, or this PC is on another network."));
                continue;
            }

            var moved = printer with { Host = newHost, MacAddress = printer.MacAddress ?? Arp.GetMacAddress(newHost) };
            var install = await PrinterInstaller.InstallAsync(moved);
            if (install.Success)
            {
                PrinterStore.Upsert(moved);
                results.Add(new(moved, RepairOutcome.Moved, $"\"{printer.WindowsName}\" moved from {printer.Host} to {newHost} and has been fixed."));
            }
            else
            {
                results.Add(new(printer, RepairOutcome.Failed, $"\"{printer.WindowsName}\" is now at {newHost}, but updating it failed: {install.Message}"));
            }
        }
        return results;
    }

    static async Task<bool> IsStillThereAsync(SavedPrinter printer, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (await NetworkScanner.IsPortOpenAsync(printer.Host, printer.Port, 1500, ct))
            {
                // Something answers at the old address; make sure it is the same device.
                if (printer.MacAddress == null) return true;
                string? mac = Arp.GetMacAddress(printer.Host);
                return mac == null || mac.Equals(printer.MacAddress, StringComparison.OrdinalIgnoreCase);
            }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    static async Task<string?> FindNewHostAsync(SavedPrinter printer, CancellationToken ct)
    {
        var candidates = (await NetworkScanner.ScanPortsAsync([printer.Port], null, null, ct))
            .Select(c => c.Host)
            .Where(h => h != printer.Host)
            .ToList();

        if (printer.MacAddress != null)
        {
            foreach (var host in candidates)
            {
                if (printer.MacAddress.Equals(Arp.GetMacAddress(host), StringComparison.OrdinalIgnoreCase)) return host;
            }
        }

        // No MAC match (e.g. the print server is behind another router): fall back to the printer's identity,
        // but only trust it when exactly one device fits.
        var matches = new List<string>();
        foreach (var host in candidates)
        {
            var found = await NetworkScanner.IdentifyAsync(host, [printer.Port], ct);
            if (found.Any(f => IsSamePrinter(f, printer))) matches.Add(host);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool IsSamePrinter(DiscoveredPrinter found, SavedPrinter saved)
    {
        if (found.Protocol != saved.Protocol) return false;
        if (found.Uuid != null && saved.Uuid != null) return found.Uuid.Equals(saved.Uuid, StringComparison.OrdinalIgnoreCase);
        if (saved.Protocol == PrinterProtocol.Ipp && !found.ResourcePath.Equals(saved.ResourcePath, StringComparison.OrdinalIgnoreCase)) return false;
        return saved.Model.Length > 0 && found.Model.Equals(saved.Model, StringComparison.OrdinalIgnoreCase);
    }
}
