#Requires -RunAsAdministrator
# دانلود آخرین sing-box ویندوز + wintun
$ErrorActionPreference = "Stop"
$vendor = Join-Path $PSScriptRoot "..\vendor" | Resolve-Path
$api = Invoke-RestMethod "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
$asset = $api.assets | Where-Object { $_.name -match "windows-amd64.*\.zip" } | Select-Object -First 1
if (-not $asset) { throw "فایل windows-amd64 پیدا نشد." }
Write-Host "دانلود: $($asset.name)"
$zip = Join-Path $env:TEMP $asset.name
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
$tmp = Join-Path $env:TEMP "sbx"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive $zip -DestinationPath $tmp
$exe = Get-ChildItem -Recurse $tmp -Filter "sing-box.exe" | Select-Object -First 1
$wintun = Get-ChildItem -Recurse $tmp -Filter "wintun.dll" | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $vendor "sing-box.exe") -Force
if ($wintun) { Copy-Item $wintun.FullName (Join-Path $vendor "wintun.dll") -Force }
Write-Host "OK -> vendor/sing-box.exe"
& (Join-Path $vendor "sing-box.exe") version
