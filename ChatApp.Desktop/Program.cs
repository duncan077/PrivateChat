using System;
using Avalonia;
using ChatApp.Media.Codec;
using ChatApp.Ui;

namespace ChatApp.Desktop;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Resolve FFmpeg before any media type is touched. Transport is pure C# now
        // (SIPSorcery), so FFmpeg is the only native dependency left.
        FFmpegLoader.Initialize();

        App.ConfigurePlatformServices = WindowsPlatformServices.Register;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
