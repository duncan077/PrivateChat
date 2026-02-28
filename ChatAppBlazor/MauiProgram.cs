using ChatAppBlazor.Services;
using ChatAppBlazor.State;
using ChatAppBlazor.WebRtc;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace ChatAppBlazor
{
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

            // MudBlazor
            builder.Services.AddMudServices();

            // Core services (singletons — shared across the app lifetime)
            builder.Services.AddSingleton<AuthStateProvider>();
            builder.Services.AddSingleton<GrpcChannelProvider>();
            builder.Services.AddSingleton<ChatClientService>();
            builder.Services.AddSingleton<ApiClientService>();

            // State stores (singletons — Blazor components subscribe to their events)
            builder.Services.AddSingleton<ChatRoomStore>();
            builder.Services.AddSingleton<DirectMessageStore>();
            builder.Services.AddSingleton<CallStore>();

            // WebRTC bridge (scoped — must match IJSRuntime's scoped lifetime).
            // In MAUI Blazor Hybrid the single BlazorWebView creates one scope for the
            // entire app lifetime, so scoped ≈ singleton here.  The call stays alive
            // across page navigation because we don't tear it down in DisposeAsync.
            builder.Services.AddScoped<WebRtcBridge>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
