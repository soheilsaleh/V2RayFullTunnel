namespace V2RayFullTunnel.Common;

public enum ProxyProtocol { Vless, Vmess, Trojan }

public sealed record ProxyLink(
    ProxyProtocol Protocol,
    string Address,
    int Port,
    string IdOrPassword,
    string Security,
    string Sni,
    string Path,
    string Remark,
    string Transport,
    string Alpn
);
