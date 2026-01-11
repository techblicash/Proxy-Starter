using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoProcessService : IMihomoProcessService, IDisposable
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? RunningChanged;

    public Task StartAsync(MihomoLaunchOptions options, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (!File.Exists(options.CorePath))
        {
            LogReceived?.Invoke(this, $"Core not found: {options.CorePath}");
            RunningChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.CorePath,
            Arguments = BuildArguments(options),
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnOutputDataReceived;
        _process.Exited += OnProcessExited;

        if (!_process.Start())
        {
            LogReceived?.Invoke(this, "Failed to start core process.");
            RunningChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        RunningChanged?.Invoke(this, true);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _process is null)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await _process.WaitForExitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        RunningChanged?.Invoke(this, false);
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        LogReceived?.Invoke(this, e.Data);
    }

    private static string BuildArguments(MihomoLaunchOptions options)
    {
        var args = "";
        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            args += $"-f \"{options.ConfigPath}\" ";
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
        {
            args += options.ExtraArguments;
        }

        return args.Trim();
    }
}
