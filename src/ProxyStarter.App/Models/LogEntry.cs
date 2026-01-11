using System;

namespace ProxyStarter.App.Models;

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = string.Empty;
}
