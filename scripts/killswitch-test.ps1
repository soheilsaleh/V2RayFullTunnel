#Requires -RunAsAdministrator
# تست خودکار و ایزوله KillSwitch مسیری؛ همه نتایج در result.txt
# کاملاً بدون کار دستی: handshake اثبات‌شده، شبیه‌سازی کرش با force-kill، بازیابی خودکار.
param([string]$ServerIp = "95.38.198.67", [string]$BootstrapDns = "223.5.5.5")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$Log = Join-Path $PSScriptRoot "result.txt"
$StateFile = Join-Path $PSScriptRoot "killswitch-state.json"
if (Test-Path $StateFile) { throw "حالت قبلی مانده؛ اول .\scripts\firewall-restore.ps1 را اجرا کن." }

# در این محیط، هر خروجی stderr برنامه‌ها کشنده است؛ پس curl همیشه --silent و بدون
# --show-error اجرا می‌شود (stderr همیشه خالی می‌ماند) و خروجی خالی یعنی شکست.
function TryCurl([string]$url, [string]$proxy = "", [int]$timeout = 15) {
  $extra = @()
  if ($proxy) { $extra += @("--proxy", $proxy) }
  $o = ((curl.exe --silent --max-time $timeout @extra $url 2>$null) | Out-String).Trim()
  if ([string]::IsNullOrWhiteSpace($o)) { return "FAILED (no answer)" } else { return $o }
}
Get-Process sing-box -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
# صبر تا پروسس‌ها واقعاً بمیرند و هندل درایور آزاد شود (وگرنه ساخت آداپتور جدید با "already exists" می‌میرد)
for ($i = 0; $i -lt 15; $i++) {
  $p = @(Get-Process sing-box -ErrorAction SilentlyContinue).Count
  $a = Get-NetAdapter -Name "v2-tun" -ErrorAction SilentlyContinue
  if ($p -eq 0 -and -not $a) { break }
  Start-Sleep 1
}
# آداپتور یتیمِ بدون پروسس (از force-kill قبلی) را غیرفعال کن تا نمونه جدید بسازد
$stale = Get-NetAdapter -Name "v2-tun" -ErrorAction SilentlyContinue
if ($stale -and @(Get-Process sing-box -ErrorAction SilentlyContinue).Count -eq 0) {
  "آداپتور stale پیدا شد؛ disable می‌شود..." | Out-File -Append $Log
  Disable-NetAdapter -Name "v2-tun" -Confirm:$false -ErrorAction SilentlyContinue
  Start-Sleep 2
}
Get-NetRoute -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like "v2-tun*" } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
"=== $(Get-Date -Format 'HH:mm:ss') شروع تست ===" | Out-File $Log

function Dump($title) {
  "--- $title ---" | Out-File -Append $Log
  Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
    Select-Object DestinationPrefix, NextHop, InterfaceIndex, InterfaceAlias, RouteMetric | Out-File -Append $Log
  Get-NetRoute -DestinationPrefix "$ServerIp/32" -ErrorAction SilentlyContinue |
    Select-Object DestinationPrefix, NextHop, InterfaceIndex, InterfaceAlias | Out-File -Append $Log
  Get-NetRoute -DestinationPrefix "$BootstrapDns/32" -ErrorAction SilentlyContinue |
    Select-Object DestinationPrefix, NextHop, InterfaceIndex, InterfaceAlias | Out-File -Append $Log
}

# ۱. اسنپ‌شات مسیرهای فیزیکی، وقتی هنوز TUN بالا نیست
$snap = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "0.0.0.0" -and $_.InterfaceAlias -notlike "v2-tun*" })
if ($snap.Count -eq 0) { throw "مسیر پیش‌فرض نیست؛ اینترنت وصل نیست." }
$gw = [string]$snap[0].NextHop; $ifx = [int]$snap[0].InterfaceIndex
"گیت‌وی فیزیکی: $gw (if $ifx)" | Out-File -Append $Log
Dump "قبل از TUN"

