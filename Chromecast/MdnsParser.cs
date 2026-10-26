using System.Net;
using System.Text;

namespace CastBox.Chromecast;

/// <summary>A Google Cast device (a Chromecast, a TV with Chromecast built in) found on the network.</summary>
public sealed record CastDevice(string Id, string Name, string Model, string Address, int Port);

/// <summary>Reads the DNS-SD answers a Chromecast gives to a "_googlecast._tcp" question: name, model, address and port.</summary>
public static class MdnsParser
{
    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    /// <summary>The Cast devices described in one mDNS packet. Anything malformed is ignored (never throws).</summary>
    public static IReadOnlyList<CastDevice> ParseCastDevices(byte[] packet, IPAddress? sender = null)
    {
        try { return Parse(packet, sender); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException) { return Array.Empty<CastDevice>(); }
    }

    private static IReadOnlyList<CastDevice> Parse(byte[] p, IPAddress? sender)
    {
        if (p.Length < 12) return Array.Empty<CastDevice>();
        var questions = U16(p, 4); var records = U16(p, 6) + U16(p, 8) + U16(p, 10);
        var pos = 12;
        for (var i = 0; i < questions; i++) { ReadName(p, ref pos); pos += 4; }

        var instances = new List<string>();
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records; i++)
        {
            var name = ReadName(p, ref pos);
            var type = U16(p, pos); var length = U16(p, pos + 8);
            var data = pos + 10; pos = data + length;
            if (pos > p.Length) break;
            switch (type)
            {
                case TypePtr when name.EndsWith("_googlecast._tcp.local", StringComparison.OrdinalIgnoreCase):
                    { var at = data; instances.Add(ReadName(p, ref at)); break; }
                case TypeSrv:
                    { var at = data + 6; srv[name] = (ReadName(p, ref at), U16(p, data + 4)); break; }
                case TypeTxt:
                    txt[name] = ReadTxt(p, data, length); break;
                case TypeA when length == 4:
                    addresses[name] = new IPAddress(p.AsSpan(data, 4)).ToString(); break;
            }
        }

        var found = new List<CastDevice>();
        foreach (var instance in instances.Concat(srv.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!srv.TryGetValue(instance, out var service)) continue;
            var fields = txt.GetValueOrDefault(instance) ?? new();
            var address = addresses.GetValueOrDefault(service.Target) ?? sender?.ToString();
            if (address is null) continue;
            var id = fields.GetValueOrDefault("id") ?? instance;
            var friendly = fields.GetValueOrDefault("fn") ?? fields.GetValueOrDefault("md") ?? instance;
            found.Add(new CastDevice(id, friendly, fields.GetValueOrDefault("md") ?? "", address, service.Port));
        }
        return found;
    }

    private static Dictionary<string, string> ReadTxt(byte[] p, int start, int length)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = start; var end = start + length;
        while (pos < end)
        {
            var n = p[pos++];
            if (n == 0 || pos + n > end) break;
            var pair = Encoding.UTF8.GetString(p, pos, n); pos += n;
            var eq = pair.IndexOf('=');
            if (eq > 0) result[pair[..eq]] = pair[(eq + 1)..];
        }
        return result;
    }

    private static int U16(byte[] p, int at) => (p[at] << 8) | p[at + 1];

    /// <summary>A DNS name, following compression pointers (with a limit so a hostile packet can't loop forever).</summary>
    private static string ReadName(byte[] p, ref int pos)
    {
        var parts = new List<string>();
        var at = pos; var jumped = false; var hops = 0;
        while (true)
        {
            var n = p[at];
            if (n == 0) { at++; break; }
            if ((n & 0xC0) == 0xC0)
            {
                var target = ((n & 0x3F) << 8) | p[at + 1];
                if (!jumped) pos = at + 2;
                jumped = true; at = target;
                if (++hops > 20) throw new InvalidOperationException("name loop");
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(p, at + 1, n)); at += 1 + n;
        }
        if (!jumped) pos = at;
        return string.Join('.', parts);
    }
}
