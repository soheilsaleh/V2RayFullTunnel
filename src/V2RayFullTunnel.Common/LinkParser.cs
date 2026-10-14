using System.Text;
using System.Text.Json;

namespace V2RayFullTunnel.Common;

public static class LinkParser
{
    public static ProxyLink Parse(string link)
    {
        link = link.Trim();
        if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return ParseVlessOrTrojan(link, ProxyProtocol.Vless);
        if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            return ParseVlessOrTrojan(link, ProxyProtocol.Trojan);
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return ParseVmess(link);
        throw new ArgumentException("لینک باید با vless:// یا vmess:// یا trojan:// شروع شود.");
    }

    private static ProxyLink ParseVlessOrTrojan(string link, ProxyProtocol proto)
    {
        var uri = new Uri(link);
        string id = Uri.UnescapeDataString(uri.UserInfo);
        string host = uri.Host;
        int port = uri.Port;
        var q = ParseQuery(uri.Query);
        string remark = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        string sni = Get(q, "sni", Get(q, "serverName", host));
        return new ProxyLink(
            proto, host, port, id,
            Get(q, "security", "none"),
            sni,
            Get(q, "path", "/"),
            remark,
            Get(q, "type", "tcp").ToLowerInvariant(),
            Get(q, "alpn", ""));
    }

    private static ProxyLink ParseVmess(string link)
    {
        string b64 = link["vmess://".Length..].Trim();
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string host = r.GetProperty("add").GetString() ?? "";
        int port = int.Parse(r.GetProperty("port").GetString() ?? "443");
        string id = r.GetProperty("id").GetString() ?? "";
        string remark = r.TryGetProperty("ps", out var ps) ? ps.GetString() ?? "" : "";
        string sni = r.TryGetProperty("sni", out var sniEl) ? sniEl.GetString() ?? host : host;
        string tls = r.TryGetProperty("tls", out var tlsEl) ? tlsEl.GetString() ?? "none" : "none";
        string net = r.TryGetProperty("net", out var netEl) ? netEl.GetString() ?? "tcp" : "tcp";
        string path = r.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "/" : "/";
        return new ProxyLink(ProxyProtocol.Vmess, host, port, id, tls, sni, path, remark, net, "");
    }

    // System.Web در دسترس نیست؛ پارسر کوچک کوئری بدون وابستگی خارجی.
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = eq < 0 ? part : part[..eq];
            string v = eq < 0 ? "" : part[(eq + 1)..];
            d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return d;
    }

    private static string Get(Dictionary<string, string> q, string key, string def) =>
        q.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;
}
