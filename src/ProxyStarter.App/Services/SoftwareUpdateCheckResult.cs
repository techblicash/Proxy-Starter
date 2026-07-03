namespace ProxyStarter.App.Services;

public enum SoftwareUpdateStatus
{
    UpdateReady,
    NoUpdate,
    PendingRestart,
    FeedNotConfigured,
    NotInstalled
}

public sealed record SoftwareUpdateCheckResult(
    SoftwareUpdateStatus Status,
    string CurrentVersion,
    string? AvailableVersion,
    string Message);
