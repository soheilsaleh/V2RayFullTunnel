using System.Text.Json.Nodes;
using V2RayFullTunnel.Common;

namespace V2RayFullTunnel.Core;

/// <summary>ساخت کانفیگ sing-box: نسخه‌ی TUN سراسری، و نسخه‌ی تست (بدون TUN).</summary>
public static class SingBoxConfigBuilder
{
    private static JsonObject BuildTls(ProxyLink link, bool defaultEnabled)
    {
        var tls = new JsonObject
        {
            ["enabled"] = string.IsNullOrEmpty(link.Security) || link.Security == "none"
                ? defaultEnabled : true,
            ["server_name"] = link.Sni
        };
        if (!string.IsNullOrWhiteSpace(link.Alpn))
        {
            var arr = new JsonArray();
            foreach (var a in link.Alpn.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                arr.Add(a.Trim());
            tls["alpn"] = arr;
        }
        return tls;
    }

    private static JsonObject WithTransport(JsonObject outbound, ProxyLink link)
    {
        JsonObject? t = link.Transport switch
        {
            "ws" or "websocket" => new JsonObject
            {
                ["type"] = "ws",
                ["path"] = string.IsNullOrEmpty(link.Path) ? "/" : link.Path,
                ["headers"] = new JsonObject { ["Host"] = link.Sni }
            },
            "grpc" or "gun" => new JsonObject
            {
                ["type"] = "grpc",
                ["service_name"] = (link.Path ?? "").Trim('/')
            },
            _ => null
        };
        if (t != null) outbound["transport"] = t;
        return outbound;
    }

    private static JsonObject BuildProxyOutbound(ProxyLink link, string? bindInterface)
    {
        JsonNode outbound = link.Protocol switch
        {
            ProxyProtocol.Vless => WithTransport(new JsonObject
            {
                ["type"] = "vless",
                ["tag"] = "proxy",
                ["server"] = link.Address,
                ["server_port"] = link.Port,
                ["uuid"] = link.IdOrPassword,
                ["tls"] = BuildTls(link, defaultEnabled: false)
            }, link),
            ProxyProtocol.Trojan => WithTransport(new JsonObject
            {
                ["type"] = "trojan",
                ["tag"] = "proxy",
                ["server"] = link.Address,
                ["server_port"] = link.Port,
                ["password"] = link.IdOrPassword,
                ["tls"] = BuildTls(link, defaultEnabled: true)
            }, link),
            _ => WithTransport(new JsonObject
            {
                ["type"] = "vmess",
                ["tag"] = "proxy",
                ["server"] = link.Address,
                ["server_port"] = link.Port,
                ["uuid"] = link.IdOrPassword,
                ["security"] = "auto",
                ["tls"] = BuildTls(link, defaultEnabled: false)
            }, link)
        };
        var proxy = (JsonObject)outbound;
        if (!string.IsNullOrEmpty(bindInterface))
            proxy["bind_interface"] = bindInterface;
        return proxy;
    }

    private static JsonArray BuildOutbounds(JsonNode proxy, string? bindInterface)
    {
        var direct = new JsonObject { ["type"] = "direct", ["tag"] = "direct" };
        if (!string.IsNullOrEmpty(bindInterface)) direct["bind_interface"] = bindInterface;
        return new JsonArray { proxy, direct, new JsonObject { ["type"] = "block", ["tag"] = "block" } };
    }

    private static JsonObject BuildDns() => new()
    {
        ["servers"] = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "direct-dns",
                ["type"] = "udp",
                ["server"] = "223.5.5.5"
            }
        },
        ["rules"] = new JsonArray(),
        // DoH در این شبکه رهگیری می‌شود؛ DNS ساده‌ی UDP پایدارتر است.
        ["final"] = "direct-dns"
    };

    private static JsonObject BuildRoute(ProxyLink link) => new()
    {
        ["auto_detect_interface"] = true,
        ["default_domain_resolver"] = new JsonObject { ["server"] = "direct-dns" },
        ["rules"] = new JsonArray
        {
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            // DNS بوت‌استرپ مستقیم می‌رود تا هسته بتواند سرور را رزولو کند
            new JsonObject { ["ip_cidr"] = new JsonArray { "223.5.5.5/32" }, ["outbound"] = "direct" },
            // اتصال خود هسته به سرور نباید وارد تونل شود (جلوگیری از loop)
            new JsonObject { ["domain"] = new JsonArray { link.Address }, ["outbound"] = "direct" }
        },
        ["final"] = "proxy"
    };

    private static string Serialize(JsonObject config) =>
        System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <param name="bindInterface">نام دقیق اینترفیس فیزیکی (از KillSwitch.GetGatewayTo)؛ حیاتی برای KillSwitch مسیری.</param>
    public static string Build(ProxyLink link, string? bindInterface) => Serialize(new JsonObject
    {
        ["log"] = new JsonObject { ["level"] = "info" },
        ["dns"] = BuildDns(),
        ["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "v2-tun",
                ["address"] = new JsonArray { "172.18.0.1/30" },
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "gvisor"
            },
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = 10808
            }
        },
        ["outbounds"] = BuildOutbounds(BuildProxyOutbound(link, bindInterface), bindInterface),
        ["route"] = BuildRoute(link)
    });

    /// <summary>
    /// کانفیگ تست: فقط یک inbound محلی (بدون TUN و بدون Kill Switch).
    /// هیچ مسیری در سیستم تغییر نمی‌کند؛ فقط صحت پروتکل/اعتبارنامه بررسی می‌شود.
    /// </summary>
    public static string BuildProbe(ProxyLink link, string? bindInterface, int mixedPort) => Serialize(new JsonObject
    {
        ["log"] = new JsonObject { ["level"] = "warn" },
        ["dns"] = BuildDns(),
        ["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = mixedPort
            }
        },
        ["outbounds"] = BuildOutbounds(BuildProxyOutbound(link, bindInterface), bindInterface),
        ["route"] = BuildRoute(link)
    });
}