# ۲. اجرای sing-box
# اجرای headless با ضبط لاگ در فایل (برای تشخیص نقطه شکست)
$SbOut = Join-Path $PSScriptRoot "tunnel-out.log"
$SbErr = Join-Path $PSScriptRoot "tunnel-err.log"
Remove-Item $SbOut, $SbErr -Force -ErrorAction SilentlyContinue
$sbx = Start-Process -FilePath (Join-Path (Get-Location).Path "vendor\sing-box.exe") -ArgumentList "run","-c",(Join-Path (Get-Location).Path "configs\sing-box-germany1.json") -RedirectStandardOutput $SbOut -RedirectStandardError $SbErr -NoNewWindow -PassThru
try {
  $up = $false
  for ($i = 0; $i -lt 15 -and -not $up; $i++) {
    Start-Sleep 2
    $t = Get-NetAdapter -Name "v2-tun" -ErrorAction SilentlyContinue
    $up = $t -and $t.Status -eq "Up"
  }
  if (-not $up) { throw "v2-tun بالا نیامد. tunnel-err.log را ببین؛ اگر خطای adapter already exists تکرار شد، VM را ریبوت کن." }
  $ip1 = ""
  for ($i = 0; $i -lt 20 -and $ip1 -ne "91.107.246.176"; $i++) {
    if (-not (Get-Process sing-box -ErrorAction SilentlyContinue)) { throw "پروسس sing-box مرد؛ لاگ پنجره sing-box را بفرست." }
    $ip1 = TryCurl "https://api.ipify.org" "http://127.0.0.1:10808" 5
    if ($ip1 -ne "91.107.246.176") { Start-Sleep 2 }
  }
  "گیت handshake (پراکسی، انتظار 91.107.246.176): $ip1" | Out-File -Append $Log
  if ($ip1 -ne "91.107.246.176") { throw "هندشیک با سرور کامل نشد. آخرین جواب: $ip1" }
  Dump "بعد از TUN (قبل از KillSwitch)"

  # ۳. KillSwitch: حذف فقط مسیرهای فیزیکی اسنپ‌شات‌شده (نه مسیر خود TUN)
  foreach ($r in $snap) {
    Remove-NetRoute -DestinationPrefix "0.0.0.0/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -Confirm:$false -ErrorAction Stop
  }
  $v6snap = @(Get-NetRoute -DestinationPrefix "::/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne "::" -and $_.InterfaceAlias -notlike "v2-tun*" })
  foreach ($r in $v6snap) {
    Remove-NetRoute -DestinationPrefix "::/0" -NextHop $r.NextHop -InterfaceIndex $r.InterfaceIndex -Confirm:$false -ErrorAction Stop
  }
  New-NetRoute -DestinationPrefix "$ServerIp/32" -NextHop $gw -InterfaceIndex $ifx -RouteMetric 5 -Confirm:$false -ErrorAction Stop | Out-Null
  New-NetRoute -DestinationPrefix "$BootstrapDns/32" -NextHop $gw -InterfaceIndex $ifx -RouteMetric 5 -Confirm:$false -ErrorAction Stop | Out-Null
  @{ v4 = @($snap | Select-Object NextHop, InterfaceIndex, RouteMetric); v6 = @($v6snap | Select-Object NextHop, InterfaceIndex, RouteMetric); serverIp = $ServerIp; bootstrapDns = $BootstrapDns } | ConvertTo-Json -Depth 4 | Set-Content $StateFile -Encoding UTF8
  Dump "بعد از KillSwitch"
  $tcpOs = Test-NetConnection -ComputerName $ServerIp -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue
  "OS TCP به سرور بعد از KillSwitch: $tcpOs" | Out-File -Append $Log
  try {
    $dnsOs = Resolve-DnsName "api.ipify.org" -Server $BootstrapDns -DnsOnly -ErrorAction Stop | Select-Object -First 1 -ExpandProperty IPAddress
    "OS DNS بوت‌استرپ ($BootstrapDns): $dnsOs" | Out-File -Append $Log
  } catch { "OS DNS بوت‌استرپ FAIL: $($_.Exception.Message)" | Out-File -Append $Log }
  Start-Sleep 5
  $t1 = ""
  for ($i = 0; $i -lt 3 -and [string]::IsNullOrWhiteSpace($t1); $i++) {
    $t1 = TryCurl "https://api.ipify.org" "" 10
    if ([string]::IsNullOrWhiteSpace($t1)) { Start-Sleep 3 }
  }
  if ([string]::IsNullOrWhiteSpace($t1)) { $t1 = "FAILED (no answer)" }
  $alive1 = @(Get-Process sing-box -ErrorAction SilentlyContinue).Count
  "تست ۱ (با تونل، انتظار 91.107.246.176؛ پروسس زنده: $alive1): $t1" | Out-File -Append $Log
  "--- sing-box stderr (30 خط آخر) ---" | Out-File -Append $Log
  Get-Content -LiteralPath $SbErr -ErrorAction SilentlyContinue | Select-Object -Last 30 | Out-File -Append $Log

  Write-Host "شبیه‌سازی کرش: sing-box با force-kill متوقف می‌شود..."
  Stop-Process -Name sing-box -Force -ErrorAction Stop
  Start-Sleep 2
  Dump "بعد از قطع sing-box"
  $t2 = TryCurl "https://api.ipify.org" "" 10
  if ([string]::IsNullOrWhiteSpace($t2)) { $t2 = "FAILED (no answer)" }
  "تست ۲ (بدون تونل، انتظار خطا): $t2" | Out-File -Append $Log
} finally {
  & (Join-Path $PSScriptRoot "firewall-restore.ps1")
}
$t3 = TryCurl "https://api.ipify.org" "" 15
if ([string]::IsNullOrWhiteSpace($t3)) { $t3 = "FAILED (no answer)" }
"تست ۳ (بعد از بازیابی، انتظار IP ایران): $t3" | Out-File -Append $Log
try { Stop-Process -Id $sbx.Id -Force -ErrorAction SilentlyContinue } catch {}
"=== پایان ===" | Out-File -Append $Log
Get-Content $Log
