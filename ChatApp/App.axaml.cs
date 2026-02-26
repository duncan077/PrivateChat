using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChatApp.Services;
using ChatApp.ViewModels;
using ChatApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<AuthStateProvider>();
        services.AddSingleton<GrpcChannelProvider>();
        services.AddSingleton<ApiClientService>();
        services.AddSingleton<ChatClientService>();
        services.AddSingleton<NavigationService>();

        // ViewModels
        services.AddTransient<LoginViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<MainViewModel>();

        // VoiceVideoViewModel must be singleton so the same WebView instance
        // is shared between ChatRoomViewModel and VoiceVideoView (if still used).
        services.AddSingleton<VoiceVideoViewModel>();

        // ChatRoomViewModel must be singleton so it holds onto its injected
        // VoiceVideoViewModel and the embedded WebView stays alive across navigation.
        services.AddSingleton<ChatRoomViewModel>();

        services.AddTransient<DirectMessagesViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
