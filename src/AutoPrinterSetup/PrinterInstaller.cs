using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoPrinterSetup;

public static class PrinterInstaller
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<List<InstalledPrinter>> GetInstalledPrintersAsync()
    {
        var result = await PowerShellRunner.RunAsync(
            "ConvertTo-Json -Compress -InputObject @(Get-Printer | Select-Object Name, PortName, DriverName)");
        if (!result.Success) return [];
        try
        {
            return JsonSerializer.Deserialize<List<InstalledPrinter>>(result.Output.Trim(), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static InstalledPrinter? FindInstalled(IEnumerable<InstalledPrinter> installed, DiscoveredPrinter printer) =>
        installed.FirstOrDefault(i => PortPointsTo(i.PortName ?? "", printer.Host, printer.Protocol, printer.ResourcePath));

    static bool PortPointsTo(string portName, string host, PrinterProtocol protocol, string resourcePath)
    {
        // Boundaries stop "192.168.29.2" from matching "192.168.29.200".
        if (!Regex.IsMatch(portName, $@"(^|[^0-9.]){Regex.Escape(host)}($|[^0-9])")) return false;
        return protocol != PrinterProtocol.Ipp || portName.EndsWith(resourcePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds (or replaces) the Windows printer. Retries with administrator rights if Windows requires them.</summary>
    public static async Task<PsResult> InstallAsync(SavedPrinter printer)
    {
        string script = BuildInstallScript(printer);
        var result = await PowerShellRunner.RunAsync(script);
        if (!result.Success && NeedsAdmin(result.Message))
            result = await PowerShellRunner.RunAsync(script, elevated: true);
        return result;
    }

    public static Task<PsResult> RemoveAsync(string printerName) =>
        PowerShellRunner.RunAsync($"$name = {PowerShellRunner.Quote(printerName)}\n{RemoveExistingScript}");

    // Remove-Printer fails with 0x80070001 on printers that use an http:// (Internet) port; printui can delete those.
    const string RemoveExistingScript = """
        if (Get-Printer -Name $name -ErrorAction SilentlyContinue) {
            try { Remove-Printer -Name $name -ErrorAction Stop }
            catch { Start-Process -FilePath rundll32.exe -ArgumentList ('printui.dll,PrintUIEntry /dl /q /n "{0}"' -f $name) -Wait }
            for ($i = 0; $i -lt 20 -and (Get-Printer -Name $name -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
            if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { throw "Windows could not remove the old copy of '$name'." }
        }
        """;

    public static Task<PsResult> PrintTestPageAsync(string printerName) => PowerShellRunner.RunAsync($$"""
        $name = {{PowerShellRunner.Quote(printerName)}}
        $printer = Get-CimInstance -ClassName Win32_Printer | Where-Object { $_.Name -eq $name }
        if (-not $printer) { throw "There is no printer called '$name'." }
        $result = Invoke-CimMethod -InputObject $printer -MethodName PrintTestPage
        if ($result.ReturnValue -ne 0) { throw "Windows could not print a test page (error $($result.ReturnValue))." }
        """);

    public static string SanitizeName(string name) =>
        new string(name.Where(c => c is not ('"' or '\\' or ',' or '!')).ToArray()).Trim();

    static string BuildInstallScript(SavedPrinter p)
    {
        var q = PowerShellRunner.Quote;
        var sb = new StringBuilder();
        sb.AppendLine($"$name = {q(p.WindowsName)}");
        sb.AppendLine(RemoveExistingScript);

        if (p.UseIppClassDriver)
        {
            sb.AppendLine($"Add-Printer -Name $name -IppURL {q(p.IppUrl)}");
            return sb.ToString();
        }

        sb.AppendLine($"$driver = {q(p.DriverName ?? "")}");
        sb.AppendLine("if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $driver }");

        switch (p.Protocol)
        {
            case PrinterProtocol.Ipp:
                // Add-Printer can't create http:// ports, but printui can.
                sb.AppendLine($"$url = {q(p.HttpUrl)}");
                sb.AppendLine("""
                    $arguments = 'printui.dll,PrintUIEntry /if /q /b "{0}" /r "{1}" /m "{2}"' -f $name, $url, $driver
                    Start-Process -FilePath rundll32.exe -ArgumentList $arguments -Wait
                    for ($i = 0; $i -lt 20 -and -not (Get-Printer -Name $name -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
                    if (-not (Get-Printer -Name $name -ErrorAction SilentlyContinue)) { throw 'Windows could not add the printer. Make sure it is switched on and the driver is right for it.' }
                    """);
                break;

            case PrinterProtocol.Raw:
                string rawPort = p.Port == 9100 ? $"IP_{p.Host}" : $"IP_{p.Host}_{p.Port}";
                sb.AppendLine($"$port = {q(rawPort)}");
                sb.AppendLine($"if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name $port -PrinterHostAddress {q(p.Host)} -PortNumber {p.Port} }}");
                sb.AppendLine("Add-Printer -Name $name -DriverName $driver -PortName $port");
                break;

            case PrinterProtocol.Lpd:
                sb.AppendLine($"$port = {q($"LPR_{p.Host}_{p.ResourcePath}")}");
                sb.AppendLine($"if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name $port -LprHostAddress {q(p.Host)} -LprQueueName {q(p.ResourcePath)} -LprByteCounting }}");
                sb.AppendLine("Add-Printer -Name $name -DriverName $driver -PortName $port");
                break;
        }
        return sb.ToString();
    }

    static bool NeedsAdmin(string error) =>
        error.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("elevat", StringComparison.OrdinalIgnoreCase);
}
