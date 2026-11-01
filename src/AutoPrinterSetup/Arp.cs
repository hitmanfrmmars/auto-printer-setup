using System.Net;
using System.Runtime.InteropServices;

namespace AutoPrinterSetup;

public static class Arp
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLength);

    /// <summary>Returns the MAC address of a device on the local network, e.g. "44-a1-91-4a-77-6a".</summary>
    public static string? GetMacAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return null;
        uint destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        var mac = new byte[6];
        uint length = (uint)mac.Length;
        if (SendARP(destination, 0, mac, ref length) != 0 || length == 0) return null;
        return string.Join("-", mac.Take((int)length).Select(b => b.ToString("x2")));
    }
}
