# V2Ray Full Tunnel (Windows)

کلاینت اختصاصی ویندوز: TUN سراسری + Kill Switch فایروال. هسته `sing-box` است؛ ما پروتکل ننوشتیم.

## وضعیت این نسخه (اسکلت قدم ۱ تا ۴)

- `src/Common`: پارسر لینک VLESS/VMess/Trojan
- `src/Core`: سازنده کانفیگ sing-box (TUN + `strict_route` + DNS ضد نشت) + اجرای `sing-box.exe`
- `src/Firewall`: کلاس `KillSwitch` (Allow سرور/Loopback/LAN/DHCP و بعد Block کل خروجی)
- `src/UI`: پنجره WPF (اتصال/قطع/بررسی IP/بازیابی شبکه)
- `src/Service`: ورکر سرویس ویندوز (نظارت + اعمال فایروال)
- `scripts/`: دانلود sing-box، تست تونل، اعمال/حذف فایروال

## پیش‌نیاز (روی این سیستم هنوز نصب نیست)

روی این ماشین `dotnet` پیدا نشد. برای بیلد یکی را نصب کن:

1. Visual Studio 2022 + workload «.NET Desktop Development» (توصیه‌شده برای WPF)، یا
2. فقط SDK دات‌نت ۸:
   ```powershell
   powershell -ExecutionPolicy Bypass -File "$env:TEMP\opencode\dotnet-install.ps1" -Channel 8.0 -InstallDir "$env:ProgramFiles\dotnet"
   ```

## شروع قدم‌به‌قدم

```powershell
# ۱. اجرا به‌صورت Administrator
Set-ExecutionPolicy Bypass -Scope Process -Force

# ۲. هسته آماده است: vendor/sing-box.exe نسخه 1.14.3 (تست شده: sing-box check روی
#    configs/sing-box-template.json با موفقیت پاس می‌شود). برای به‌روزرسانی:
.\scripts\download-singbox.ps1

# ۳. لینکت را بده و کانفیگ تست بساز (فعلا دستی):
#    برنامه UI را اجرا کن، لینک را بچسبان، Connect بزن.
#    یا configs/sing-box-template.json را با IP/UUID واقعی پر کن و کپی کن به configs/sing-box-test.json

# ۴. تست بدون UI
.\scripts\test-tunnel.ps1 -Config ..\configs\sing-box-test.json

# ۵. بیلد (بعد از نصب SDK)
dotnet build V2RayFullTunnel.sln -c Release
# خروجی UI: src/V2RayFullTunnel.UI/bin/Release/net8.0-windows/
# sing-box.exe و wintun.dll را کنار فایل UI کپی کن و با Run as Administrator اجرا کن.

# ۶. تست Kill Switch (اول در VM!)
.\scripts\firewall-apply.ps1 -ServerIp 1.2.3.4 -ServerPort 443
.\scripts\firewall-restore.ps1   # بازیابی اضطراری
```

## ترتیب امن (مهم)

1. تونل را وصل و IP خروجی را تایید کن، بعد فایروال Block را بگذار.
2. قطع sing-box = اینترنت مستقیم قطع می‌ماند (برنمی‌گردد).
3. «بازیابی شبکه» فقط با دکمه عمدی کاربر.
4. DHCP/LAN/Loopback را نبند وگرنه شبکه محلی می‌میرد.

## قدم بعدی

- [ ] تست واقعی با یک لینک سالم (قدم ۱)
- [ ] تست TUN و `route print` (قدم ۲)
- [ ] تست نشت DNS/IPv6 در ipleak.net (قدم ۳)
- [ ] جدا کردن کامل Service از UI با Named Pipe (قدم ۵)
