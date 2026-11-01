using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoPrinterSetup;

/// <summary>Reads a printer's model name over SNMPv1 (community "public") without sending it anything printable.</summary>
public static class SnmpProbe
{
    static readonly string[] ModelOids =
    [
        "1.3.6.1.2.1.25.3.2.1.3.1", // hrDeviceDescr.1
        "1.3.6.1.2.1.1.1.0",        // sysDescr.0
    ];

    public static async Task<string?> GetModelAsync(string host, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var address)) return null;
        foreach (var oid in ModelOids)
        {
            string? value = await GetStringAsync(address, oid, ct);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    static async Task<string?> GetStringAsync(IPAddress address, string oid, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            byte[] request = BuildGetRequest("public", Random.Shared.Next(1, int.MaxValue), oid);
            await udp.SendAsync(request, new IPEndPoint(address, 161), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var reply = await udp.ReceiveAsync(timeout.Token);
            return ParseStringValue(reply.Buffer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    static byte[] BuildGetRequest(string community, int requestId, string oid)
    {
        byte[] varBind = Tlv(0x30, [.. Oid(oid), 0x05, 0x00]);
        byte[] pdu = Tlv(0xA0, [.. Integer(requestId), .. Integer(0), .. Integer(0), .. Tlv(0x30, varBind)]);
        return Tlv(0x30, [.. Integer(0), .. Tlv(0x04, Encoding.ASCII.GetBytes(community)), .. pdu]);
    }

    static string? ParseStringValue(byte[] d)
    {
        int pos = 0;

        bool Next(out byte tag, out int start, out int length)
        {
            tag = 0; start = 0; length = 0;
            if (pos + 2 > d.Length) return false;
            tag = d[pos++];
            int len = d[pos++];
            if ((len & 0x80) != 0)
            {
                int count = len & 0x7F;
                if (count > 3 || pos + count > d.Length) return false;
                len = 0;
                for (int i = 0; i < count; i++) len = (len << 8) | d[pos++];
            }
            if (pos + len > d.Length) return false;
            start = pos;
            length = len;
            pos += len;
            return true;
        }

        if (!Next(out var tag, out var start, out _) || tag != 0x30) return null;
        pos = start;
        if (!Next(out _, out _, out _)) return null;                          // version
        if (!Next(out _, out _, out _)) return null;                          // community
        if (!Next(out tag, out start, out _) || tag != 0xA2) return null;     // GetResponse PDU
        pos = start;
        if (!Next(out _, out _, out _)) return null;                          // request id
        if (!Next(out _, out start, out var length)) return null;             // error status
        if (length != 1 || d[start] != 0) return null;
        if (!Next(out _, out _, out _)) return null;                          // error index
        if (!Next(out tag, out start, out _) || tag != 0x30) return null;     // varbind list
        pos = start;
        if (!Next(out tag, out start, out _) || tag != 0x30) return null;     // first varbind
        pos = start;
        if (!Next(out _, out _, out _)) return null;                          // oid
        if (!Next(out tag, out start, out length) || tag != 0x04) return null;
        return Encoding.UTF8.GetString(d, start, length).TrimEnd('\0');
    }

    static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] length = content.Length switch
        {
            < 0x80 => [(byte)content.Length],
            < 0x100 => [0x81, (byte)content.Length],
            _ => [0x82, (byte)(content.Length >> 8), (byte)content.Length],
        };
        return [tag, .. length, .. content];
    }

    static byte[] Integer(int value)
    {
        var bytes = new List<byte>();
        uint v = (uint)value;
        do
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        } while (v != 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return Tlv(0x02, [.. bytes]);
    }

    static byte[] Oid(string oid)
    {
        uint[] parts = oid.Split('.').Select(uint.Parse).ToArray();
        var bytes = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (uint part in parts.Skip(2))
        {
            var encoded = new Stack<byte>();
            uint v = part;
            encoded.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) encoded.Push((byte)(0x80 | (v & 0x7F)));
            bytes.AddRange(encoded);
        }
        return Tlv(0x06, [.. bytes]);
    }
}
