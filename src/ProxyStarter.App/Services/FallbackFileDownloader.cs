using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Velopack.Sources;

namespace ProxyStarter.App.Services;

internal sealed class FallbackFileDownloader : IFileDownloader
{
    private readonly IFileDownloader _systemProxyDownloader = new HttpClientFileDownloader();
    private readonly IFileDownloader _directDownloader = new DirectFileDownloader();

    public async Task DownloadFile(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancelToken = default)
    {
        try
        {
            await _systemProxyDownloader.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryDirect(ex))
        {
            TryDeletePartialFile(targetFile);
            await _directDownloader.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        try
        {
            return await _systemProxyDownloader.DownloadBytes(url, headers, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryDirect(ex))
        {
            return await _directDownloader.DownloadBytes(url, headers, timeout).ConfigureAwait(false);
        }
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        try
        {
            return await _systemProxyDownloader.DownloadString(url, headers, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryDirect(ex))
        {
            return await _directDownloader.DownloadString(url, headers, timeout).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetryDirect(Exception ex)
    {
        return ex is HttpRequestException
               || ex.InnerException is HttpRequestException
               || ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeletePartialFile(string targetFile)
    {
        try
        {
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }
        }
        catch
        {
        }
    }

    private sealed class DirectFileDownloader : HttpClientFileDownloader
    {
        protected override HttpClientHandler CreateHttpClientHandler()
        {
            return new HttpClientHandler
            {
                UseProxy = false
            };
        }
    }
}
