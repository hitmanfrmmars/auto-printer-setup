using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AutoPrinterSetup;

public sealed record LocalNetwork(IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{Address}/{PrefixLength}";
}

public sealed record ScanProgress(int Done, int Total);

public static class NetworkScanner
{
    public static readonly int[] PrinterPorts = [631, 9100, 515];

    // Networks bigger than this are too slow to sweep, so only the PC's own /24 is scanned.
    const int SmallestScannedPrefix = 22;
    const int ConnectTimeoutMs = 600;
    const int MaxParallelHosts = 64;

    public static List<LocalNetwork> GetLocalNetworks()
    {
        var networks = new List<LocalNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway) continue;
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    networks.Add(new LocalNetwork(unicast.Address, unicast.PrefixLength));
            }
        }
        return networks;
    }

    /// <summary>Finds every printer on the local networks. Each printer is reported as soon as it is identified.</summary>
    public static async Task<List<DiscoveredPrinter>> ScanAsync(
        IProgress<ScanProgress>? progress, IProgress<DiscoveredPrinter>? found, CancellationToken ct)
    {
        var printers = new ConcurrentBag<DiscoveredPrinter>();
        await ScanPortsAsync(PrinterPorts, async (host, openPorts) =>
        {
            foreach (var printer in await IdentifyAsync(host, openPorts, ct))
            {
                printers.Add(printer);
                found?.Report(printer);
            }
        }, progress, ct);
        return printers.OrderBy(p => SortKey(p.Host)).ToList();
    }

    public static async Task<List<(string Host, HashSet<int> OpenPorts)>> ScanPortsAsync(
        IReadOnlyCollection<int> ports,
        Func<string, HashSet<int>, Task>? onHostFound,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var hosts = GetLocalNetworks().SelectMany(GetHosts).Distinct().ToList();
        var results = new ConcurrentBag<(string Host, HashSet<int> OpenPorts)>();
        int done = 0;
        using var gate = new SemaphoreSlim(MaxParallelHosts);

        await Task.WhenAll(hosts.Select(async host =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var open = await GetOpenPortsAsync(host, ports, ct);
                if (open.Count == 0) return;
                results.Add((host, open));
                if (onHostFound == null) return;
                try
                {
                    await onHostFound(host, open);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
            }
            finally
            {
                gate.Release();
                progress?.Report(new ScanProgress(Interlocked.Increment(ref done), hosts.Count));
            }
        }));

        ct.ThrowIfCancellationRequested();
        return results.OrderBy(r => SortKey(r.Host)).ToList();
    }

    /// <summary>Works out what kind of printer answers on the given ports. IPP is preferred because it reports queue names.</summary>
    public static async Task<List<DiscoveredPrinter>> IdentifyAsync(string host, HashSet<int> openPorts, CancellationToken ct)
    {
        var printers = new List<DiscoveredPrinter>();
        if (openPorts.Contains(631)) printers.AddRange(await IppProbe.DiscoverAsync(host, 631, ct));

        if (printers.Count == 0 && (openPorts.Contains(9100) || openPorts.Contains(515)))
        {
            string model = await SnmpProbe.GetModelAsync(host, ct) ?? "";
            string name = model.Length > 0 ? model : $"Printer at {host}";
            printers.Add(openPorts.Contains(9100)
                ? new DiscoveredPrinter { Host = host, Protocol = PrinterProtocol.Raw, Port = 9100, DisplayName = name, Model = model, Source = "Raw" }
                : new DiscoveredPrinter { Host = host, Protocol = PrinterProtocol.Lpd, Port = 515, ResourcePath = "lp", DisplayName = name, Model = model, Source = "LPD" });
        }

        string? mac = printers.Count > 0 ? Arp.GetMacAddress(host) : null;
        foreach (var printer in printers) printer.MacAddress = mac;
        return printers;
    }

    public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    static async Task<HashSet<int>> GetOpenPortsAsync(string host, IEnumerable<int> ports, CancellationToken ct)
    {
        var checks = ports.Select(async port => (port, open: await IsPortOpenAsync(host, port, ConnectTimeoutMs, ct)));
        return (await Task.WhenAll(checks)).Where(c => c.open).Select(c => c.port).ToHashSet();
    }

    static IEnumerable<string> GetHosts(LocalNetwork network)
    {
        int prefix = network.PrefixLength < SmallestScannedPrefix ? 24 : network.PrefixLength;
        if (prefix >= 31) yield break;
        uint ip = ToUInt32(network.Address);
        uint mask = uint.MaxValue << (32 - prefix);
        uint first = (ip & mask) + 1, last = (ip | ~mask) - 1;
        for (uint host = first; host <= last; host++)
        {
            if (host != ip) yield return FromUInt32(host).ToString();
        }
    }

    static uint SortKey(string host) => IPAddress.TryParse(host, out var a) ? ToUInt32(a) : 0;

    static uint ToUInt32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    static IPAddress FromUInt32(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
}
