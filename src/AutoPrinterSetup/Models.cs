using System.Text.Json.Serialization;

namespace AutoPrinterSetup;

public enum PrinterProtocol { Ipp, Raw, Lpd }

public sealed class DiscoveredPrinter
{
    public required string Host { get; init; }
    public required PrinterProtocol Protocol { get; init; }
    public required int Port { get; init; }

    /// <summary>IPP: resource path such as "/printers/name". LPD: queue name. Raw: unused.</summary>
    public string ResourcePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>False only for printers that accept driverless IPP jobs (PDF / PWG raster).</summary>
    public bool NeedsVendorDriver { get; init; } = true;
    public string? Uuid { get; init; }
    public string? MacAddress { get; set; }
    public string Source { get; init; } = "";

    public string ConnectionLabel => Protocol switch
    {
        PrinterProtocol.Ipp => string.IsNullOrEmpty(Source) ? "IPP" : $"IPP ({Source})",
        PrinterProtocol.Raw => $"Raw, port {Port}",
        _ => $"LPD, queue \"{ResourcePath}\"",
    };
}

public sealed record SavedPrinter
{
    public string WindowsName { get; init; } = "";
    public string? DriverName { get; init; }
    public bool UseIppClassDriver { get; init; }
    public PrinterProtocol Protocol { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string ResourcePath { get; init; } = "";
    public string Model { get; init; } = "";
    public string? MacAddress { get; init; }
    public string? Uuid { get; init; }
    public bool AutoRepair { get; init; }

    [JsonIgnore] public string HttpUrl => $"http://{Host}:{Port}{ResourcePath}";
    [JsonIgnore] public string IppUrl => $"ipp://{Host}:{Port}{ResourcePath}";

    public static SavedPrinter From(DiscoveredPrinter p, string windowsName, string? driverName, bool useIppClassDriver, bool autoRepair) => new()
    {
        WindowsName = windowsName,
        DriverName = driverName,
        UseIppClassDriver = useIppClassDriver,
        Protocol = p.Protocol,
        Host = p.Host,
        Port = p.Port,
        ResourcePath = p.ResourcePath,
        Model = p.Model,
        MacAddress = p.MacAddress,
        Uuid = p.Uuid,
        AutoRepair = autoRepair,
    };
}

public sealed record InstalledPrinter(string Name, string? PortName, string? DriverName);
