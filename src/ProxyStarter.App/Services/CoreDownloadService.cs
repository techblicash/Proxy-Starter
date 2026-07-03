using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyStarter.App.Services;

public sealed class CoreDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CoreDownloadService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CoreDownloadResult> DownloadAsync(CoreDefinition core, CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(core, cancellationToken).ConfigureAwait(false);
        var asset = SelectAsset(core, release.Assets);
        var targetPath = ProxyCorePath.ResolveExecutable(core.RelativeExecutablePath);
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? AppPaths.RuntimeDirectory;
        Directory.CreateDirectory(targetDirectory);

        var tempDirectory = Path.Combine(AppPaths.DataDirectory, "core-downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var archivePath = Path.Combine(tempDirectory, asset.Name);
            await DownloadFileAsync(asset.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);

            var extractedExecutable = ExtractExecutable(core, archivePath, tempDirectory);
            File.Copy(extractedExecutable, targetPath, overwrite: true);

            return new CoreDownloadResult(core, release.Version, asset.Name, targetPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "CoreDownloadService: cleanup");
            }
        }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(CoreDefinition core, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"https://api.github.com/repos/{core.Repository}/releases/latest", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var version = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? "latest"
            : "latest";

        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"No release assets found for {core.DisplayName}.");
        }

        var assets = new List<GitHubAsset>();
        foreach (var item in assetsElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = item.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
            {
                assets.Add(new GitHubAsset(name, url));
            }
        }

        return new GitHubRelease(version, assets);
    }

    private static GitHubAsset SelectAsset(CoreDefinition core, IReadOnlyList<GitHubAsset> assets)
    {
        var candidates = assets
            .Select(asset => new
            {
                Asset = asset,
                Score = ScoreAsset(core, asset.Name)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"No Windows x64 asset found for {core.DisplayName}.");
        }

        return candidates[0].Asset;
    }

    private static int ScoreAsset(CoreDefinition core, string assetName)
    {
        var name = assetName.ToLowerInvariant();
        if (core.CoreType == "mihomo")
        {
            if (!name.Contains("windows-amd64", StringComparison.Ordinal)
                || (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            var score = 10;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }
            if (name.Contains("compatible", StringComparison.Ordinal))
            {
                score += 20;
            }
            if (name.Contains("-v1", StringComparison.Ordinal))
            {
                score += 10;
            }
            if (name.Contains("go12", StringComparison.Ordinal))
            {
                score -= 1;
            }

            return score;
        }

        if (core.CoreType == "sing-box")
        {
            if (!name.Contains("windows-amd64", StringComparison.Ordinal)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return name.Contains("legacy", StringComparison.Ordinal) ? 5 : 10;
        }

        return 0;
    }

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractExecutable(CoreDefinition core, string archivePath, string tempDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDirectory = Path.Combine(tempDirectory, "extract");
            ZipFile.ExtractToDirectory(archivePath, extractDirectory);
            var executable = Directory.EnumerateFiles(extractDirectory, core.ExecutableName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(extractDirectory, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(file => Path.GetFileName(file).Contains(core.CoreType.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(extractDirectory, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

            if (executable is null)
            {
                throw new InvalidOperationException($"{core.ExecutableName} not found in downloaded archive.");
            }

            return executable;
        }

        if (archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var executablePath = Path.Combine(tempDirectory, core.ExecutableName);
            using var input = File.OpenRead(archivePath);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = File.Create(executablePath);
            gzip.CopyTo(output);
            return executablePath;
        }

        if (archivePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return archivePath;
        }

        throw new InvalidOperationException($"Unsupported archive type: {Path.GetFileName(archivePath)}");
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProxyStarter", "1.0"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record GitHubRelease(string Version, IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(string Name, string DownloadUrl);
}
