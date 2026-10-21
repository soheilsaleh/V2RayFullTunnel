using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using SystemIcons = System.Drawing.SystemIcons;
// WinForms به‌صورت implicit اضافه می‌شود؛ این aliasها ابهام را رفع می‌کنند
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Key = System.Windows.Input.Key;
using V2RayFullTunnel.Common;
using V2RayFullTunnel.Core;

namespace V2RayFullTunnel.UI;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly TunnelManager _tunnel = new();
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayConnect;
    private Forms.ToolStripMenuItem? _trayDisconnect;
    private System.Drawing.Icon? _iconOff, _iconOn, _iconBusy, _iconError;
    private bool _syncingProfileList;
    private bool _updatingProfile;
    private bool _reallyExit;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // لاگ‌ها در صف جمع می‌شوند و حداکثر ~۸ بار در ثانیه روی UI خالی می‌شوند.
    // قبلاً هر خط با Dispatcher.Invoke همگام نوشته می‌شد و هنگام سیل لاگ رابط را قفل می‌کرد.
    private readonly Queue<string> _logQueue = new();
    private readonly object _logLock = new();
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private int _logLines;

    public MainWindow()
    {
        InitializeComponent();

        _tunnel.Log += Log;
        _tunnel.StateChanged += (state, message) =>
            Dispatcher.BeginInvoke(() => ApplyState(state, message));

        _logTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        InitTray();
        RefreshProfiles(StartupSelection());
        WarnIfPrerequisitesMissing();
        _tunnel.RecoverOnStartup();
        Closing += OnClosing;
        Closed += (_, _) => ShutdownEverything();
    }

    private string? StartupSelection()
    {
        if (!string.IsNullOrEmpty(_store.DefaultId)) return _store.DefaultId;
        return _store.Profiles.FirstOrDefault()?.Id;
    }

    private void WarnIfPrerequisitesMissing()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (!File.Exists(exe))
            Log($"WARNING: core not found ({exe}). sing-box.exe must sit next to the app.");
    }

    // ---------- System tray ----------

    private void InitTray()
    {
        _iconOff = CreateStatusIcon(System.Drawing.Color.FromArgb(0x6B, 0x76, 0x84), "\u2013");
        _iconBusy = CreateStatusIcon(System.Drawing.Color.FromArgb(0xE0, 0xA1, 0x2E), "\u2026");
        _iconOn = CreateStatusIcon(System.Drawing.Color.FromArgb(0x2E, 0x9E, 0x5B), "\u2713");
        _iconError = CreateStatusIcon(System.Drawing.Color.FromArgb(0xC0, 0x39, 0x2B), "!");

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayConnect = new Forms.ToolStripMenuItem("Connect", null, (_, _) => Dispatcher.Invoke(ConnectFromTray));
        _trayDisconnect = new Forms.ToolStripMenuItem("Disconnect", null, (_, _) => Dispatcher.Invoke(DisconnectFromTray))
        {
            Enabled = false
        };
        menu.Items.Add(_trayConnect);
        menu.Items.Add(_trayDisconnect);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));

        _tray = new Forms.NotifyIcon
        {
            Text = "V2Ray Full Tunnel",
            Icon = _iconOff ?? SystemIcons.Shield,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
    }

    /// <summary>آیکون وضعیت را در زمان اجرا می‌سازد (بدون فایل منبع اضافی).</summary>
    private static System.Drawing.Icon CreateStatusIcon(System.Drawing.Color fill, string glyph)
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(fill);
            g.FillEllipse(brush, 1f, 1f, 30f, 30f);
            using var font = new System.Drawing.Font("Segoe UI", 18f,
                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString(glyph, font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 0, 32, 32), format);
        }
        IntPtr h = bmp.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return;
        e.Cancel = true;      // فقط مخفی می‌شود، برنامه در تری ادامه می‌دهد
        Hide();
        _tray?.ShowBalloonTip(1500, "V2Ray Full Tunnel",
            "Still running in the tray. Right-click the icon for Connect / Disconnect / Exit.",
            Forms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Application.Current.Shutdown();
    }

    private void ShutdownEverything()
    {
        try { _tunnel.Dispose(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
        try { _iconOff?.Dispose(); _iconOn?.Dispose(); _iconBusy?.Dispose(); _iconError?.Dispose(); } catch { }
    }

    private async void ConnectFromTray()
    {
        if (_tunnel.State is TunnelState.Connected or TunnelState.Connecting) return;
        ResolveLinkFromProfiles();
        if (!IsLinkValid())
        {
            Balloon("No profile", "Open the window and add a valid profile first.", Forms.ToolTipIcon.Warning);
            return;
        }
        await ConnectAsync(showDialog: false);
        if (_tunnel.State == TunnelState.Connected)
            Balloon("Connected", "All traffic now goes through the tunnel.", Forms.ToolTipIcon.Info);
        else
            Balloon("Connection failed", "Open the window to see the log.", Forms.ToolTipIcon.Error);
    }

    private async void DisconnectFromTray()
    {
        if (_tunnel.State == TunnelState.Disconnected) return;
        await DisconnectAsync(showDialog: false);
        Balloon("Disconnected", "The network has been restored.", Forms.ToolTipIcon.Info);
    }

    private void ResolveLinkFromProfiles()
    {
        if (IsLinkValid() || string.IsNullOrEmpty(_store.DefaultId)) return;
        var def = _store.Profiles.FirstOrDefault(p => p.Id == _store.DefaultId);
        if (def != null) LinkBox.Text = def.Link;
    }

    private void Balloon(string title, string text, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(2000, title, text, icon);

    // ---------- Profiles ----------

    private SavedProfile? Selected => ProfileList.SelectedItem as SavedProfile;

    private void RefreshProfiles(string? selectId = null)
    {
        _store.Refresh();
        _syncingProfileList = true;
        ProfileList.ItemsSource = _store.Profiles.ToList();
        if (ProfileList.Items.Count == 0)
        {
            _syncingProfileList = false;
            SetEditingMode(false);
            LinkBox.Text = "";
            return;
        }
        int index = 0;
        if (!string.IsNullOrEmpty(selectId))
            for (int i = 0; i < ProfileList.Items.Count; i++)
                if (((SavedProfile)ProfileList.Items[i]).Id == selectId) { index = i; break; }
        _syncingProfileList = false;
        ProfileList.SelectedIndex = index;
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        string link = LinkBox.Text.Trim();
        if (link.Length == 0) { Warn(DescribeInvalid()); return; }
        try
        {
            _store.Add(NameBox.Text, link);
            string id = _store.Profiles[^1].Id;
            NameBox.Clear();
            SetEditingMode(false);
            RefreshProfiles(id);
            Log($"Saved: {ProfileStore.Describe(link)}");
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnUpdateProfile(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;
        string link = LinkBox.Text.Trim();
        if (link.Length == 0) { Warn(DescribeInvalid()); return; }
        try
        {
            _store.Update(p.Id, NameBox.Text, link);
            SetEditingMode(false);
            RefreshProfiles(p.Id);
            Log($"Updated: {ProfileStore.Describe(link)}");
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnSetDefault(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;
        _store.SetDefault(p.Id);
        RefreshProfiles(p.Id);
        Log(_store.DefaultId == p.Id ? $"Default profile: {p.Name}" : "Default cleared.");
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;
        if (MessageBox.Show($"Delete \"{p.Name}\"?", "Delete profile",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _store.Delete(p.Id);
        RefreshProfiles(StartupSelection());
        Log($"Deleted: {p.Name}");
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProfileList) return;
        var p = Selected;
        if (p == null) { SetEditingMode(false); return; }
        _updatingProfile = true;
        LinkBox.Text = p.Link;
        NameBox.Text = p.Name;
        _updatingProfile = false;
        SetEditingMode(true);
        Log($"Selected: {p.Display}");
    }

    private void SetEditingMode(bool editing)
    {
        EditBtn.IsEnabled = editing;
        DeleteBtn.IsEnabled = editing;
        DefaultBtn.IsEnabled = editing;
        DefaultBtn.Content = Selected is { IsDefault: true } ? "Default ✓" : "Default";
        SaveBtn.IsEnabled = !editing;
        LinkLabel.Text = editing
            ? "Editing selected profile - change link or name, then press Update"
            : "Profile link - paste a new VLESS / VMess / Trojan link here";
    }

    private void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingProfile) return;
        ConnectBtn.IsEnabled = IsLinkValid() && _tunnel.State != TunnelState.Connected;
        TestBtn.IsEnabled = IsLinkValid() && _tunnel.State != TunnelState.Connected;
        ConnectBtn.Opacity = IsLinkValid() ? 1.0 : 0.45;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) { }

    private void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (EditBtn.IsEnabled) OnUpdateProfile(sender, e);
        else OnSaveProfile(sender, e);
    }

    private static string DescribeInvalid() =>
        "The link box is empty or not a valid link.\n" +
        "Expected a link starting with vless://, vmess:// or trojan://";

    // ---------- Tunnel actions ----------

    private async void OnConnect(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private async Task ConnectAsync(bool showDialog = true)
    {
        SetBusy(true);
        try
        {
            var link = LinkParser.Parse(LinkBox.Text);
            DisconnectBtn.IsEnabled = true;
            await Task.Run(() => _tunnel.Connect(link));
        }
        catch (Exception ex)
        {
            ApplyState(TunnelState.Failed, "Connection failed");
            Warn(ex.Message, showDialog);
        }
        finally { SetBusy(false); }
    }

    private async Task DisconnectAsync(bool showDialog = true)
    {
        SetBusy(true);
        try { await Task.Run(() => _tunnel.Disconnect()); }
        catch (Exception ex) { Warn(ex.Message, showDialog); }
        finally { SetBusy(false); }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_tunnel.State == TunnelState.Connected)
        {
            Warn("Disconnect first, then test the profile.");
            return;
        }
        SetBusy(true);
        try
        {
            var link = LinkParser.Parse(LinkBox.Text);
            Log("Testing profile (real proxy request, no system changes) ...");
            string ip = await Task.Run(() => _tunnel.TestAsync(link));
            Log($"Test passed - the profile works. Exit IP: {ip}");
            ApplyState(TunnelState.Disconnected, "Ready to connect");
            MessageBox.Show(
                $"The profile works.\n\nExit IP through the tunnel: {ip}\n\nYou can press Connect now.",
                "Test successful", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Warn("Test failed: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private async void OnCheckIp(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            string ip = await _tunnel.CheckExitIpAsync();
            Log($"Exit IP: {ip}");
            MessageBox.Show($"Exit IP\n{ip}", "Check IP");
        }
        catch (Exception ex) { Warn("Check IP failed: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // ---------- UI state ----------

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectBtn.IsEnabled = !busy && _tunnel.State != TunnelState.Connected && IsLinkValid();
        DisconnectBtn.IsEnabled = !busy && _tunnel.State == TunnelState.Connected;
        TestBtn.IsEnabled = !busy && _tunnel.State != TunnelState.Connected && IsLinkValid();
        CheckIpBtn.IsEnabled = !busy;
    }

    private bool IsLinkValid()
    {
        try { LinkParser.Parse(LinkBox.Text); return true; } catch { return false; }
    }

    private void ApplyState(TunnelState state, string message)
    {
        StatusTitle.Text = message;
        var accent = new SolidColorBrush(state switch
        {
            TunnelState.Connected => Color.FromRgb(0x2E, 0x9E, 0x5B),
            TunnelState.Connecting => Color.FromRgb(0xE0, 0xA1, 0x2E),
            TunnelState.Failed => Color.FromRgb(0xC0, 0x39, 0x2B),
            _ => Color.FromRgb(0xC3, 0xC9, 0xD2)
        });
        StatusDot.Background = accent;
        StatusAccent.Background = accent;
        StatusSub.Text = state == TunnelState.Connected
            ? "KillSwitch is on: while the tunnel is down, no traffic can leave directly."
            : "Select a saved profile and press Connect.";
        ConnectBtn.IsEnabled = state != TunnelState.Connected && IsLinkValid();
        TestBtn.IsEnabled = state != TunnelState.Connected && IsLinkValid();
        DisconnectBtn.IsEnabled = state == TunnelState.Connected;
        if (_tray != null)
        {
            _tray.Icon = state switch
            {
                TunnelState.Connected => _iconOn,
                TunnelState.Connecting => _iconBusy,
                TunnelState.Failed => _iconError,
                _ => _iconOff
            } ?? _iconOff ?? SystemIcons.Shield;
            _tray.Text = state switch
            {
                TunnelState.Connected => "V2Ray Full Tunnel - CONNECTED",
                TunnelState.Connecting => "V2Ray Full Tunnel - connecting ...",
                TunnelState.Failed => "V2Ray Full Tunnel - failed",
                _ => "V2Ray Full Tunnel - disconnected"
            };
        }
        if (_trayConnect != null)
            _trayConnect.Enabled = state is not (TunnelState.Connected or TunnelState.Connecting);
        if (_trayDisconnect != null)
            _trayDisconnect.Enabled = state != TunnelState.Disconnected;
        FooterNote.Text = state == TunnelState.Connected
            ? "Protected: no traffic can leave the machine outside the tunnel."
            : "Closing the window keeps the app in the system tray. Disconnect restores the network.";
    }

    private void Log(string message)
    {
        lock (_logLock) _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void FlushLog()
    {
        string[] batch;
        lock (_logLock)
        {
            if (_logQueue.Count == 0) return;
            batch = _logQueue.ToArray();
            _logQueue.Clear();
        }
        foreach (var line in batch) LogBox.AppendText(line + "\n");
        _logLines += batch.Length;
        // برای جلوگیری از کند شدن تدریجی، متن لاگ را هر چند صد خط نصف می‌کنیم
        if (_logLines > 800)
        {
            string text = LogBox.Text;
            int cut = text.IndexOf('\n', text.Length / 2);
            if (cut > 0) LogBox.Text = text[(cut + 1)..];
            _logLines = LogBox.LineCount;
        }
        LogBox.ScrollToEnd();
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        lock (_logLock) _logQueue.Clear();
        _logLines = 0;
    }

    private void Warn(string message, bool showDialog = true)
    {
        Log("ERROR: " + message);
        if (showDialog)
            MessageBox.Show(message, "V2Ray Full Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}