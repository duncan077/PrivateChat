using Microsoft.Maui.Storage;

namespace ChatAppBlazor.Services;

public static class NotificationNavigationBridge
{
    private const string PendingRouteKey = "notifications.pendingRoute";

    public static event Action<string>? NavigateRequested;

    public static void QueueNavigation(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return;
        }

        var trimmed = route.Trim();
        Preferences.Default.Set(PendingRouteKey, trimmed);
        NavigateRequested?.Invoke(trimmed);
    }

    public static string? ConsumePendingNavigation()
    {
        var route = Preferences.Default.Get(PendingRouteKey, string.Empty);
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        Preferences.Default.Remove(PendingRouteKey);
        return route.Trim();
    }
}

