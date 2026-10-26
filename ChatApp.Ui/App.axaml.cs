using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChatApp.Core;
using ChatApp.Core.Media;
using ChatApp.Ui.ViewModels;
using ChatApp.Ui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatApp.Ui;

public partial class App : Application
{
    /// <summary>
    /// Hook for a head to register platform services (GPU info, secure store,
    /// notifications, media engine) before the container is built. Assign this from
    /// the head's entry point, before Avalonia starts.
    /// </summary>
    public static Action<IServiceCollection>? ConfigurePlatformServices { get; set; }

    /// <summary>Resolved container. Available once the framework has initialized.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddChatAppCore(ConfigurePlatformServices);
        Services = services.BuildServiceProvider();

        // Controls are built by the XAML loader and cannot take constructor
        // dependencies, so the scaler is handed to the class once, here.
        Controls.VideoView.ConverterFactory = Services.GetService<IVideoFrameConverterFactory>();

        LogStartupDiagnostics();

        var mainViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(Services);

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
                SetUpTrayIcon(desktop);
                break;

            case IActivityApplicationLifetime activityLifetime:
                activityLifetime.MainViewFactory =
                    () => new MainView { DataContext = mainViewModel };
                break;

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = mainViewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tray presence for desktop: click to restore the window, menu to open or quit.
    /// Built in code because it only applies to the desktop lifetime.
    /// </summary>
    private void SetUpTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            using var iconStream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://ChatApp.Ui/Assets/avalonia-logo.ico"));

            var open = new Avalonia.Controls.NativeMenuItem("Open");
            open.Click += (_, _) => WindowAlert.ActivateMainWindow();

            var quit = new Avalonia.Controls.NativeMenuItem("Quit");
            quit.Click += (_, _) => desktop.Shutdown();

            var tray = new Avalonia.Controls.TrayIcon
            {
                Icon = new Avalonia.Controls.WindowIcon(iconStream),
                ToolTipText = "ChatFlow",
                Menu = [open, quit]
            };

            tray.Clicked += (_, _) => WindowAlert.ActivateMainWindow();

            Avalonia.Controls.TrayIcon.SetIcons(this, [tray]);
        }
        catch
        {
            // No tray support on this desktop environment; the app works without it.
        }
    }

    /// <summary>
    /// Resolves the media stack eagerly and records the outcome.
    /// <para>
    /// Without this, a missing registration or a native library that failed to load only
    /// surfaces when the user clicks "Join call" — as an opaque failure, minutes after
    /// launch. Failing loudly at startup, in the log, is much easier to diagnose.
    /// </para>
    /// </summary>
    private static void LogStartupDiagnostics()
    {
        var logger = Services.GetService<ILoggerFactory>()?.CreateLogger("Startup");
        if (logger is null)
            return;

        logger.LogInformation("ChatFlow starting.");

        try
        {
            var host = Services.GetService<MediaEngineHost>();

            if (host is null)
                logger.LogWarning("No media engine registered — calls will be unavailable on this platform.");
            else
                logger.LogInformation("Media engine ready ({Engine}).", host.Engine.GetType().Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The media stack failed to initialize; calls will not work.");
        }
    }
}
