using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class QosPolicyService
{
    private const string PolicyName = "ProxyStarter Throttle";
    private const string PolicyStore = "ActiveStore";

    public bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public async Task<QosApplyResult> ApplyAsync(AppSettings settings, bool silent = false, CancellationToken cancellationToken = default)
    {
        var effectiveLimitKbps = GetEffectiveLimit(settings.DownloadLimitKbps, settings.UploadLimitKbps);
        var needsPolicy = effectiveLimitKbps > 0;

        if (!IsAdministrator())
        {
            if (!needsPolicy)
            {
                return QosApplyResult.Ok("QoS unchanged.");
            }

            if (silent)
            {
                return QosApplyResult.Fail("Administrator privileges are required to apply QoS policies.");
            }

            return QosApplyResult.Fail("QoS requires administrator privileges. Please run Proxy Starter as administrator.");
        }

        var appPath = ResolveCorePath(settings.CorePath);
        var limitBits = effectiveLimitKbps <= 0 ? 0 : (long)effectiveLimitKbps * 1024 * 8;
        var script = BuildPolicyScript(appPath, limitBits);

        var result = await RunPowerShellAsync(script, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!needsPolicy)
        {
            return QosApplyResult.Ok("QoS policy removed.");
        }

        return QosApplyResult.Ok($"QoS applied at {effectiveLimitKbps} KB/s.");
    }

    private static int GetEffectiveLimit(int downloadKbps, int uploadKbps)
    {
        if (downloadKbps <= 0 && uploadKbps <= 0)
        {
            return 0;
        }

        if (downloadKbps <= 0)
        {
            return uploadKbps;
        }

        if (uploadKbps <= 0)
        {
            return downloadKbps;
        }

        return Math.Min(downloadKbps, uploadKbps);
    }

    private static string BuildPolicyScript(string appPath, long limitBitsPerSecond)
    {
        var safeAppPath = EscapePowerShellString(appPath);
        var sb = new StringBuilder();
        sb.Append("$policyName = '").Append(PolicyName).AppendLine("';");
        sb.Append("$store = '").Append(PolicyStore).AppendLine("';");
        sb.AppendLine("Get-NetQosPolicy -Name $policyName -PolicyStore $store -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue;");
        if (limitBitsPerSecond > 0)
        {
            sb.Append("$appPath = '").Append(safeAppPath).AppendLine("';");
            sb.Append("$rate = ").Append(limitBitsPerSecond).AppendLine(";");
            sb.AppendLine("New-NetQosPolicy -Name $policyName -PolicyStore $store -AppPathNameMatchCondition $appPath -ThrottleRateActionBitsPerSecond $rate;");
        }

        return sb.ToString();
    }

    private static async Task<QosApplyResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "Failed to apply QoS policy.";
                }
                return QosApplyResult.Fail(message.Trim());
            }

            return QosApplyResult.Ok(output.Trim());
        }
        catch (Exception ex)
        {
            return QosApplyResult.Fail($"Failed to execute QoS policy: {ex.Message}");
        }
    }

    private static string ResolveCorePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        if (System.IO.Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return System.IO.Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    private static string EscapePowerShellString(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }
}

public sealed record QosApplyResult(bool IsSuccess, string Message)
{
    public static QosApplyResult Ok(string message) => new(true, message);
    public static QosApplyResult Fail(string message) => new(false, message);
}
