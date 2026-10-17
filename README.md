# V2Ray Full Tunnel

A TUN-based system-wide proxy client for Windows, with a route-based Kill Switch.

![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)

V2Ray Full Tunnel routes all system traffic through a proxy by creating a virtual
network adapter (TUN), instead of relying on per-application proxy settings. It is a
WPF desktop application that generates a configuration for and supervises the
[sing-box](https://github.com/SagerNet/sing-box) core.

> **Note on the name:** the project is named `V2RayFullTunnel`, but its core is
> **sing-box**. No V2Ray or Xray code is included. The name is historical. This project
> is not affiliated with, endorsed by, or a derivative work of sing-box or WireGuard.

## Why this exists

Most proxy clients on Windows operate in one of two modes: a system proxy (which many
applications ignore) or per-application routing. Neither reliably captures **all**
traffic from **all** processes, and behavior differed from one application to another
during testing. This project was written to provide a single, transparent, system-wide
tunnel with a predictable fail-closed behavior, so that the machine's traffic always
follows one path. It is published in the hope that it is useful to others who need the
same thing.

## Features

- **System-wide TUN routing** via `auto_route`, so traffic from every process is
  captured without per-application configuration.
- **Route-based Kill Switch (fail-closed).** While the tunnel is up, the physical
  default route is removed and replaced with narrow host routes for the proxy server and
  the bootstrap DNS resolver. If the core stops, the machine has no direct internet
  path until the route state is restored. Route state is persisted and survives
  restarts of the app; it is not permanent, so a reboot is always a safe fallback.
- **DNS handling.** DNS queries are hijacked into the tunnel, with a plain-UDP bootstrap
  resolver reachable outside the tunnel so the core can resolve the server address.
- **Loop prevention.** The proxy outbound is bound to the physical interface, and the
  core's own connection to the server is routed directly, avoiding a routing loop.
- **Persistent profiles.** Proxy links (`vless://`, `vmess://`, `trojan://`) are parsed
  and saved under `%APPDATA%\V2RayFullTunnel\`, with one selectable default profile.
- **Self-healing adapter state.** If a TUN adapter is left behind by a hard shutdown,
  the app detects and removes it automatically and retries; startup also recovers an
  interrupted network state.
- **Tray integration.** Closing the window hides it to the notification area; the tray
  menu offers Show / Connect / Disconnect / Exit.
- **Exit IP check**, live status indicator, and an in-app log pane.

Supported proxy links: VLESS, VMess, and Trojan, with TLS and `ws` / `grpc` transports,
SNI, and ALPN options.

## How it works

1. The physical default gateway and the proxy server's IP are resolved **before** the
   tunnel starts, because name resolution moves into the tunnel afterwards.
2. A sing-box configuration is generated (TUN inbound + proxy outbound + direct and
   block outbounds) and validated with `sing-box check`.
3. sing-box is started and the app waits for the TUN interface to come up. If it does
   not, the leftover adapter is cleaned up and the start is retried.
4. The Kill Switch is applied atomically: host routes for the server and bootstrap DNS
   are created and verified first, then the physical default route is removed. Any
   failure triggers a full rollback.
5. On disconnect, the saved routes are restored and the core is stopped.

## Project layout

| Path | Description |
| --- | --- |
| `src/V2RayFullTunnel.Common` | Proxy-link parser (`LinkParser`), data model, and profile storage (`ProfileStore`). |
| `src/V2RayFullTunnel.Core` | sing-box config generation (`SingBoxConfigBuilder`), process runner (`SingBoxRunner`), and orchestration (`TunnelManager`). |
| `src/V2RayFullTunnel.Firewall` | `KillSwitch`: gateway detection, server resolution, route-based kill switch, adapter cleanup. |
| `src/V2RayFullTunnel.UI` | WPF application (window, tray icon, log pane). |
| `src/V2RayFullTunnel.Service` | Windows service worker (early-stage; not yet wired to the UI). |
| `configs/` | Example sing-box configuration templates. |
| `scripts/` | Helper scripts: core download, tunnel test, kill-switch test, shortcut install. |
| `vendor/` | Location for the sing-box binary (not committed to the repository). |

## Requirements

- Windows 10 or Windows 11 (x64).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build; the .NET 8
  **Desktop Runtime** to run a published build.
- Administrator privileges. Creating a TUN adapter and modifying the routing table
  require elevation. The app manifest requests elevation on launch.
- The sing-box core executable (downloaded separately; see below).

## Getting started

```powershell
# 1. Download the core (placed in vendor\)
powershell -ExecutionPolicy Bypass -File .\scripts\download-singbox.ps1

# 2. Build
dotnet build V2RayFullTunnel.sln -c Release

# 3. Copy the core next to the UI output
Copy-Item .\vendor\sing-box.exe .\src\V2RayFullTunnel.UI\bin\Release\net8.0-windows\

# 4. Run (as Administrator)
.\src\V2RayFullTunnel.UI\bin\Release\net8.0-windows\V2RayFullTunnel.exe
```

Then paste a proxy link, click **Connect**, and use **Check IP** to confirm the exit
address. Close the window to keep running in the tray, or use the tray **Exit** item to
shut down and restore the network.

You can also create a desktop shortcut:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-shortcut.ps1
```

## Verified behavior

During development, on a test machine the following were confirmed: with the tunnel
connected the only default route was the TUN interface, host routes for the proxy server
and bootstrap DNS remained on the physical interface, and the observed exit address
matched the proxy. With the core stopped while the Kill Switch was active, direct
internet access was unavailable; after disconnect, the previous route state was
restored.

## Known limitations

- Windows only. The TUN and routing logic is Windows-specific.
- Requires elevation; it is not a background service yet. The
  `V2RayFullTunnel.Service` project is an early-stage scaffold.
- The Kill Switch is route-based, which fails closed but is not a packet filter. It does
  not block traffic by application or port.
- IPv6 is handled together with IPv4, but has been tested less thoroughly.
- The core must be provided by the user; it is not committed to this repository.

## Third-party components

This project runs, but does not modify, the following third-party software. Their
licenses apply to them; see [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for
details.

| Component | License | Copyright |
| --- | --- | --- |
| [sing-box](https://github.com/SagerNet/sing-box) | GPL-3.0-or-later | © nekohasekai / SagerNet |
| [Wintun](https://www.wintun.net/) (embedded in the sing-box binary) | Prebuilt Binaries License (GPL-2.0 for source) | © WireGuard LLC |
| .NET, WPF, Windows Forms | MIT | © .NET Foundation and contributors |

**Redistribution:** the sing-box core is GPL-3.0-or-later. It is **not** committed to
this repository and is downloaded by the user. If you redistribute a build of this
application together with the sing-box binary, you must comply with the GPL-3.0,
including providing the corresponding source of the bundled core and its license text.
The sing-box license additionally states that no derivative work may use its name or
imply association without prior consent; this project therefore does not present itself
as sing-box or as affiliated with SagerNet.

## Disclaimer

This software is provided for lawful use only. You are responsible for complying with
the laws and the terms of service that apply to you and for the networks you connect to.
The authors are not affiliated with sing-box, SagerNet, or WireGuard, and provide no
warranty of any kind.

## License

The source code of this project is released under the [MIT License](LICENSE).

---

Design by Soheil Saleh.
