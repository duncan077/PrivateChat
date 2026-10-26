using ChatAppBlazor.Services;
using ChatAppBlazor.State;
using ChatAppBlazor.WebRtc;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using System.Reflection;
using System.Threading;

namespace ChatAppBlazor
{
    public static class MauiProgram
    {
        private static int _messagePackConfigured;

        private static void ConfigureMessagePackForAot()
        {
            if (Interlocked.Exchange(ref _messagePackConfigured, 1) == 1)
            {
                return;
            }

            StaticCompositeResolver.Instance.Register(
                global::MagicOnionClientGenerationMarker.Resolver,
                StandardResolver.Instance);

            MessagePackSerializer.DefaultOptions =
                MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance);
        }

        public static MauiApp CreateMauiApp()
        {
            global::MagicOnionClientGenerationMarker.Register();
            ConfigureMessagePackForAot();

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
            builder.Services.AddSingleton<MarkdownRenderService>();
            builder.Services.AddSingleton<ISystemNotificationService, SystemNotificationService>();

            // Screen-share annotation overlay. Windows draws remote participants'
            // strokes on the real desktop; other platforms fall back to in-app
            // rendering over the video.
#if WINDOWS
            builder.Services.AddSingleton<IScreenAnnotationOverlay, Platforms.Windows.WindowsScreenAnnotationOverlay>();
#else
            builder.Services.AddSingleton<IScreenAnnotationOverlay, NullScreenAnnotationOverlay>();
#endif

            // State stores (singletons — Blazor components subscribe to their events)
            builder.Services.AddSingleton<ServerAssetStore>();
            builder.Services.AddSingleton<ChatRoomStore>();
            builder.Services.AddSingleton<DirectMessageStore>();
            builder.Services.AddSingleton<CallStore>();
            builder.Services.AddSingleton<MembersPanelStore>();

            // WebRTC bridge (scoped — must match IJSRuntime's scoped lifetime).
            // In MAUI Blazor Hybrid the single BlazorWebView creates one scope for the
            // entire app lifetime, so scoped ≈ singleton here.  The call stays alive
            // across page navigation because we don't tear it down in DisposeAsync.
            builder.Services.AddScoped<WebRtcBridge>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif
            builder.Services.AddSingleton<UpdateService>();

            var app = builder.Build();

            var updateService = app.Services.GetRequiredService<UpdateService>();
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion != null)
                updateService.CurrentVersion = assemblyVersion.ToString(3);
            updateService.StartPolling();

            return app;
        }
    }
}
