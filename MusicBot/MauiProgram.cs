using Microsoft.Extensions.Logging;
using MusicBot.Services;

namespace MusicBot;

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
        builder.Services.AddSingleton<AuthSession>();
        builder.Services.AddSingleton<GrpcChannelProvider>();
        builder.Services.AddSingleton<ApiClientService>();
        builder.Services.AddSingleton<ChatHubClient>();
        builder.Services.AddSingleton<YoutubeAudioResolver>();
        builder.Services.AddSingleton<LocalAudioProxyService>();
        builder.Services.AddScoped<WebRtcBotBridge>();
        builder.Services.AddScoped<MusicBotRuntime>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
