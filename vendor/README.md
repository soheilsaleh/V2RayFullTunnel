# vendor

Binary files in this directory are **not** committed to the repository
(see `vendor/*.exe` and `vendor/*.dll` in `.gitignore`).

`sing-box.exe` (v1.14.3) is downloaded here by `scripts/download-singbox.ps1`.

The Wintun TUN driver is embedded inside the sing-box binary, so a separate
`wintun.dll` is not required.

See the repository's `THIRD_PARTY_NOTICES.md` for the licenses that apply to these
binaries.
