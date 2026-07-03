using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class MihomoProcessService : IMihomoProcessService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private Process? _process;
    private bool _isStopping;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return IsProcessRunning(_process);
            }
        }
    }

    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? RunningChanged;

    public async Task StartAsync(MihomoLaunchOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            lock (_stateLock)
            {
                if (IsProcessRunning(_process))
                {
                    return;
                }

                DisposeProcess(_process);
                _process = null;
            }

            if (!File.Exists(options.CorePath))
            {
                LogReceived?.Invoke(this, $"Core not found: {options.CorePath}");
                RunningChanged?.Invoke(this, false);
                return;
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

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnOutputDataReceived;
            process.Exited += OnProcessExited;

            try
            {
                if (!process.Start())
                {
                    LogReceived?.Invoke(this, "Failed to start core process.");
                    RunningChanged?.Invoke(this, false);
                    DisposeProcess(process);
                    return;
                }

                lock (_stateLock)
                {
                    _process = process;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!IsProcessRunning(process))
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_process, process))
                        {
                            _process = null;
                        }
                    }

                    DisposeProcess(process);
                    RunningChanged?.Invoke(this, false);
                    return;
                }

                RunningChanged?.Invoke(this, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }

                CrashLogger.Log(ex, "MihomoProcessService: Start");
                LogReceived?.Invoke(this, $"Failed to start core process: {ex.Message}");
                RunningChanged?.Invoke(this, false);
                DisposeProcess(process);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_stateLock)
            {
                process = _process;
                if (!IsProcessRunning(process))
                {
                    DisposeProcess(process);
                    _process = null;
                    return;
                }

                _isStopping = true;
            }

            try
            {
                process!.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "MihomoProcessService: Stop kill");
            }

            try
            {
                await process!.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var notifyStopped = false;
                lock (_stateLock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        notifyStopped = true;
                    }

                    _isStopping = false;
                }

                DisposeProcess(process);

                if (notifyStopped)
                {
                    RunningChanged?.Invoke(this, false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;

        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _process = null;
            _isStopping = false;
        }

        try
        {
            if (IsProcessRunning(process))
            {
                process!.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "MihomoProcessService: Dispose kill");
        }

        DisposeProcess(process);
        _gate.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        var shouldNotify = false;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_process, process) || _isStopping)
            {
                return;
            }

            _process = null;
            shouldNotify = true;
        }

        try
        {
            LogReceived?.Invoke(this, $"Core process exited with code {process.ExitCode}.");
        }
        catch
        {
        }

        DisposeProcess(process);

        if (shouldNotify)
        {
            RunningChanged?.Invoke(this, false);
        }
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MihomoProcessService));
        }
    }

    private void DisposeProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnOutputDataReceived;
            process.Exited -= OnProcessExited;
        }
        catch
        {
        }

        try
        {
            process.Dispose();
        }
        catch
        {
        }
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
        if (!string.IsNullOrWhiteSpace(options.Arguments))
        {
            return options.Arguments.Trim();
        }

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
