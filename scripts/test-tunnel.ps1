#Requires -RunAsAdministrator
# تست قدم ۱ و ۲: check + route + DNS + IP (بدون UI)
param([string]$Config = "")
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\vendor\sing-box.exe" | Resolve-Path
if ([string]::IsNullOrWhiteSpace($Config)) { $Config = Join-Path $PSScriptRoot "..\configs\sing-box-test.json" }
Write-Host "== sing-box check =="
& $exe check -c $Config
Write-Host "`n== route print (فقط خطوط 0.0.0.0) =="
route print -4 | Select-String "0.0.0.0"
Write-Host "`n== DNS =="
Resolve-DnsName google.com | Select-Object -First 4
Write-Host "`n== IP خروجی =="
Invoke-RestMethod https://api.ipify.org
Write-Host "`nنکته: برای تست TUN باید sing-box run با auto_route روشن باشد، بعد همین IP باید IP سرور شود."
