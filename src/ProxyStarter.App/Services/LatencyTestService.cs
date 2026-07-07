using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace ProxyStarter.App.Services;

public sealed class LatencyTestService
{
    private static readonly string[] UrlTestCandidates =
    {
        "https://www.gstatic.com/generate_204",
        "https://cp.cloudflare.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt"
    };

    private static readonly string[] SpeedTestCandidates =
    {
        "https://speed.cloudflare.com/__down?bytes=5000000",
        "https://cachefly.cachefly.net/5mb.test",
        "https://ash-speed.hetzner.com/10MB.bin"
    };

    private readonly MihomoApiClient _apiClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly ProxyCatalogStore _proxyCatalogStore;
    private readonly ISerializer _serializer;

    public LatencyTestService(
        MihomoApiClient apiClient,
        AppSettingsStore settingsStore,
        ProxyCatalogStore proxyCatalogStore)
    {
        _apiClient = apiClient;
        _settingsStore = settingsStore;
        _proxyCatalogStore = proxyCatalogStore;
        _serializer = new SerializerBuilder().Build();
    }

    public async Task<int> TestTcpAsync(string host, int port, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            var watch = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            watch.Stop();
            return (int)watch.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<int> TestProxyDelayAsync(string proxyName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        var latency = await TestWithTemporaryMihomoAsync(proxyName, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (latency >= 0)
        {
            return latency;
        }

        return await _apiClient.TestDelayAsync(proxyName, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<double> TestProxyDownloadSpeedMbpsAsync(
        string proxyName,
        int timeoutMs = 12000,
        long maxBytes = 5_000_000,
        CancellationToken cancellationToken = default)
    {
        return await TestSpeedWithTemporaryMihomoAsync(proxyName, timeoutMs, maxBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> TestWithTemporaryMihomoAsync(string proxyName, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proxyName))
        {
            return -1;
        }

        var corePath = ResolveMihomoExecutable(_settingsStore.Settings);
        if (!File.Exists(corePath))
        {
            return -1;
        }

        var proxy = FindProxyDefinition(proxyName);
        if (proxy is null)
        {
            return -1;
        }

        var tempDirectory = Path.Combine(AppPaths.DataDirectory, "tmp");
        var configPath = Path.Combine(tempDirectory, $"latency-test-{Guid.NewGuid():N}.yaml");
        var mixedPort = GetFreeTcpPort();
        var apiPort = GetFreeTcpPort();
        var dnsPort = GetFreeTcpPort();
        var output = new StringBuilder();

        Process? process = null;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var config = BuildTemporaryMihomoConfig(proxy, mixedPort, apiPort, dnsPort);
            AtomicFile.WriteAllText(configPath, _serializer.Serialize(config), new UTF8Encoding(false));

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = corePath,
                    Arguments = $"-f \"{configPath}\" -d \"{AppPaths.DataDirectory}\"",
                    WorkingDirectory = AppPaths.DataDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => AppendOutput(output, e.Data);
            process.ErrorDataReceived += (_, e) => AppendOutput(output, e.Data);

            if (!process.Start())
            {
                return -1;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.HasExited)
            {
                LogTemporaryFailure(proxyName, "temporary core exited before URL test", output);
                return -1;
            }

            if (!await WaitForTcpPortAsync(mixedPort, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            {
                LogTemporaryFailure(proxyName, "temporary core did not open mixed port before URL test", output);
                return -1;
            }

            var latency = await TestUrlsThroughProxyAsync(mixedPort, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (latency < 0)
            {
                LogTemporaryFailure(proxyName, "all URL candidates failed", output);
            }

            return latency;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "LatencyTestService: Temporary Mihomo test");
            return -1;
        }
        finally
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            TryDelete(configPath);
        }
    }

    private async Task<double> TestSpeedWithTemporaryMihomoAsync(
        string proxyName,
        int timeoutMs,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proxyName))
        {
            return -1;
        }

        var corePath = ResolveMihomoExecutable(_settingsStore.Settings);
        if (!File.Exists(corePath))
        {
            return -1;
        }

        var proxy = FindProxyDefinition(proxyName);
        if (proxy is null)
        {
            return -1;
        }

        var tempDirectory = Path.Combine(AppPaths.DataDirectory, "tmp");
        var configPath = Path.Combine(tempDirectory, $"speed-test-{Guid.NewGuid():N}.yaml");
        var mixedPort = GetFreeTcpPort();
        var apiPort = GetFreeTcpPort();
        var dnsPort = GetFreeTcpPort();
        var output = new StringBuilder();

        Process? process = null;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var config = BuildTemporaryMihomoConfig(proxy, mixedPort, apiPort, dnsPort);
            AtomicFile.WriteAllText(configPath, _serializer.Serialize(config), new UTF8Encoding(false));

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = corePath,
                    Arguments = $"-f \"{configPath}\" -d \"{AppPaths.DataDirectory}\"",
                    WorkingDirectory = AppPaths.DataDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => AppendOutput(output, e.Data);
            process.ErrorDataReceived += (_, e) => AppendOutput(output, e.Data);

            if (!process.Start())
            {
                return -1;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.HasExited)
            {
                LogTemporaryFailure(proxyName, "temporary core exited before speed test", output);
                return -1;
            }

            if (!await WaitForTcpPortAsync(mixedPort, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            {
                LogTemporaryFailure(proxyName, "temporary core did not open mixed port before speed test", output);
                return -1;
            }

            var speed = await TestDownloadSpeedThroughProxyAsync(mixedPort, timeoutMs, maxBytes, cancellationToken)
                .ConfigureAwait(false);
            if (speed < 0)
            {
                LogTemporaryFailure(proxyName, "all speed test candidates failed", output);
            }

            return speed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "LatencyTestService: Temporary Mihomo speed test");
            return -1;
        }
        finally
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            TryDelete(configPath);
        }
    }

    private Dictionary<string, object>? FindProxyDefinition(string proxyName)
    {
        foreach (var proxy in _proxyCatalogStore.LoadProxyDefinitions())
        {
            if (!proxy.TryGetValue("name", out var nameValue))
            {
                continue;
            }

            var name = nameValue?.ToString() ?? string.Empty;
            if (string.Equals(name, proxyName, StringComparison.Ordinal))
            {
                return new Dictionary<string, object>(proxy, StringComparer.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    private static string ResolveMihomoExecutable(ProxyStarter.App.Models.AppSettings settings)
    {
        if (ProxyCoreAdapterFactory.NormalizeCoreType(settings.CoreType) == "mihomo")
        {
            var configured = ProxyCorePath.ResolveExecutable(settings.CorePath);
            if (File.Exists(configured))
            {
                return configured;
            }
        }

        return ProxyCorePath.ResolveExecutable(CoreCatalog.Get("mihomo").RelativeExecutablePath);
    }

    private static Dictionary<string, object> BuildTemporaryMihomoConfig(
        Dictionary<string, object> proxy,
        int mixedPort,
        int apiPort,
        int dnsPort)
    {
        var proxyName = proxy.TryGetValue("name", out var nameValue)
            ? nameValue?.ToString() ?? "proxy"
            : "proxy";

        return new Dictionary<string, object>
        {
            ["mixed-port"] = mixedPort,
            ["allow-lan"] = false,
            ["mode"] = "rule",
            ["log-level"] = "warning",
            ["ipv6"] = false,
            ["external-controller"] = $"127.0.0.1:{apiPort}",
            ["unified-delay"] = true,
            ["tcp-concurrent"] = true,
            ["dns"] = MihomoDnsConfig.Build(dnsPort, "127.0.0.1"),
            ["proxies"] = new List<Dictionary<string, object>> { proxy },
            ["proxy-groups"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["name"] = "PROXY",
                    ["type"] = "select",
                    ["proxies"] = new[] { proxyName }
                }
            },
            ["rules"] = new[] { "MATCH,PROXY" }
        };
    }

    private static async Task<int> TestUrlsThroughProxyAsync(int mixedPort, int timeoutMs, CancellationToken cancellationToken)
    {
        foreach (var url in UrlTestCandidates)
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}")
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs))
            };

            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                watch.Stop();
                if ((int)response.StatusCode < 400)
                {
                    return (int)watch.ElapsedMilliseconds;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return -1;
    }

    private static async Task<double> TestDownloadSpeedThroughProxyAsync(
        int mixedPort,
        int timeoutMs,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        foreach (var url in SpeedTestCandidates)
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}")
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(3000, timeoutMs))
            };

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);
                using var response = await client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                if ((int)response.StatusCode >= 400)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                long totalBytes = 0;
                var watch = Stopwatch.StartNew();

                while (totalBytes < maxBytes)
                {
                    var bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - totalBytes)),
                        timeoutCts.Token).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytes += bytesRead;
                }

                watch.Stop();
                if (totalBytes < 128 * 1024 || watch.Elapsed.TotalSeconds <= 0)
                {
                    continue;
                }

                return Math.Round(totalBytes * 8d / watch.Elapsed.TotalSeconds / 1_000_000d, 2);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return -1;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitForTcpPortAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(300);
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static void AppendOutput(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
            if (output.Length > 12000)
            {
                output.Remove(0, output.Length - 8000);
            }
        }
    }

    private static void LogTemporaryFailure(string proxyName, string reason, StringBuilder output)
    {
        string tail;
        lock (output)
        {
            tail = output.ToString();
        }

        CrashLogger.Log(
            new InvalidOperationException($"{reason}: {proxyName}\n{tail}"),
            "LatencyTestService: Temporary Mihomo test failed");
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
