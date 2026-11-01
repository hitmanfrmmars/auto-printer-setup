using System.Text.RegularExpressions;

namespace AutoPrinterSetup;

public static partial class DriverMatcher
{
    public const string PclClassDriver = "Microsoft PCL6 Class Driver";
    public const string BrotherClassDriver = "Brother Laser Type1 Class Driver";

    [GeneratedRegex(@"[A-Za-z]{1,6}-?[A-Za-z]{0,3}\d{2,}[A-Za-z0-9]*")]
    private static partial Regex ModelNumber();

    [GeneratedRegex(@"OneNote|Print To PDF|PDF Converter|XPS Document Writer|Fax|Point and Print|Universal Print|Virtual Print|IPP Class Driver", RegexOptions.IgnoreCase)]
    private static partial Regex NonPrinterDriver();

    [GeneratedRegex(@"\b(DCP|MFC|HL)-", RegexOptions.IgnoreCase)]
    private static partial Regex BrotherModel();

    /// <summary>Printer drivers installed on this PC, excluding virtual printers such as "Print to PDF".</summary>
    public static async Task<List<string>> GetInstalledDriversAsync()
    {
        var result = await PowerShellRunner.RunAsync("Get-PrinterDriver | Select-Object -ExpandProperty Name | Sort-Object -Unique");
        if (!result.Success) return [];
        return result.Output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(name => !NonPrinterDriver().IsMatch(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds the installed driver whose name contains the printer's model number, e.g. "DCP-B7500D".</summary>
    public static string? FindBestMatch(DiscoveredPrinter printer, IEnumerable<string> drivers)
    {
        var tokens = ModelNumber().Matches($"{printer.Model} {printer.DisplayName}")
            .Select(m => Normalize(m.Value))
            .Where(t => t.Length >= 4)
            .Distinct()
            .ToList();
        if (tokens.Count == 0) return null;

        return drivers
            .Select(driver =>
            {
                string normalized = Normalize(driver);
                return (Driver: driver, Score: tokens.Where(t => normalized.Contains(t)).Sum(t => t.Length));
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Driver.Length)
            .Select(x => x.Driver)
            .FirstOrDefault();
    }

    /// <summary>A generic driver that ships with Windows and handles most laser printers.</summary>
    public static string FindFallback(DiscoveredPrinter printer)
    {
        string text = $"{printer.Model} {printer.DisplayName}";
        bool isBrother = text.Contains("Brother", StringComparison.OrdinalIgnoreCase) || BrotherModel().IsMatch(text);
        return isBrother ? BrotherClassDriver : PclClassDriver;
    }

    static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
