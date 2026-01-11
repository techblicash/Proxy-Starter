using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public interface IMihomoProcessService
{
    bool IsRunning { get; }

    event EventHandler<string>? LogReceived;
    event EventHandler<bool>? RunningChanged;

    Task StartAsync(MihomoLaunchOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
