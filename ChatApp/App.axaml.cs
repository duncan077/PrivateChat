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

        services.AddSingleton<AuthStateProvider>();
        services.AddSingleton<GrpcChannelProvider>();
        services.AddSingleton<ApiClientService>();
        services.AddSingleton<ChatClientService>();
        services.AddSingleton<NavigationService>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChatRoomViewModel>();
        services.AddTransient<DirectMessagesViewModel>();
        services.AddSingleton<VoiceVideoViewModel>();

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
