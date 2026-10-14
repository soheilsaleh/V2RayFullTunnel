using System.IO;
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
    private bool _syncingProfileList;
    private bool _updatingProfile;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        _tunnel.Log += Log;
        _tunnel.StateChanged += (state, message) =>
            Dispatcher.Invoke(() => ApplyState(state, message));

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
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Connect", null, (_, _) => Dispatcher.Invoke(() => ConnectFromTray()));
        menu.Items.Add("Disconnect", null, (_, _) => Dispatcher.Invoke(() => DisconnectFromTray()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));

        _tray = new Forms.NotifyIcon
        {
            Text = "V2Ray Full Tunnel",
            Icon = SystemIcons.Shield,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
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
    }

    private async void ConnectFromTray()
    {
        ShowWindow();
        string link = LinkBox.Text.Trim();
        if (!IsLinkValid() && !string.IsNullOrEmpty(_store.DefaultId))
        {
            var def = _store.Profiles.FirstOrDefault(p => p.Id == _store.DefaultId);
            if (def != null) { LinkBox.Text = def.Link; link = def.Link; }
        }
        if (!IsLinkValid()) { Warn(DescribeInvalid()); return; }
        await ConnectAsync();
    }

    private async void DisconnectFromTray()
    {
        ShowWindow();
        await DisconnectAsync();
    }

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

    private async Task ConnectAsync()
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
            Warn(ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        try { await Task.Run(() => _tunnel.Disconnect()); }
        catch (Exception ex) { Warn(ex.Message); }
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
        CheckIpBtn.IsEnabled = !busy;
    }

    private bool IsLinkValid()
    {
        try { LinkParser.Parse(LinkBox.Text); return true; } catch { return false; }
    }

    private void ApplyState(TunnelState state, string message)
    {
        StatusTitle.Text = message;
        StatusDot.Background = new SolidColorBrush(state switch
        {
            TunnelState.Connected => Color.FromRgb(0x2E, 0x9E, 0x5B),
            TunnelState.Connecting => Color.FromRgb(0xE0, 0xA1, 0x2E),
            TunnelState.Failed => Color.FromRgb(0xC0, 0x39, 0x2B),
            _ => Color.FromRgb(0xC3, 0xC9, 0xD2)
        });
        StatusSub.Text = state == TunnelState.Connected
            ? "KillSwitch is on: while the tunnel is down, no traffic can leave directly."
            : "Select a saved profile and press Connect.";
        ConnectBtn.IsEnabled = state != TunnelState.Connected && IsLinkValid();
        DisconnectBtn.IsEnabled = state == TunnelState.Connected;
        if (_tray != null)
            _tray.Text = state == TunnelState.Connected
                ? "V2Ray Full Tunnel - CONNECTED"
                : "V2Ray Full Tunnel";
        FooterNote.Text = state == TunnelState.Connected
            ? "Protected: no traffic can leave the machine outside the tunnel."
            : "Closing the window keeps the app in the system tray. Disconnect restores the network.";
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void Warn(string message)
    {
        Log("ERROR: " + message);
        MessageBox.Show(message, "V2Ray Full Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}