using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using V2RayFullTunnel.Common;
using V2RayFullTunnel.Firewall;

namespace V2RayFullTunnel.Core;

public enum TunnelState { Disconnected, Connecting, Connected, Failed }

/// <summary>همه منطق تونل در یک جا؛ UI فقط صداش می‌کند.</summary>
public sealed class TunnelManager : IDisposable
{
    private const int MixedPort = 10808;
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);
    public event Action<string>? Log;
    public event Action<TunnelState, string>? StateChanged;

    private SingBoxRunner? _runner;
    private TunnelState _state = TunnelState.Disconnected;
    private string _exePath;
    private string _configPath;

    // ریکانکت خودکار: تا وقتی کاربر Disconnect نزده، قطعی هسته/شبکه به‌صورت خودکار ترمیم می‌شود.
    private ProxyLink? _lastLink;
    private string? _lastServerIp;
    private volatile bool _autoReconnect;
    private volatile bool _stopping;
    private int _reconnectActive;
    private int _healthActive;
    private int _healthFails;
    private DateTime _lastHealth = DateTime.MinValue;
    private DateTime _lastLockCheck = DateTime.MinValue;
    private long _lastCoreLogTicks;
    private int _coreSuppressed;
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

    /// <summary>فقط خطوط مهم هسته (خطا/هشدار) به UI می‌رود تا لاگ قابل خواندن بماند.</summary>
    private void WriteCore(string m)
    {
        string line = AnsiRegex.Replace(m, "").TrimEnd();
        if (line.Length == 0) return;

        bool fatal = line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("panic", StringComparison.OrdinalIgnoreCase);
        bool important = fatal
                         || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("WARN", StringComparison.OrdinalIgnoreCase);
        if (!important) return;

        // سیل خطاهای تکراری (مثل DNS down) لاگ را غیرقابل‌استفاده می‌کند؛
        // حداکثر یکی هر ۱.۵ ثانیه، با شمارش موارد سرکوب‌شده.
        long now = Environment.TickCount64;
        if (!fatal && now - Interlocked.Read(ref _lastCoreLogTicks) < 1500)
        {
            Interlocked.Increment(ref _coreSuppressed);
            return;
        }
        Interlocked.Exchange(ref _lastCoreLogTicks, now);
        int suppressed = Interlocked.Exchange(ref _coreSuppressed, 0);
        if (suppressed > 0) Write($"core: (+{suppressed} similar messages suppressed)");
        Write("core: " + line);
    }

    public void Connect(ProxyLink link)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        _lastLink = link;
        _autoReconnect = true;
        lock (_gate) ConnectInternal(link, "Connecting...");
    }

    private void ConnectInternal(ProxyLink link, string connectingMessage, string? preferredServerIp = null)
    {
        Set(TunnelState.Connecting, connectingMessage);

        // ترتیب حیاتی: اول رزولو و گیت‌وی، چون بعد از روشن شدن TUN دیگر در دسترس نیست.
        // گیت‌وی را از مسیر واقعیِ رسیدن به سرور می‌گیریم (نه کمترین متریک)، چون ویندوز ممکن است
        // چند مسیر پیش‌فرض داشته باشد (VPN/Fortinet/OpenVPN/VMware) و کمترین متریک گمراه‌کننده باشد.
        string serverIp;
        if (!string.IsNullOrEmpty(preferredServerIp))
        {
            // ریکانکت‌های اولیه نباید به DNS وابسته باشند (وقتی شبکه تازه برگشته، DNS هنوز ممکن است کار نکند).
            serverIp = preferredServerIp!;
        }
        else
        {
            serverIp = KillSwitch.ResolveServerIp(link.Address);
            _lastServerIp = serverIp;
        }
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
                KillSwitch.CleanupTunnelResidue(killStrayCore: true);
                Thread.Sleep(1500);
            }
            try
            {
                var runner = new SingBoxRunner(_exePath, _configPath);
                _runner = runner;
                runner.OnLog += WriteCore;
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
            KillSwitch.CleanupTunnelResidue(killStrayCore: true);
            KillSwitch.Restore(silent: true);
            Set(TunnelState.Failed, "KillSwitch failed - tunnel stopped");
            throw;
        }
        // تأیید واقعی: اگر با وجود بالا آمدن TUN هیچ ترافیکی از پروکسی عبور نکند،
        // اتصال را موفق اعلام نمی‌کنیم (قبلاً «سبز» می‌شد ولی اینترنت نداشت).
        if (!ProxyWorks(TimeSpan.FromSeconds(6)))
        {
            _runner?.Dispose();
            _runner = null;
            try { KillSwitch.CleanupTunnelResidue(killStrayCore: true); } catch { }
            try { KillSwitch.Restore(silent: true); } catch { }
            Set(TunnelState.Failed, "The tunnel started but no traffic passed through it");
            throw new InvalidOperationException(
                "the tunnel started but no traffic passed through it (server/DNS unreachable)");
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
            if (TunIsUp()) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    private static bool TunIsUp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.Name.StartsWith("v2-tun", StringComparison.OrdinalIgnoreCase)
                          && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        }
        catch { return false; }
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

            if (DateTime.UtcNow - _lastHealth >= TimeSpan.FromSeconds(15))
            {
                _lastHealth = DateTime.UtcNow;
                _ = HealthCheckAsync();
            }

            // ویندوز بعد از تغییر شبکه (قطع/وصل Wi-Fi) مسیرهای پیش‌فرض را برمی‌گرداند؛
            // قفل مسیری باید دوباره اعمال شود، وگرنه ترافیک مستقیم از کنار تونل عبور می‌کند.
            if (DateTime.UtcNow - _lastLockCheck < TimeSpan.FromSeconds(30)) return;
            _lastLockCheck = DateTime.UtcNow;
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_autoReconnect && _state == TunnelState.Connected && KillSwitch.RefreshLock())
                {
                    Write("Network change detected: physical default routes blocked again.");
                    _lastHealth = DateTime.MinValue; // health check فوری‌تر
                }
            }
            catch (Exception ex) { Write("Kill switch refresh failed: " + ex.Message); }
            finally { Monitor.Exit(_gate); }
        }
        catch { }
    }

    /// <summary>
    /// سلامت تونل را از طریق inbound محلی خود هسته می‌سنجد (نه مسیر سیستم)،
    /// تا نوسان DNS ویندوز باعث ریکانکت‌های بی‌مورد نشود. تا ۳ خطای متوالی صبر می‌کنیم.
    /// </summary>
    private async Task HealthCheckAsync()
    {
        if (Interlocked.Exchange(ref _healthActive, 1) == 1) return;
        try
        {
            if (!TunIsUp())
            {
                ScheduleReconnect("TUN interface is down");
                return;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{MixedPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await http.GetStringAsync("http://api.ipify.org", cts.Token);
            if (_healthFails > 0) Write("Health check: the tunnel is reachable again.");
            _healthFails = 0;
        }
        catch
        {
            _healthFails++;
            Write($"Health check failed ({_healthFails}/3).");
            if (_healthFails >= 3)
            {
                _healthFails = 0;
                ScheduleReconnect("no traffic through the tunnel");
            }
        }
        finally { Interlocked.Exchange(ref _healthActive, 0); }
    }

    /// <summary>یک درخواست واقعی از طریق inbound محلی هسته؛ true یعنی تونل واقعاً ترافیک عبور می‌دهد.</summary>
    private static bool ProxyWorks(TimeSpan timeout)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{MixedPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = timeout };
            using var cts = new CancellationTokenSource(timeout);
            http.GetStringAsync("http://api.ipify.org", cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
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

                        // مهم: قبل از تلاش مجدد، شبکه را برمی‌گردانیم.
                        // با KillSwitch فعال، رزولو کردن DNS سرور و مسیرهای جدید ممکن نیست
                        // (همان چیزی که باعث می‌شد ریکانکت هیچ‌وقت موفق نشود و فقط
                        // Disconnect/Connect دستی جواب بدهد).
                        try { KillSwitch.CleanupTunnelResidue(killStrayCore: true); } catch { }
                        try { KillSwitch.Restore(silent: true); } catch { }

                        // تلاش اول با IP کش‌شده (بدون DNS)؛ تلاش‌های بعدی رزولوشن تازه.
                        string? preferred = attempt == 1 ? _lastServerIp : null;
                        ConnectInternal(link, $"Reconnecting... (attempt {attempt}/{maxAttempts})", preferred);
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
            bool netOk;
            lock (_gate)
            {
                TryStopCore();
                try { KillSwitch.CleanupTunnelResidue(killStrayCore: true); } catch { }
                netOk = KillSwitch.Restore(silent: true);
            }
            _autoReconnect = false;
            if (netOk)
            {
                Set(TunnelState.Failed, "Reconnect failed - network restored, press Connect to retry");
            }
            else
            {
                Write("WARNING: could not restore a default route. Try Disconnect; if that fails, reboot.");
                Set(TunnelState.Failed, "Reconnect failed - network could not be restored (reboot may be needed)");
            }
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
        bool netOk;
        lock (_gate)
        {
            TryStopCore();
            // kill سخت باعث می‌شود شیء آداپتور در درایور بماند؛ همین‌جا پاکش می‌کنیم
            if (!KillSwitch.CleanupTunnelResidue(killStrayCore: true))
                Write("NOTE: TUN adapter residue left behind; a reboot may be needed before the next Connect.");
            netOk = KillSwitch.Restore();
        }
        if (netOk)
        {
            Write("Tunnel stopped and network restored.");
            Set(TunnelState.Disconnected, "Disconnected - network restored");
        }
        else
        {
            Write("WARNING: no default route after disconnect. Restarting the network adapter did not help either; a reboot may be needed.");
            Set(TunnelState.Disconnected, "Disconnected - network could not be restored (reboot may be needed)");
        }
    }

    public void RestoreNetwork()
    {
        _autoReconnect = false;
        TryStopCore();
        try { KillSwitch.CleanupTunnelResidue(killStrayCore: true); } catch { }
        bool ok = KillSwitch.Restore(silent: true);
        Write(ok ? "Network restored." : "WARNING: network could not be fully restored; a reboot may be needed.");
        Set(TunnelState.Disconnected, ok ? "Ready to connect" : "Network could not be restored (reboot may be needed)");
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
        try { _runner?.Dispose(); } catch { }
        try { KillSwitch.CleanupTunnelResidue(killStrayCore: true); } catch { }
        try { KillSwitch.Restore(silent: true); } catch { }
    }
}