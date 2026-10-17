# Third-Party Notices

This project uses the third-party software listed below. Each component remains under
its own license and copyright. This file is provided for attribution and compliance.

This project is **not** affiliated with, endorsed by, or a derivative work of any of the
projects listed here.

---

## 1. sing-box

- Project: https://github.com/SagerNet/sing-box
- Used as: an external process (`sing-box.exe`). This project generates a configuration
  file and supervises the process; it does not link against sing-box code.
- License: **GNU General Public License v3.0 or later (GPL-3.0-or-later)**
- Copyright: © 2022 nekohasekai / SagerNet

The sing-box distribution also carries the following additional term, quoted from its
`LICENSE`:

> In addition, no derivative work may use the name or imply association with this
> application without prior consent.

The sing-box binary is **not** included in this repository. It is downloaded by the user
via `scripts/download-singbox.ps1`.

**If you redistribute this application together with a sing-box binary**, you must
comply with GPL-3.0-or-later for that binary: provide the complete corresponding source
code (or a written offer to provide it) and a copy of the GPL-3.0 license text. The full
license text is available at <https://www.gnu.org/licenses/gpl-3.0.txt> and in the
sing-box source tree.

---

## 2. Wintun

- Project: https://www.wintun.net/ and https://github.com/WireGuard/wintun
- Used as: the TUN driver for Windows. The signed `wintun.dll` is **embedded inside the
  sing-box binary** (the `sing-tun` component loads it internally). This project does not
  ship or invoke `wintun.dll` directly.
- Source license: **GPL-2.0**
- Prebuilt binaries license: **Prebuilt Binaries License** (permissive)
- Copyright: © 2018-2021 WireGuard LLC. All Rights Reserved.

Per wintun.net, the signed prebuilt binaries are the only supported way to distribute
Wintun and are released under a more permissive license than GPL-2.0. Relevant terms
(summary; see the license file in the official Wintun archive for the full text):

- You may use the prebuilt `wintun.dll` for lawful purposes.
- You must not reverse engineer, decompile, disassemble, extract from, or modify it.
- You must not remove proprietary notices, labels, or copyrights.
- It may be redistributed **only** as part of other software that uses it solely via the
  permitted API (as sing-box does).

Because this project satisfies these conditions only through the sing-box binary, any
redistribution obligation for Wintun is handled by distributing the unmodified sing-box
binary as obtained from its official release.

---

## 3. .NET, WPF, and Windows Forms

- Project: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- Used as: application framework and runtime.
- License: **MIT**
- Copyright: © .NET Foundation and contributors.

---

## Notes on the project name

This application is named `V2RayFullTunnel` for historical reasons. It does **not**
contain, link, or bundle V2Ray or Xray code. Its proxy core is sing-box.
