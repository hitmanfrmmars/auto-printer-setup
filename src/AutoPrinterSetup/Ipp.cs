using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;

namespace AutoPrinterSetup;

public sealed class IppResponse
{
    public int StatusCode { get; init; }
    public bool IsSuccess => StatusCode < 0x0100;
    public List<Dictionary<string, List<string>>> PrinterGroups { get; } = [];
}

/// <summary>Minimal IPP/1.1 encoder and decoder (RFC 8010), enough for printer discovery.</summary>
public static class Ipp
{
    public const byte TagCharset = 0x47, TagLanguage = 0x48, TagUri = 0x45, TagName = 0x42, TagKeyword = 0x44;
    public const short OpGetPrinterAttributes = 0x000B, OpCupsGetPrinters = 0x4002;

    public static byte[] BuildRequest(short operation, int requestId, IEnumerable<(byte Tag, string Name, string[] Values)> attributes)
    {
        using var ms = new MemoryStream();
        ms.Write([0x01, 0x01]);
        WriteUInt16(ms, (ushort)operation);
        WriteInt32(ms, requestId);
        ms.WriteByte(0x01);
        foreach (var (tag, name, values) in attributes)
        {
            for (int i = 0; i < values.Length; i++)
            {
                ms.WriteByte(tag);
                WriteString(ms, i == 0 ? name : "");
                WriteString(ms, values[i]);
            }
        }
        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    public static IppResponse? ParseResponse(byte[] data)
    {
        if (data.Length < 9) return null;
        var response = new IppResponse { StatusCode = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) };
        Dictionary<string, List<string>>? group = null;
        string? lastName = null;
        int pos = 8;
        while (pos < data.Length)
        {
            byte tag = data[pos++];
            if (tag == 0x03) break;
            if (tag < 0x10)
            {
                group = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (tag == 0x04) response.PrinterGroups.Add(group);
                continue;
            }
            if (pos + 2 > data.Length) break;
            int nameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;
            if (pos + nameLength + 2 > data.Length) break;
            string name = Encoding.UTF8.GetString(data, pos, nameLength);
            pos += nameLength;
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;
            if (pos + valueLength > data.Length) break;
            var value = data.AsSpan(pos, valueLength);
            pos += valueLength;

            if (nameLength > 0) lastName = name;
            if (group == null || lastName == null) continue;
            if (!group.TryGetValue(lastName, out var values)) group[lastName] = values = [];
            values.Add(DecodeValue(tag, value));
        }
        return response;
    }

    static string DecodeValue(byte tag, ReadOnlySpan<byte> value) => tag switch
    {
        0x21 or 0x23 when value.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(value).ToString(),
        0x22 when value.Length == 1 => value[0] != 0 ? "true" : "false",
        >= 0x41 and <= 0x49 => Encoding.UTF8.GetString(value),
        _ => "",
    };

    static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(s, (ushort)bytes.Length);
        s.Write(bytes);
    }

    static void WriteUInt16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    static void WriteInt32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }
}

public static class IppProbe
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    static readonly string[] RequestedAttributes =
    [
        "printer-name", "printer-uri-supported", "printer-make-and-model", "printer-info",
        "printer-uuid", "device-uri", "cups-version",
    ];

    static readonly string[] DirectPrinterPaths = ["/ipp/print", "/ipp", "/"];

    /// <summary>Lists the queues of a CUPS server, or describes a stand-alone IPP printer.</summary>
    public static async Task<List<DiscoveredPrinter>> DiscoverAsync(string host, int port, CancellationToken ct)
    {
        var printers = new List<DiscoveredPrinter>();

        var cups = await SendAsync(host, port, "/", Ipp.OpCupsGetPrinters, null, ct);
        if (cups is { IsSuccess: true })
        {
            foreach (var group in cups.PrinterGroups)
            {
                if (FromCupsQueue(host, port, group) is { } printer) printers.Add(printer);
            }
        }
        if (printers.Count > 0) return printers;

        foreach (var path in DirectPrinterPaths)
        {
            var response = await SendAsync(host, port, path, Ipp.OpGetPrinterAttributes, $"ipp://{host}:{port}{path}", ct);
            if (response is { IsSuccess: true, PrinterGroups.Count: > 0 })
            {
                printers.Add(FromDirectPrinter(host, port, path, response.PrinterGroups[0]));
                break;
            }
        }
        return printers;
    }

    static DiscoveredPrinter? FromCupsQueue(string host, int port, Dictionary<string, List<string>> attrs)
    {
        string name = First(attrs, "printer-name");
        if (name.Length == 0) return null;

        string uri = First(attrs, "printer-uri-supported");
        string path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? parsed.AbsolutePath
            : "/printers/" + Uri.EscapeDataString(name);
        string info = First(attrs, "printer-info");
        string makeAndModel = First(attrs, "printer-make-and-model");
        string friendly = (info.Length > 0 ? info : name).Replace('_', ' ');

        // A raw queue forwards jobs untouched, so the PC must send the printer's native language.
        bool isRawQueue = makeAndModel.Length == 0 || makeAndModel.Contains("raw", StringComparison.OrdinalIgnoreCase);
        string cupsVersion = First(attrs, "cups-version");

        return new DiscoveredPrinter
        {
            Host = host,
            Protocol = PrinterProtocol.Ipp,
            Port = port,
            ResourcePath = path,
            DisplayName = friendly,
            Model = isRawQueue ? friendly : makeAndModel,
            NeedsVendorDriver = isRawQueue,
            Uuid = NullIfEmpty(First(attrs, "printer-uuid")),
            Source = cupsVersion.Length > 0 ? $"CUPS {cupsVersion}" : "CUPS",
        };
    }

    static DiscoveredPrinter FromDirectPrinter(string host, int port, string path, Dictionary<string, List<string>> attrs)
    {
        string model = First(attrs, "printer-make-and-model");
        string name = First(attrs, "printer-info") is { Length: > 0 } info ? info : First(attrs, "printer-name");
        return new DiscoveredPrinter
        {
            Host = host,
            Protocol = PrinterProtocol.Ipp,
            Port = port,
            ResourcePath = path,
            DisplayName = name.Length > 0 ? name : model.Length > 0 ? model : $"Printer at {host}",
            Model = model,
            NeedsVendorDriver = false,
            Uuid = NullIfEmpty(First(attrs, "printer-uuid")),
            Source = "printer",
        };
    }

    static async Task<IppResponse?> SendAsync(string host, int port, string path, short operation, string? printerUri, CancellationToken ct)
    {
        var attributes = new List<(byte, string, string[])>
        {
            (Ipp.TagCharset, "attributes-charset", ["utf-8"]),
            (Ipp.TagLanguage, "attributes-natural-language", ["en"]),
        };
        if (printerUri != null) attributes.Add((Ipp.TagUri, "printer-uri", [printerUri]));
        attributes.Add((Ipp.TagName, "requesting-user-name", ["auto-printer-setup"]));
        attributes.Add((Ipp.TagKeyword, "requested-attributes", RequestedAttributes));

        try
        {
            using var content = new ByteArrayContent(Ipp.BuildRequest(operation, 1, attributes));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
            using var response = await Http.PostAsync($"http://{host}:{port}{path}", content, ct);
            if (!response.IsSuccessStatusCode) return null;
            return Ipp.ParseResponse(await response.Content.ReadAsByteArrayAsync(ct));
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

    static string First(Dictionary<string, List<string>> attrs, string name) =>
        attrs.TryGetValue(name, out var values) && values.Count > 0 ? values[0].Trim() : "";

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
