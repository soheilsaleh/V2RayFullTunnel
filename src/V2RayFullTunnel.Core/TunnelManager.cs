using System.Net;
using V2RayFullTunnel.Common;
using V2RayFullTunnel.Firewall;

namespace V2RayFullTunnel.Core;

public enum TunnelState { Disconnected, Connecting, Connected, Failed }

/// <summary>همه منطق تونل در یک جا؛ UI فقط صداش می‌کند.</summary>
public sealed class TunnelManager : IDisposable
{
    public event Action<string>? Log;
    public event Action<TunnelState, string>? StateChanged;

    private SingBoxRunner? _runner;
    private TunnelState _state = TunnelState.Disconnected;
    private string _exePath;
    private string _configPath;

    public TunnelState State => _state;
    public bool IsConnected => _state == TunnelState.Connected;

    public TunnelManager(string? exePath = null, string? configPath = null)
    {
        _exePath = exePath ?? Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "sing-box.json");
    }

    /// <summary>اگر اجرای قبلی نیمه‌کاره مانده بود، شبکه را خودمان برمی‌گردانیم.</summary>
    public void RecoverOnStartup()
    {
        try
        {
            if (KillSwitch.HasPendingState()
                && System.Diagnostics.Process.GetProcessesByName("sing-box").Length == 0)
            {
                KillSwitch.RecoverStaleState();
                Write("Recovered a stale network state left by a previous run.");
            }
        }
        catch (Exception ex) { Write("Recovery failed: " + ex.Message); }
    }

    private void Set(TunnelState s, string message)
    {
        _state = s;
        StateChanged?.Invoke(s, message);
    }

    private void Write(string m) => Log?.Invoke(m);

    public void Connect(ProxyLink link)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        Set(TunnelState.Connecting, "Connecting...");

        // ترتیب حیاتی: اول رزولو و گیت‌وی، چون بعد از روشن شدن TUN دیگر در دسترس نیست.
        // گیت‌وی را از مسیر واقعیِ رسیدن به سرور می‌گیریم (نه کمترین متریک)، چون ویندوز ممکن است
        // چند مسیر پیش‌فرض داشته باشد (VPN/Fortinet/OpenVPN/VMware) و کمترین متریک گمراه‌کننده باشد.
        string serverIp = KillSwitch.ResolveServerIp(link.Address);
        var gw = KillSwitch.GetGatewayTo(serverIp);
        Write($"Server: {serverIp} | Gateway: {gw.InterfaceAlias} ({gw.NextHop})");

        if (!KillSwitch.CleanupTunnelResidue())
            Write("NOTE: a stale TUN device is still present. Connect will retry cleanup.");

        string json = SingBoxConfigBuilder.Build(link, gw.InterfaceAlias);
        File.WriteAllText(_configPath, json);
        Write("Config generated.");

        // درایور Wintun گاهی بعد از قطع ناگهانی گیر می‌کند؛ با پاکسازی + تلاش دوباره خودش حل می‌شود
        bool tunReady = false;
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 3 && !tunReady; attempt++)
        {
            if (attempt > 1)
            {
                Write($"TUN did not start; cleaning up and retrying ({attempt}/3)...");
                _runner?.Dispose();
                _runner = null;
                KillSwitch.CleanupTunnelResidue();
                Thread.Sleep(1500);
            }
            try
            {
                _runner = new SingBoxRunner(_exePath, _configPath);
                _runner.OnLog += Write;
                _runner.OnExited += OnCoreExited;
                if (!_runner.CheckConfig(out string checkOut))
                {
                    _runner.Dispose();
                    _runner = null;
                    Set(TunnelState.Failed, "Invalid config.");
                    throw new InvalidOperationException("Invalid config: " + checkOut);
                }
                _runner.Start();
                tunReady = WaitForTunnel(TimeSpan.FromSeconds(20));
                if (!tunReady)
                    lastError = new InvalidOperationException("the TUN adapter did not come up");
            }
            catch (Exception ex) { lastError = ex; }
        }

        if (!tunReady)
        {
            _runner?.Dispose();
            _runner = null;
            Set(TunnelState.Failed, "TUN failed to start");
            throw new InvalidOperationException(
                $"Could not start the TUN adapter after 3 attempts: {lastError?.Message}\n" +
                "If this persists, reboot once and try again.");
        }

        // مهم: اگر KillSwitch شکست بخورد و پروسس زنده بماند، آداپتور TUN قفل می‌شود و
        // Connect بعدی با "create adapter: already exists" می‌میرد. پس همیشه تمیز می‌کنیم.
        try
        {
            KillSwitch.Apply(serverIp, gw, _exePath);
        }
        catch
        {
            _runner?.Dispose();
            _runner = null;
            KillSwitch.Restore(silent: true);
            KillSwitch.CleanupTunnelResidue();
            Set(TunnelState.Failed, "KillSwitch failed - tunnel stopped");
            throw;
        }
        Write("KillSwitch on: without the tunnel, direct internet is blocked.");
        Set(TunnelState.Connected, "Connected - all traffic goes through the tunnel");
    }

    private bool WaitForTunnel(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_runner is { IsRunning: false }) return false;
            try
            {
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                if (nics.Any(n => n.Name.StartsWith("v2-tun", StringComparison.OrdinalIgnoreCase)
                                  && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up))
                    return true;
            }
            catch { }
            Thread.Sleep(150);
        }
        return false;
    }

    private void OnCoreExited(int code)
    {
        // KillSwitch عمداً باقی می‌ماند: بدون تونل نباید ترافیک مستقیم عبور کند.
        Set(TunnelState.Failed, $"هسته خارج شد (کد {code}) — اینترنت مستقیم همچنان مسدود است");
    }

    public void Disconnect()
    {
        _runner?.Dispose();
        _runner = null;
        // kill سخت باعث می‌شود شیء آداپتور در درایور بماند؛ همین‌جا پاکش می‌کنیم
        if (!KillSwitch.CleanupTunnelResidue())
            Write("NOTE: TUN adapter residue left behind; a reboot may be needed before the next Connect.");
        KillSwitch.Restore();
        Write("Tunnel stopped and network restored.");
        Set(TunnelState.Disconnected, "Disconnected - network restored");
    }

    public void RestoreNetwork()
    {
        KillSwitch.Restore();
        _runner?.Dispose();
        _runner = null;
        Write("Network restored.");
        Set(TunnelState.Disconnected, "Ready to connect");
    }

    public async Task<string> CheckExitIpAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var body = await http.GetStringAsync("https://api.ipify.org", ct);
        return body.Trim();
    }

    public void Dispose()
    {
        // خروج واقعی از برنامه = برگرداندن شبکه
        try { KillSwitch.Restore(silent: true); } catch { }
        try { _runner?.Dispose(); } catch { }
        try { KillSwitch.CleanupTunnelResidue(); } catch { }
    }
}