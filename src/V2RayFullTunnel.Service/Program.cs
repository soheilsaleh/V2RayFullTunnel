using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using V2RayFullTunnel.Core;
using V2RayFullTunnel.Firewall;

var builder = Host.CreateApplicationBuilder(args);
// TODO(phase5): builder.Services.AddWindowsService(o => o.ServiceName = "V2RayFullTunnel");
// (نیاز به پکیج NuGet؛ فعلاً به‌صورت ورکر کنسولی اجرا می‌شود)
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();

sealed class Worker(ILogger<Worker> log) : BackgroundService
{
    private SingBoxRunner? _runner;
    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "V2RayFullTunnel");
    private static string ConfigPath => Path.Combine(DataDir, "sing-box.json");
    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "sing-box.exe");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("سرویس V2RayFullTunnel روشن شد.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(Path.Combine(DataDir, "connect.flag")))
                {
                    if (_runner is not { IsRunning: true } && File.Exists(ConfigPath) && File.Exists(ExePath))
                    {
                        // استخراج هاست/پورت از فایل کنار کانفیگ برای KillSwitch
                        string server = File.ReadAllText(Path.Combine(DataDir, "server.txt")).Trim();
                        var parts = server.Split(':');
                        _runner = new SingBoxRunner(ExePath, ConfigPath);
                        _runner.OnExited += code => log.LogWarning("sing-box با کد {c} خارج شد؛ اینترنت در حالت Block می‌ماند.", code);
                        if (_runner.CheckConfig(out _))
                        {
                            _runner.Start();
                            KillSwitch.Apply(parts[0], int.Parse(parts[1]), ExePath);
                            log.LogInformation("تونل وصل + KillSwitch اعمال شد.");
                        }
                    }
                }
                else
                {
                    _runner?.Stop(); _runner = null;
                }
            }
            catch (Exception ex) { log.LogError(ex, "خطای سرویس"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        // هنگام توقف سرویس: تونل را نگه دار در حالت Block (امن)، فقط پروسس را ببند
        _runner?.Stop();
    }
}
