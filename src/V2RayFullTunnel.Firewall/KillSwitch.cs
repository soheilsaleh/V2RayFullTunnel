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
            catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(1000); }
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
            for (int i = 0; i < attempts; i++)
            {
                System.Threading.Thread.Sleep(i == 0 ? 1500 : 2000); // آزاد شدن هندل درایور
                Ps("Get-NetRoute -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -like 'v2-tun*' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; exit 0");

                string json = Ps("Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'v2-tun*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.InterfaceDescription -like '*sing-tun*' } | Select-Object Name | ConvertTo-Json -Compress; exit 0");
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null" || json.Trim() == "[]")
                    return true; // چیزی برای پاکسازی نمانده

                Ps("Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'v2-tun*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.InterfaceDescription -like '*sing-tun*' } | ForEach-Object { Disable-NetAdapter -Name $_.Name -Confirm:$false -ErrorAction SilentlyContinue }; exit 0");
                System.Threading.Thread.Sleep(800);
                Ps("Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'v2-tun*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.InterfaceDescription -like '*sing-tun*' } | ForEach-Object { Remove-NetAdapter -Name $_.Name -Confirm:$false -ErrorAction SilentlyContinue }; exit 0");
                System.Threading.Thread.Sleep(800);
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

    public static void Apply(string serverHost, int serverPort, string coreExePath)
    {
        string remoteIp = ResolveServerIp(serverHost);
        Directory.CreateDirectory(DataDir);
        // حالت نیمه‌کاره از اجرای قبلی: مسیرهای ذخیره‌شده متعلق به خودمان است، پس
        // به‌جای خطای «اول Restore بزن» خودمان برمی‌گردانیم و ادامه می‌دهیم.
        if (File.Exists(StatePath))
        {
            Restore();
            throw new InvalidOperationException("Stale network state from a previous run was restored. Please try Connect again.");
        }

        var gw = GetDefaultGateway();
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

        var v4 = ParseRoutes(Run("Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias | ConvertTo-Json"));
        var v6 = ParseRoutes(Run("Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '::' -and $_.InterfaceAlias -notlike 'v2-tun*' } | Select-Object NextHop,InterfaceIndex,RouteMetric,InterfaceAlias | ConvertTo-Json"));
        var removed = new List<(string Prefix, SavedRoute R)>();
        var created = new List<string>();
        try
        {
            // ۱. اول استثناها (idempotent)
            // NOTE: حذف مسیر ناموجود حتی با SilentlyContinue کد خروج ۱ می‌دهد؛ پس exit 0 صریح.
            Run($"Remove-NetRoute -DestinationPrefix '{remoteIp}/32' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            Run($"Remove-NetRoute -DestinationPrefix '{BootstrapDns}/32' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            Run($"New-NetRoute -DestinationPrefix '{remoteIp}/32' -NextHop '{gw.NextHop}' -InterfaceIndex {gw.InterfaceIndex} -RouteMetric 5 -Confirm:$false");
            created.Add(remoteIp + "/32");
            Run($"New-NetRoute -DestinationPrefix '{BootstrapDns}/32' -NextHop '{gw.NextHop}' -InterfaceIndex {gw.InterfaceIndex} -RouteMetric 5 -Confirm:$false");
            created.Add(BootstrapDns + "/32");
            Run($"if (@(Get-NetRoute -DestinationPrefix '{remoteIp}/32' -ErrorAction SilentlyContinue).Count -eq 0) {{ exit 1 }}");
            Run($"if (@(Get-NetRoute -DestinationPrefix '{BootstrapDns}/32' -ErrorAction SilentlyContinue).Count -eq 0) {{ exit 1 }}");

            // ۲. بعد حذف defaultهای فیزیکی (نه TUN)
            foreach (var r in v4)
            {
                Run($"Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false");
                removed.Add(("0.0.0.0/0", r));
            }
            foreach (var r in v6)
            {
                Run($"Remove-NetRoute -DestinationPrefix '::/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false");
                removed.Add(("::/0", r));
            }

            File.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(new SavedState(v4, v6, remoteIp, BootstrapDns)));
        }
        catch (Exception ex)
        {
            foreach (var (prefix, r) in removed)
                Ps($"New-NetRoute -DestinationPrefix '{prefix}' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false -ErrorAction SilentlyContinue");
            foreach (var c in created)
                Ps($"Remove-NetRoute -DestinationPrefix '{c}' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
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
            Ps($"Remove-NetRoute -DestinationPrefix '{state.ServerIp}/32' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            if (!string.IsNullOrEmpty(state.BootstrapIp))
                Ps($"Remove-NetRoute -DestinationPrefix '{state.BootstrapIp}/32' -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
            foreach (var r in state.V4.Where(x => !string.IsNullOrEmpty(x.NextHop) && x.InterfaceIndex > 0))
            {
                Ps($"Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
                // اگر همان مسیر از قبل هست (مثلاً DHCP برگردانده) دوباره نساز
                string exists = Ps($"@(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{r.NextHop}' -and $_.InterfaceIndex -eq {r.InterfaceIndex} }}).Count; exit 0");
                if (exists.Trim() != "0") continue;
                Ps($"New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false");
            }
            foreach (var r in state.V6.Where(x => !string.IsNullOrEmpty(x.NextHop) && x.InterfaceIndex > 0))
            {
                Ps($"Remove-NetRoute -DestinationPrefix '::/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -Confirm:$false -ErrorAction SilentlyContinue; exit 0");
                string exists = Ps($"@(Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{r.NextHop}' -and $_.InterfaceIndex -eq {r.InterfaceIndex} }}).Count; exit 0");
                if (exists.Trim() != "0") continue;
                Ps($"New-NetRoute -DestinationPrefix '::/0' -NextHop '{r.NextHop}' -InterfaceIndex {r.InterfaceIndex} -RouteMetric {r.RouteMetric} -Confirm:$false");
            }
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
        var list = new List<SavedRoute>();
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return list;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : Enumerable.Repeat(doc.RootElement, 1);
        foreach (var el in items)
        {
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
