using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    // ریکانکت خودکار: تا وقتی کاربر Disconnect نزده، قطعی هسته/شبکه به‌صورت خودکار ترمیم می‌شود.
    private ProxyLink? _lastLink;
    private volatile bool _autoReconnect;
    private volatile bool _stopping;
    private int _reconnectActive;
    private int _healthActive;
    private int _healthFails;
    private DateTime _lastHealth = DateTime.MinValue;
    private readonly object _gate = new();
    private System.Threading.Timer? _watchdog;

    public TunnelState State => _state;
    public bool IsConnected => _state == TunnelState.Connected;

    public TunnelManager(string? exePath = null, string? configPath = null)
    {
        _exePath = exePath ?? Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "sing-box.json");
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, 5000, 5000);
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
        _lastLink = link;
        _autoReconnect = true;
        lock (_gate) ConnectInternal(link, "Connecting...");
    }

    private void ConnectInternal(ProxyLink link, string connectingMessage)
    {
        Set(TunnelState.Connecting, connectingMessage);

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
                var runner = new SingBoxRunner(_exePath, _configPath);
                _runner = runner;
                runner.OnLog += Write;
                runner.OnExited += code => OnCoreExited(runner, code);
                if (!runner.CheckConfig(out string checkOut))
                {
                    runner.Dispose();
                    _runner = null;
                    Set(TunnelState.Failed, "Invalid config.");
                    throw new InvalidOperationException("Invalid config: " + checkOut);
                }
                runner.Start();
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
        _healthFails = 0;
        _lastHealth = DateTime.UtcNow;
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

    private void OnCoreExited(SingBoxRunner source, int code)
    {
        if (!ReferenceEquals(source, _runner)) return; // رویداد مربوط به پروسه‌ی قدیمی
        if (_stopping) return;
        if (_state == TunnelState.Connecting) return; // خودِ حلقه‌ی اتصال خطا را مدیریت می‌کند

        if (_autoReconnect && _lastLink != null)
        {
            Write($"Core exited (code {code}). Auto-reconnect started.");
            ScheduleReconnect("core exited");
        }
        else
        {
            // KillSwitch عمداً باقی می‌ماند: بدون تونل نباید ترافیک مستقیم عبور کند.
            Set(TunnelState.Failed, $"هسته خارج شد (کد {code}) — اینترنت مستقیم همچنان مسدود است");
        }
    }

    // ---------- Watchdog / auto-reconnect ----------

    private void Watchdog()
    {
        try
        {
            if (!_autoReconnect || _lastLink == null || _stopping) return;
            if (_state != TunnelState.Connected) return;

            if (_runner is not { IsRunning: true })
            {
                ScheduleReconnect("core is not running");
                return;
            }

            if (DateTime.UtcNow - _lastHealth < TimeSpan.FromSeconds(10)) return;
            _lastHealth = DateTime.UtcNow;
            _ = HealthCheckAsync();
        }
        catch { }
    }

    private async Task HealthCheckAsync()
    {
        if (Interlocked.Exchange(ref _healthActive, 1) == 1) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await http.GetStringAsync("https://www.gstatic.com/generate_204", cts.Token);
            if (_healthFails > 0) Write("Health check: the tunnel is reachable again.");
            _healthFails = 0;
        }
        catch
        {
            _healthFails++;
            Write($"Health check failed ({_healthFails}/2).");
            if (_healthFails >= 2)
            {
                _healthFails = 0;
                ScheduleReconnect("no traffic through the tunnel");
            }
        }
        finally { Interlocked.Exchange(ref _healthActive, 0); }
    }

    private void ScheduleReconnect(string reason)
    {
        if (!_autoReconnect || _lastLink == null) return;
        if (Interlocked.Exchange(ref _reconnectActive, 1) == 1) return;
        Write("Auto-reconnect triggered: " + reason);
        var link = _lastLink;
        Task.Run(() => ReconnectLoopAsync(link));
    }

    private async Task ReconnectLoopAsync(ProxyLink link)
    {
        const int maxAttempts = 6;
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (!_autoReconnect) return;
                try
                {
                    lock (_gate)
                    {
                        if (!_autoReconnect) return;
                        TryStopCore();
                        ConnectInternal(link, $"Reconnecting... (attempt {attempt}/{maxAttempts})");
                    }
                    Write("Auto-reconnect succeeded.");
                    return;
                }
                catch (Exception ex)
                {
                    Write($"Reconnect attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                }

                if (!_autoReconnect) return;
                int delay = attempt switch { 1 => 2, 2 => 4, 3 => 8, 4 => 12, _ => 15 };
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            // بعد از تلاش‌های ناموفق، شبکه را برمی‌گردانیم تا کاربر بدون اینترنت گیر نکند.
            Write("Auto-reconnect gave up. Restoring the network.");
            lock (_gate)
            {
                TryStopCore();
                try { KillSwitch.Restore(silent: true); } catch { }
                try { KillSwitch.CleanupTunnelResidue(); } catch { }
            }
            _autoReconnect = false;
            Set(TunnelState.Failed, "Reconnect failed - network restored, press Connect to retry");
        }
        catch (Exception ex) { Write("Auto-reconnect error: " + ex.Message); }
        finally { Interlocked.Exchange(ref _reconnectActive, 0); }
    }

    private void TryStopCore()
    {
        _stopping = true;
        try { _runner?.Dispose(); } catch { }
        finally { _stopping = false; }
        _runner = null;
    }

    public void Disconnect()
    {
        _autoReconnect = false;
        lock (_gate)
        {
            TryStopCore();
            // kill سخت باعث می‌شود شیء آداپتور در درایور بماند؛ همین‌جا پاکش می‌کنیم
            if (!KillSwitch.CleanupTunnelResidue())
                Write("NOTE: TUN adapter residue left behind; a reboot may be needed before the next Connect.");
            KillSwitch.Restore();
        }
        Write("Tunnel stopped and network restored.");
        Set(TunnelState.Disconnected, "Disconnected - network restored");
    }

    public void RestoreNetwork()
    {
        _autoReconnect = false;
        KillSwitch.Restore();
        TryStopCore();
        Write("Network restored.");
        Set(TunnelState.Disconnected, "Ready to connect");
    }

    public async Task<string> CheckExitIpAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var body = await http.GetStringAsync("https://api.ipify.org", ct);
        return body.Trim();
    }

    /// <summary>
    /// تست پیکربندی پیش از اتصال: یک نمونه‌ی موقت sing-box فقط با inbound محلی اجرا می‌شود
    /// (بدون TUN و بدون لمس مسیرها)، سپس از طریق پروکسی یک درخواست واقعی زده و IP خروجی برگردانده می‌شود.
    /// اگر این تست موفق شود، پیکربندی و اعتبارنامه‌ها قطعاً کار می‌کنند.
    /// </summary>
    public async Task<string> TestAsync(ProxyLink link, CancellationToken ct = default)
    {
        if (IsConnected) throw new InvalidOperationException("Disconnect first, then test.");
        if (KillSwitch.HasPendingState())
            throw new InvalidOperationException(
                "A tunnel is still active (kill switch engaged). Disconnect it first, then test.");

        string serverIp = KillSwitch.ResolveServerIp(link.Address);
        var gw = KillSwitch.GetGatewayTo(serverIp);

        int port = 18100 + Random.Shared.Next(0, 400);
        string probePath = Path.Combine(Path.GetTempPath(), $"v2ft-probe-{Guid.NewGuid():N}.json");
        File.WriteAllText(probePath, SingBoxConfigBuilder.BuildProbe(link, gw.InterfaceAlias, port));
        Write($"Test: starting temporary core on 127.0.0.1:{port} ...");

        var runner = new SingBoxRunner(_exePath, probePath);
        runner.OnLog += Write;
        try
        {
            if (!runner.CheckConfig(out string chk))
                throw new InvalidOperationException("Invalid config: " + chk);

            runner.Start();

            var sw = Stopwatch.StartNew();
            bool listening = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (!runner.IsRunning) throw new InvalidOperationException("The core exited during the test.");
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync("127.0.0.1", port, ct);
                    listening = true;
                    break;
                }
                catch { await Task.Delay(150, ct); }
            }
            if (!listening) throw new InvalidOperationException("The test listener did not come up.");

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            string body = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
            Write($"Test passed - tunnel works, exit IP {body}");
            return body;
        }
        finally
        {
            runner.Dispose();
            try { File.Delete(probePath); } catch { }
        }
    }

    public void Dispose()
    {
        // خروج واقعی از برنامه = برگرداندن شبکه
        _autoReconnect = false;
        try { _watchdog?.Dispose(); } catch { }
        try { KillSwitch.Restore(silent: true); } catch { }
        try { _runner?.Dispose(); } catch { }
        try { KillSwitch.CleanupTunnelResidue(); } catch { }
    }
}