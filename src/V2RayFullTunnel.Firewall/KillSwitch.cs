using System.Diagnostics;
using System.Net;

namespace V2RayFullTunnel.Firewall;

/// <summary>
/// Kill Switch مبتنی بر جدول مسیریابی (قدم ۳).
/// اتمیک: اول استثناهای /32 ساخته و راستی‌آزمایی می‌شوند، بعد default فیزیکی حذف می‌شود؛
/// در هر خطا rollback کامل انجام می‌شود. مسیر خود TUN هرگز حذف نمی‌شود.
/// </summary>
public static class KillSwitch
{
    public sealed record SavedRoute(string NextHop, int InterfaceIndex, ushort RouteMetric, string InterfaceAlias);
    public sealed record SavedState(List<SavedRoute> V4, List<SavedRoute> V6, string ServerIp, string BootstrapIp);
    public sealed record DefaultGateway(string NextHop, int InterfaceIndex, string InterfaceAlias);

    private const string BootstrapDns = "223.5.5.5"; // باید با DNS کانفیگ یکی باشد
    private const string TunAlias = "v2-tun";

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "V2RayFullTunnel");
    private static string StatePath => Path.Combine(DataDir, "killswitch-state.json");

    /// <summary>گیت‌وی پیش‌فرض فیزیکی واقعی (کمترین متریک، بدون TUN). برای bind و KillSwitch.</summary>
    public static DefaultGateway GetDefaultGateway()
    {
        string json = Ps("Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Sort-Object RouteMetric | Select-Object -First 1 NextHop,InterfaceIndex,InterfaceAlias | ConvertTo-Json");
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            throw new InvalidOperationException("مسیر پیش‌فرض فیزیکی پیدا نشد؛ اینترنت وصل نیست؟");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DefaultGateway(
            r.GetProperty("NextHop").GetString() ?? "",
            r.GetProperty("InterfaceIndex").GetInt32(),
            r.GetProperty("InterfaceAlias").GetString() ?? "");
    }

    /// <summary>
    /// گیت‌وی/اینترفیس واقعی برای رسیدن به یک IP مشخص.
    /// نکته‌ی حیاتی: گیتویی انتخاب می‌شود که «on-link» باشد (داخل سابنت همان اینترفیس)،
    /// چون بعد از حذف مسیر پیش‌فرض فیزیکی، فقط گیتویِ on-link قابل دسترسی می‌ماند
    /// (گیتوی‌های شبح مانند 10.232.118.45 بیرون سابنت، باعث «no route to internet» می‌شوند).
    /// </summary>
    public static DefaultGateway GetGatewayTo(string ip)
    {
        string script = @"
$src = Find-NetRoute -RemoteIPAddress '" + ip + @"' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop } | Select-Object -First 1
if (-not $src) { exit 1 }
$ifIdx = $src.InterfaceIndex
$onlink = @(Get-NetRoute -InterfaceIndex $ifIdx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -eq '0.0.0.0' -and $_.DestinationPrefix -ne '0.0.0.0/0' } | Select-Object -ExpandProperty DestinationPrefix)
$defs = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.NextHop -ne '127.0.0.1' } | Sort-Object RouteMetric)
$best = $null
foreach ($d in $defs) {
    $gwb = [System.Net.IPAddress]::Parse($d.NextHop).GetAddressBytes()
    foreach ($ol in $onlink) {
        $parts = $ol.Split('/'); $nb = [System.Net.IPAddress]::Parse($parts[0]).GetAddressBytes(); $len = [int]$parts[1]
        $ok = $true
        for ($i = 0; $i -lt 4; $i++) {
            $bits = [Math]::Min(8, [Math]::Max(0, $len - $i * 8))
            if ($bits -le 0) { break }
            $mask = if ($bits -ge 8) { 255 } else { 256 - [int][Math]::Pow(2, 8 - $bits) }
            if (($gwb[$i] -band $mask) -ne ($nb[$i] -band $mask)) { $ok = $false; break }
        }
        if ($ok) { $best = $d; break }
    }
    if ($best) { break }
}
if (-not $best) { $best = $src }
@{ NextHop = $best.NextHop; InterfaceIndex = $best.InterfaceIndex; InterfaceAlias = $best.InterfaceAlias } | ConvertTo-Json -Compress
exit 0
";
        string json = Ps(script);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            throw new InvalidOperationException($"مسیر رسیدن به {ip} پیدا نشد؛ اینترنت وصل نیست؟");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DefaultGateway(
            r.GetProperty("NextHop").GetString() ?? "",
            r.GetProperty("InterfaceIndex").GetInt32(),
            r.GetProperty("InterfaceAlias").GetString() ?? "");
    }

    /// <summary>رزولو IP سرور با retry؛ حتماً قبل از روشن شدن TUN صدا بزن.</summary>
    public static string ResolveServerIp(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var ip = Dns.GetHostAddresses(host)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ip != null) return ip.ToString();
                last = new InvalidOperationException("پاسخی برنگشت.");
            }
            catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(300); }
            if (i == 2) throw new InvalidOperationException($"IP سرور {host} پیدا نشد: {last?.Message}");
        }
        throw new InvalidOperationException($"IP سرور {host} پیدا نشد.");
    }

    /// <summary>
    /// پاک‌سازی بازمانده‌های آداپتور TUN. بعد از هر kill (حتی تمیز) ممکن است
    /// شیء آداپتور در درایور باقی بماند و sing-box نتواند دوباره بسازد
    /// ("create adapter: already exists / open existing adapter: Element not found").
    /// بدون این، فقط ریبوت سیستم مشکل را حل می‌کند. true یعنی تمیز شد.
    /// </summary>
    public static bool CleanupTunnelResidue(int attempts = 4)
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("sing-box").Length > 0) return false;

            // مسیر سریع: اگر بازمانده‌ای نیست، هیچ ثانیه‌ای تلف نشود (قبلاً همیشه ۱.۵ ثانیه می‌خوابید).
            const string filter = "$_.Name -like 'v2-tun*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.InterfaceDescription -like '*sing-tun*'";
            string probe = Ps($"Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {{ {filter} }} | Select-Object Name | ConvertTo-Json -Compress; exit 0");
            if (string.IsNullOrWhiteSpace(probe) || probe.Trim() == "null" || probe.Trim() == "[]")
                return true;

            for (int i = 0; i < attempts; i++)
            {
                System.Threading.Thread.Sleep(i == 0 ? 600 : 1500); // آزاد شدن هندل درایور
                Ps("Get-NetRoute -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like 'v2-tun*' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; exit 0");

                string json = Ps($"Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {{ {filter} }} | Select-Object Name | ConvertTo-Json -Compress; exit 0");
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null" || json.Trim() == "[]")
                    return true; // چیزی برای پاکسازی نمانده

                Ps($"Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {{ {filter} }} | ForEach-Object {{ Disable-NetAdapter -Name $_.Name -Confirm:$false -ErrorAction SilentlyContinue }}; exit 0");
                System.Threading.Thread.Sleep(400);
                Ps($"Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {{ {filter} }} | ForEach-Object {{ Remove-NetAdapter -Name $_.Name -Confirm:$false -ErrorAction SilentlyContinue }}; exit 0");
                System.Threading.Thread.Sleep(400);
                // آخرین شانس: دستگاه شبح (Status=Unknown) فقط با حذف PnP پاک می‌شود.
                // نام دستگاه ممکن است «sing-tun Tunnel» یا «Wintun» باشد، پس روی InstanceId هم فیلتر می‌کنیم.
                Ps("Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like '*Wintun*' -or $_.FriendlyName -like '*tun*Tunnel*' -or $_.InstanceId -like '*WINTUN*' } | ForEach-Object { pnputil /remove-device $_.InstanceId /subtree | Out-Null }; exit 0");
            }
            return false;
        }
        catch { return false; }
    }

    public static bool HasPendingState() => File.Exists(StatePath);

    /// <summary>بازیابی خودکار حالت نیمه‌کاره از اجرای قبلی (بدون تونل فعال).</summary>
    public static void RecoverStaleState()
    {
        if (!File.Exists(StatePath)) return;
        Restore();
    }

    /// <param name="remoteIp">IP سروری که قبل از بالا آمدن TUN رزولو شده.</param>
    /// <param name="gw">گیت‌وی/اینترفیسِ واقعیِ رسیدن به سرور، که باید قبل از بالا آمدن TUN گرفته شود
    /// (بعد از روشن شدن TUN، مسیر سرور از داخل تونل است و دیگر قابل تشخیص نیست).</param>
    public static void Apply(string remoteIp, DefaultGateway gw, string coreExePath)
    {
        Directory.CreateDirectory(DataDir);
        // اگر وضعیتی از قبل ذخیره شده (اتصال فعال یا ریکانکت)، اسنپ‌شات اصلی کاربر حفظ می‌شود.
        // مسیرهای پیش‌فرضی که در این فاصله دوباره ظاهر شده‌اند به همان اسنپ‌شات merge می‌شوند
        // تا Disconnect نهایی همه‌چیز را برگرداند.
        string? priorJson = File.Exists(StatePath) ? File.ReadAllText(StatePath) : null;

        var transcript = new List<string> { $"gw={gw.NextHop} if={gw.InterfaceAlias} server={remoteIp}" };
        string Run(string cmd)
        {
            transcript.Add("> " + cmd);
            try
            {
                string o = Ps(cmd);
                transcript.Add("< ok (" + o.Trim().Length + " chars)");
                return o;
            }
            catch (Exception ex)
            {
                transcript.Add("< FAIL: " + ex.Message);
                throw;
            }
        }

        // هر دو اسنپ‌شات (IPv4 و IPv6) در یک پروسه‌ی پاورشل — نصف شدن زمان اعمال
        string snapJson = Run(@"
$v4 = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias)
$v6 = @(Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '::' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias)
[pscustomobject]@{ v4 = $v4; v6 = $v6 } | ConvertTo-Json -Depth 5 -Compress
");
        List<SavedRoute> v4, v6;
        using (var snap = System.Text.Json.JsonDocument.Parse(snapJson))
        {
            v4 = ParseRoutes(snap.RootElement.GetProperty("v4"));
            v6 = ParseRoutes(snap.RootElement.GetProperty("v6"));
        }

        // merge با اسنپ‌شات قبلی (اگر وجود دارد) تا در ریکانکت/تغییر شبکه چیزی گم نشود
        if (priorJson != null)
        {
            try
            {
                var prior = System.Text.Json.JsonSerializer.Deserialize<SavedState>(priorJson);
                if (prior != null)
                {
                    v4 = prior.V4.Concat(v4).Distinct().ToList();
                    v6 = prior.V6.Concat(v6).Distinct().ToList();
                }
            }
            catch { }
        }
        var removed = new List<(string Prefix, SavedRoute R)>();
        var created = new List<string>();
        try
        {
            // ۱. اول استثناها (idempotent) — همه در یک پروسه پاورشل تا سریع باشد
            // NOTE: حذف مسیر ناموجود حتی با SilentlyContinue کد خروج ۱ می‌دهد؛ پس exit 0 صریح.
            Run($@"$ErrorActionPreference='Continue';
Remove-NetRoute -DestinationPrefix '{remoteIp}/32' -Confirm:$false -ErrorAction SilentlyContinue;
Remove-NetRoute -DestinationPrefix '{BootstrapDns}/32' -Confirm:$false -ErrorAction SilentlyContinue;
New-NetRoute -DestinationPrefix '{remoteIp}/32' -NextHop '{gw.NextHop}' -InterfaceIndex {gw.InterfaceIndex} -RouteMetric 5 -Confirm:$false;
New-NetRoute -DestinationPrefix '{BootstrapDns}/32' -NextHop '{gw.NextHop}' -InterfaceIndex {gw.InterfaceIndex} -RouteMetric 5 -Confirm:$false;
if (@(Get-NetRoute -DestinationPrefix '{remoteIp}/32' -ErrorAction SilentlyContinue).Count -eq 0) {{ exit 1 }}
if (@(Get-NetRoute -DestinationPrefix '{BootstrapDns}/32' -ErrorAction SilentlyContinue).Count -eq 0) {{ exit 1 }}
exit 0");
            created.Add(remoteIp + "/32");
            created.Add(BootstrapDns + "/32");

            // ۲. بعد حذف defaultهای فیزیکی (نه TUN)
            // مقاوم: خطای «مسیر یافت نشد» نباید کل اتصال را خراب کند (چند مسیر پیش‌فرض روی ویندوز رایج است).
            if (v4.Count > 0)
            {
                Run("$ErrorActionPreference='Continue'; " + string.Join(" ", v4.Select(r =>
                    $"Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false -ErrorAction SilentlyContinue;")) + " exit 0");
                foreach (var r in v4) removed.Add(("0.0.0.0/0", r));
            }
            if (v6.Count > 0)
            {
                Run("$ErrorActionPreference='Continue'; " + string.Join(" ", v6.Select(r =>
                    $"Remove-NetRoute -DestinationPrefix '::/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false -ErrorAction SilentlyContinue;")) + " exit 0");
                foreach (var r in v6) removed.Add(("::/0", r));
            }

            File.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(new SavedState(v4, v6, remoteIp, BootstrapDns)));
        }
        catch (Exception ex)
        {
            foreach (var (prefix, r) in removed)
                Ps($"New-NetRoute -DestinationPrefix '{prefix}' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false -ErrorAction SilentlyContinue");
            foreach (var c in created)
                Ps($"Remove-NetRoute -DestinationPrefix '{c}' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            try
            {
                if (priorJson != null) File.WriteAllText(StatePath, priorJson);
                else if (File.Exists(StatePath)) File.Delete(StatePath);
            }
            catch { }
            throw new InvalidOperationException("KillSwitch ناتمام ماند و تغییرات برگردانده شد.\nمراحل:\n" + string.Join("\n", transcript), ex);
        }
    }

    public static void Restore(bool silent = false)
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = System.Text.Json.JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath))
                ?? throw new InvalidOperationException("فایل حالت خراب است.");
            // همه‌ی بازیابی در یک پروسه پاورشل (قبلاً هر مسیر ۲ پروسه و قطع‌وصل را کند می‌کرد).
            var sb = new System.Text.StringBuilder("$ErrorActionPreference='Continue';");
            sb.Append($"Remove-NetRoute -DestinationPrefix '{state.ServerIp}/32' -Confirm:$false -ErrorAction SilentlyContinue;");
            if (!string.IsNullOrEmpty(state.BootstrapIp))
                sb.Append($"Remove-NetRoute -DestinationPrefix '{state.BootstrapIp}/32' -Confirm:$false -ErrorAction SilentlyContinue;");
            foreach (var r in state.V4.Where(x => !string.IsNullOrEmpty(x.NextHop) && x.InterfaceIndex > 0))
                sb.Append($"if (-not (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{r.NextHop}' -and $_.InterfaceIndex -eq {r.InterfaceIndex} }})) {{ New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false -ErrorAction SilentlyContinue }};");
            foreach (var r in state.V6.Where(x => !string.IsNullOrEmpty(x.NextHop) && x.InterfaceIndex > 0))
                sb.Append($"if (-not (Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{r.NextHop}' -and $_.InterfaceIndex -eq {r.InterfaceIndex} }})) {{ New-NetRoute -DestinationPrefix '::/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false -ErrorAction SilentlyContinue }};");
            sb.Append("exit 0");
            Ps(sb.ToString());
            File.Delete(StatePath);
        }
        catch when (silent) { }
    }

    private static List<SavedRoute> SnapshotV4()
    {
        string json = Ps("Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias | ConvertTo-Json");
        return ParseRoutes(json);
    }

    private static List<SavedRoute> SnapshotV6()
    {
        string json = Ps("Get-NetRoute -DestinationPrefix '::/0' | Where-Object { $_.NextHop -ne '::' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias | ConvertTo-Json");
        return ParseRoutes(json);
    }

    private static void VerifyExists(string prefix)
    {
        string json = Ps($"@(Get-NetRoute -DestinationPrefix '{prefix}' -ErrorAction SilentlyContinue).Count; exit 0");
        if (json.Trim() == "0")
            throw new InvalidOperationException($"مسیر {prefix} ساخته نشد.");
    }

    private static List<SavedRoute> ParseRoutes(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return new List<SavedRoute>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return ParseRoutes(doc.RootElement);
    }

    private static List<SavedRoute> ParseRoutes(System.Text.Json.JsonElement root)
    {
        var list = new List<SavedRoute>();
        if (root.ValueKind == System.Text.Json.JsonValueKind.Null) return list;
        var items = root.ValueKind == System.Text.Json.JsonValueKind.Array
            ? root.EnumerateArray()
            : Enumerable.Repeat(root, 1);
        foreach (var el in items)
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            string alias = el.TryGetProperty("InterfaceAlias", out var al) ? al.GetString() ?? "" : "";
            list.Add(new SavedRoute(
                el.GetProperty("NextHop").GetString() ?? "",
                el.GetProperty("InterfaceIndex").GetInt32(),
                (ushort)el.GetProperty("RouteMetric").GetInt32(),
                alias));
        }
        return list;
    }

    private static string Ps(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference = 'Stop'; {command}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        string psout = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Route خطا (کد {p.ExitCode}): {err} {psout}".Trim());
        return psout;
    }
}
