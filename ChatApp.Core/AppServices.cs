using ChatApp.Core.Abstractions;
using ChatApp.Core.Platform;
using ChatApp.Core.Services;
using ChatApp.Core.State;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp.Core;

/// <summary>
/// Composition root shared by every head (desktop, Android).
/// <para>
/// Replaces <c>MauiProgram.CreateMauiApp</c>. Lifetimes match the Blazor client's:
/// services and stores are singletons that outlive any view, because the UI
/// subscribes to their events and a call must survive navigation. The scoped
/// <c>WebRtcBridge</c> is gone — <see cref="Media.IMediaEngine"/> is a singleton
/// registered by each head.
/// </para>
/// </summary>
public static class AppServices
{
    private static int _messagePackConfigured;

    /// <summary>
    /// Registers the cross-platform services. <paramref name="configurePlatform"/> is
    /// where a head supplies its own <see cref="IGpuInfoProvider"/>,
    /// <see cref="ISecureStore"/>, notifications and media engine.
    /// </summary>
    public static IServiceCollection AddChatAppCore(
        this IServiceCollection services,
        Action<IServiceCollection>? configurePlatform = null)
    {
        ConfigureMessagePack();

        // Platform-replaceable defaults. TryAdd* semantics: a head that registers its
        // own implementation before calling this wins.
        services.AddSingleton<IAppPreferences>(_ => new JsonFilePreferences());
        services.AddSingleton<ISecureStore>(_ => new FileSecureStore());
        services.AddSingleton<IGpuInfoProvider, NullGpuInfoProvider>();

        services.AddSingleton<AuthStateProvider>();
        services.AddSingleton<GrpcChannelProvider>();
        services.AddSingleton<ChatClientService>();
        services.AddSingleton<ApiClientService>();

        services.AddSingleton<ChatRoomStore>();
        services.AddSingleton<DirectMessageStore>();
        services.AddSingleton<CallStore>();
        services.AddSingleton<MembersPanelStore>();

        configurePlatform?.Invoke(services);

        return services;
    }

    /// <summary>
    /// Points MessagePack at the source-generated resolver so serialization works
    /// without runtime code generation (required for trimmed and AOT Android builds).
    /// </summary>
    public static void ConfigureMessagePack()
    {
        if (Interlocked.Exchange(ref _messagePackConfigured, 1) == 1)
            return;

        MagicOnionClientGenerationMarker.Register();

        StaticCompositeResolver.Instance.Register(
            MagicOnionClientGenerationMarker.Resolver,
            StandardResolver.Instance);

        MessagePackSerializer.DefaultOptions =
            MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance);
    }
}
