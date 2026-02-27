using Microsoft.Extensions.Logging;
using PrivateChatApp.Services;
using PrivateChatApp.State;
using PrivateChatApp.WebRtc;

namespace PrivateChatApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Core services (singletons — shared across the app lifetime)
        builder.Services.AddSingleton<AuthStateProvider>();
        builder.Services.AddSingleton<GrpcChannelProvider>();
        builder.Services.AddSingleton<ChatClientService>();
        builder.Services.AddSingleton<ApiClientService>();

        // State stores (singletons — Blazor components subscribe to their events)
        builder.Services.AddSingleton<ChatRoomStore>();
        builder.Services.AddSingleton<DirectMessageStore>();
        builder.Services.AddSingleton<CallStore>();

        // WebRTC bridge (scoped — one DotNetObjectReference per Blazor scope)
        builder.Services.AddScoped<WebRtcBridge>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        var app = builder.Build();

        // Catch unhandled exceptions globally for diagnostics
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[FATAL] Unobserved task: {e.Exception}");
        };

        return app;
    }
}
