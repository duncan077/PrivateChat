using System.Threading;
using System.Runtime.Versioning;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif
#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace ChatAppBlazor.Services;

public sealed class SystemNotificationService : ISystemNotificationService
{
#if ANDROID
    private const string AndroidChannelId = "chat_messages";
    private const string AndroidChannelName = "Chat Messages";
    private const string AndroidChannelDescription = "Incoming chat message notifications";
    private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";
    private static int _nextNotificationId;
#endif
#if WINDOWS
    private static int _windowsNotificationsInitialized;
#endif

    public SystemNotificationService()
    {
#if WINDOWS
        EnsureWindowsNotificationsInitialized();
#endif
    }

    public Task<bool> ShowChatNotificationAsync(string title, string message, string? navigateUri = null)
    {
#if ANDROID
        return Task.FromResult(ShowAndroidNotification(title, message, navigateUri));
#elif WINDOWS
        return Task.FromResult(ShowWindowsNotification(title, message, navigateUri));
#else
        return Task.FromResult(false);
#endif
    }

#if ANDROID
    private static bool ShowAndroidNotification(string title, string message, string? navigateUri)
    {
        var appContext = Platform.AppContext;
        if (appContext is null)
        {
            return false;
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var permissionState = ContextCompat.CheckSelfPermission(
                appContext,
                PostNotificationsPermission);
            if (permissionState != Android.Content.PM.Permission.Granted)
            {
                return false;
            }
        }

        var manager = appContext.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null)
        {
            return false;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            EnsureAndroidChannel(manager);
        }

        var intent = new Intent(appContext, typeof(MainActivity));
        intent.SetAction($"{appContext.PackageName}.OPEN_CHAT_NOTIFICATION");
        intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop | ActivityFlags.NewTask);
        if (!string.IsNullOrWhiteSpace(navigateUri))
        {
            intent.PutExtra("navigateUri", navigateUri);
        }

        var requestCode = Interlocked.Increment(ref _nextNotificationId);
        var pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            pendingFlags |= PendingIntentFlags.Immutable;
        }

        var pendingIntent = PendingIntent.GetActivity(appContext, requestCode, intent, pendingFlags);
        var soundUri = RingtoneManager.GetDefaultUri(RingtoneType.Notification);

        var builder = new NotificationCompat.Builder(appContext, AndroidChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(string.IsNullOrWhiteSpace(title) ? "New message" : title);
        builder.SetContentText(message ?? string.Empty);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(message ?? string.Empty));
        builder.SetPriority((int)NotificationPriority.High);
        builder.SetCategory(NotificationCompat.CategoryMessage);
        builder.SetVisibility((int)NotificationVisibility.Private);
        builder.SetAutoCancel(true);
        builder.SetContentIntent(pendingIntent);
        builder.SetDefaults((int)NotificationDefaults.Vibrate);
        builder.SetSound(soundUri);

        manager.Notify(requestCode, builder.Build());
        return true;
    }

    [SupportedOSPlatform("android26.0")]
    private static void EnsureAndroidChannel(NotificationManager manager)
    {
        var existing = manager.GetNotificationChannel(AndroidChannelId);
        if (existing != null)
        {
            return;
        }

        var channel = new NotificationChannel(
            AndroidChannelId,
            AndroidChannelName,
            NotificationImportance.High)
        {
            Description = AndroidChannelDescription
        };

        var soundUri = RingtoneManager.GetDefaultUri(RingtoneType.Notification);
        var audioAttributesBuilder = new AudioAttributes.Builder();
        if (audioAttributesBuilder is null)
        {
            manager.CreateNotificationChannel(channel);
            return;
        }

        audioAttributesBuilder.SetUsage(AudioUsageKind.Notification);
        audioAttributesBuilder.SetContentType(AudioContentType.Sonification);
        var audioAttributes = audioAttributesBuilder.Build();

        channel.EnableVibration(true);
        channel.SetSound(soundUri, audioAttributes);
        manager.CreateNotificationChannel(channel);
    }
#endif

#if WINDOWS
    private static bool ShowWindowsNotification(string title, string message, string? navigateUri)
    {
        try
        {
            EnsureWindowsNotificationsInitialized();

            var builder = new AppNotificationBuilder()
                .AddText(string.IsNullOrWhiteSpace(title) ? "New message" : title)
                .AddText(message ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(navigateUri))
            {
                builder.AddArgument("route", navigateUri.Trim());
            }

            var appNotification = builder.BuildNotification();
            AppNotificationManager.Default.Show(appNotification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureWindowsNotificationsInitialized()
    {
        if (Interlocked.Exchange(ref _windowsNotificationsInitialized, 1) == 1)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnWindowsNotificationInvoked;
            AppNotificationManager.Default.Register();
        }
        catch
        {
            // Keep notification path best-effort; fallback snackbar remains available.
        }
    }

    private static void OnWindowsNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var route = TryGetWindowsNotificationArgument(args?.Arguments, "route");
            if (!string.IsNullOrWhiteSpace(route))
            {
                NotificationNavigationBridge.QueueNavigation(route);
            }
        }
        catch
        {
            // Ignore malformed payloads.
        }
    }

    private static string? TryGetWindowsNotificationArgument(IDictionary<string, string>? arguments, string key)
    {
        if (arguments == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (!arguments.TryGetValue(key, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
#endif
}
