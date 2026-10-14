#Requires -RunAsAdministrator
# بازیابی جدول مسیریابی (حالت ذخیره‌شده توسط firewall-apply.ps1)
param([string]$StateFile = "")
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($StateFile)) { $StateFile = Join-Path $PSScriptRoot "killswitch-state.json" }
if (-not (Test-Path $StateFile)) { Write-Host "فایل حالت پیدا نشد؛ کاری انجام نشد. (اگر اینترنت نداری، ریبوت کن)"; return }
$state = Get-Content -Raw -Path $StateFile | ConvertFrom-Json
Remove-NetRoute -DestinationPrefix "$($state.serverIp)/32" -Confirm:$false -ErrorAction SilentlyContinue
if ($state.bootstrapDns) { Remove-NetRoute -DestinationPrefix "$($state.bootstrapDns)/32" -Confirm:$false -ErrorAction SilentlyContinue }
# @() لازم است: JSON تک‌عضوی اسکالر می‌شود و foreach روی نام فیلدها حلقه می‌زد
foreach ($r in @($state.v4)) { if ($r.NextHop -and $r.InterfaceIndex) { New-NetRoute -DestinationPrefix "0.0.0.0/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -RouteMetric $r.RouteMetric -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } }
foreach ($r in @($state.v6)) { if ($r.NextHop -and $r.InterfaceIndex) { New-NetRoute -DestinationPrefix "::/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -RouteMetric $r.RouteMetric -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } }
Remove-Item $StateFile -Force
# ته‌مانده مسیرهای TUN نمونه force-killشده هم پاک شود (وگرنه بعد از بازیابی ترافیک سیاه‌چاله می‌شود)
Get-NetRoute -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like "v2-tun*" } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
$have = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "0.0.0.0" }).Count
if ($have -eq 0) { throw "مسیر پیش‌فرض بازگردانده نشد؛ سیستم را ریبوت کن." }
Write-Host "جدول مسیریابی بازیابی شد ($have مسیر پیش‌فرض)."
