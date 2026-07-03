namespace ProxyStarter.App.Helpers;

/// <summary>
/// Allows a ViewModel to pause timers/network polls when its page is not visible.
/// This helps reduce UI-thread churn and memory/CPU usage while still keeping the page cached.
/// </summary>
public interface IPageLifecycleAware
{
    void OnPageActivated();
    void OnPageDeactivated();
}

