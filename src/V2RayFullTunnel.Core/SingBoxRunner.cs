using System.Diagnostics;

namespace V2RayFullTunnel.Core;

/// <summary>اجرای sing-box.exe: check و run + نظارت بر خروج ناگهانی.</summary>
public sealed class SingBoxRunner : IDisposable
{
    private Process? _proc;
    public string ExePath { get; }
    public string ConfigPath { get; }
    public event Action<string>? OnLog;
    public event Action<int>? OnExited;
    public bool IsRunning => _proc is { HasExited: false };

    public SingBoxRunner(string exePath, string configPath)
    {
        ExePath = exePath;
        ConfigPath = configPath;
    }

    public bool CheckConfig(out string output)
    {
        var psi = new ProcessStartInfo(ExePath, $"check -c \"{ConfigPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode == 0;
    }

    public void Start()
    {
        Stop();
        var psi = new ProcessStartInfo(ExePath, $"run -c \"{ConfigPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke(e.Data); };
        _proc.Exited += (_, _) => OnExited?.Invoke(_proc?.ExitCode ?? -1);
        if (!_proc.Start()) throw new InvalidOperationException("sing-box اجرا نشد.");
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(5000);
            }
        }
        catch { }
        finally { _proc?.Dispose(); _proc = null; }
    }

    public void Dispose() => Stop();
}
