#Requires -RunAsAdministrator
# Kill Switch مبتنی بر جدول مسیریابی (قدم ۳)
# چرا نه فایروال؟ فایروال ویندوز روی رابط TUN هم فیلتر می‌کند و با DefaultOutboundAction=Block
# ترافیک داخل تونل هم خفه می‌شود. در این روش مسیر پیش‌فرض فیزیکی حذف می‌شود:
# بدون TUN هیچ ترافیکی راه به بیرون ندارد؛ فقط مسیر /32 به سرور و DNS بوت‌استرپ می‌ماند.
# (مسیرها با ریبوت پاک می‌شوند = fail-safe)
param([string]$ServerIp = "1.2.3.4", [string]$BootstrapDns = "223.5.5.5", [string]$StateFile = "")
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($StateFile)) { $StateFile = Join-Path $PSScriptRoot "killswitch-state.json" }
if (Test-Path $StateFile) { throw "حالت قبلی پاک نشده ($StateFile). اول restore را اجرا کن." }

# اگر اجرای قبلی نصفه مانده و مسیر پیش‌فرض نیست، از کانفیگ اینترفیس بازیابی کن
$v4defs = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "0.0.0.0" })
if ($v4defs.Count -eq 0) {
  $cfg = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1
  if ($cfg -and $cfg.IPv4DefaultGateway) {
    New-NetRoute -DestinationPrefix "0.0.0.0/0" -NextHop $cfg.IPv4DefaultGateway.NextHop -InterfaceIndex $cfg.InterfaceIndex -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
$v4defs = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "0.0.0.0" -and $_.InterfaceAlias -notlike "v2-tun*" })
  }
}
if ($v4defs.Count -eq 0) { throw "مسیر پیش‌فرض پیدا نشد و بازیابی خودکار ممکن نبود؛ ریبوت کن." }

$state = @{ v4 = @(); v6 = @(); serverIp = $ServerIp; bootstrapDns = $BootstrapDns }
foreach ($r in $v4defs) {
  $state.v4 += ,@{ NextHop = [string]$r.NextHop; InterfaceIndex = [int]$r.InterfaceIndex; RouteMetric = [int]$r.RouteMetric }
  Remove-NetRoute -DestinationPrefix "0.0.0.0/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -Confirm:$false -ErrorAction Stop
}
$v6defs = @(Get-NetRoute -DestinationPrefix "::/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "::" -and $_.InterfaceAlias -notlike "v2-tun*" })
foreach ($r in $v6defs) {
  $state.v6 += ,@{ NextHop = [string]$r.NextHop; InterfaceIndex = [int]$r.InterfaceIndex; RouteMetric = [int]$r.RouteMetric }
  Remove-NetRoute -DestinationPrefix "::/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -Confirm:$false -ErrorAction Stop
}

$gw = $state.v4[0].NextHop; $ifx = [int]$state.v4[0].InterfaceIndex
Remove-NetRoute -DestinationPrefix "$ServerIp/32" -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetRoute -DestinationPrefix "$BootstrapDns/32" -Confirm:$false -ErrorAction SilentlyContinue
# InterfaceIndex صریح لازم است وگرنه New-NetRoute منتظر ورودی تعاملی می‌ماند
New-NetRoute -DestinationPrefix "$ServerIp/32" -NextHop $gw -InterfaceIndex $ifx -RouteMetric 5 -Confirm:$false -ErrorAction Stop | Out-Null
New-NetRoute -DestinationPrefix "$BootstrapDns/32" -NextHop $gw -InterfaceIndex $ifx -RouteMetric 5 -Confirm:$false -ErrorAction Stop | Out-Null

$state | ConvertTo-Json -Depth 4 | Set-Content -Path $StateFile -Encoding UTF8
$ok1 = @(Get-NetRoute -DestinationPrefix "$ServerIp/32" -ErrorAction SilentlyContinue).Count
$ok2 = @(Get-NetRoute -DestinationPrefix "$BootstrapDns/32" -ErrorAction SilentlyContinue).Count
if ($ok1 -eq 0 -or $ok2 -eq 0) { throw "ساخت مسیرهای /32 ناموفق بود." }
$tunDef = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like "v2-tun*" }).Count
if ($tunDef -eq 0) { Write-Host "هشدار: مسیر پیش‌فرض TUN دیده نشد؛ اگر TUN روشن است ممکن است آسیب دیده باشد." }
Write-Host "KillSwitch (مسیری) اعمال شد: $($state.v4.Count) مسیر v4 و $($state.v6.Count) مسیر v6 حذف؛ $ServerIp/32 و $BootstrapDns/32 حفظ شدند."
