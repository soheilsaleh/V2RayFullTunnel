# ساخت شورتکات دسکتاپ برای V2Ray Full Tunnel
# برنامه خودش با manifest دسترسی ادمین می‌خواهد، پس شورتکات معمولی کافی است.
param([switch]$Remove)

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\src\V2RayFullTunnel.UI\bin\Release\net8.0-windows\V2RayFullTunnel.exe"
$exe = (Resolve-Path $exe).Path
$core = Join-Path $PSScriptRoot "..\vendor\sing-box.exe"
if (Test-Path $core) { Copy-Item $core (Split-Path $exe) -Force }

$desktop = [Environment]::GetFolderPath("Desktop")
$lnk = Join-Path $desktop "V2Ray Full Tunnel.lnk"
if ($Remove) { if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "شورتکات حذف شد." }; return }

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = Split-Path $exe
$sc.Description = "V2Ray Full Tunnel - تونل سراسری با KillSwitch"
$sc.IconLocation = "$exe,0"
$sc.WindowStyle = 1
$sc.Save()
Write-Host "شورتکات ساخته شد: $lnk"