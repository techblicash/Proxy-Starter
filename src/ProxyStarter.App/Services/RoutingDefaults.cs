namespace ProxyStarter.App.Services;

public static class RoutingDefaults
{
    public const string FlatModeSelectionGroup = "PROXY";

    public static string ResolveSelectionGroup(string configuredSelectionGroup, bool useSubscriptionPolicyGroups)
    {
        if (!useSubscriptionPolicyGroups)
        {
            return FlatModeSelectionGroup;
        }

        return string.IsNullOrWhiteSpace(configuredSelectionGroup)
            ? FlatModeSelectionGroup
            : configuredSelectionGroup.Trim();
    }
}
