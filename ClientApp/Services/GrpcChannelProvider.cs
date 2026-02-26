using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace ClientApp.Services;

public class GrpcChannelProvider : IDisposable
{
    private readonly AuthStateProvider _auth;
    private GrpcChannel? _channel;

    public string ServerUrl { get; set; } = "https://localhost:5001";

    public GrpcChannelProvider(AuthStateProvider auth)
    {
        _auth = auth;
    }

    public GrpcChannel GetChannel()
    {
        if (_channel != null) return _channel;

        var credentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            if (_auth.Token != null)
            {
                metadata.Add("Authorization", $"Bearer {_auth.Token}");
            }
            return Task.CompletedTask;
        });

        var handler = CreateHandler();

        _channel = GrpcChannel.ForAddress(ServerUrl, new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials)
        });

        return _channel;
    }

    private static HttpMessageHandler CreateHandler()
    {
#if ANDROID
        return new Xamarin.Android.Net.AndroidMessageHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                         | System.Security.Authentication.SslProtocols.Tls13
        };
#else
        return new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };
#endif
    }

    public IClientFilter[] GetAuthFilters()
    {
        return [new AuthClientFilter(_auth)];
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}

public class AuthClientFilter : IClientFilter
{
    private readonly AuthStateProvider _auth;

    public AuthClientFilter(AuthStateProvider auth)
    {
        _auth = auth;
    }

    public async ValueTask<ResponseContext> SendAsync(RequestContext context, Func<RequestContext, ValueTask<ResponseContext>> next)
    {
        var headers = context.CallOptions.Headers;
        if (_auth.Token != null && headers != null && !headers.Any(x => x.Key == "authorization"))
        {
            headers.Add("Authorization", $"Bearer {_auth.Token}");
        }

        return await next(context);
    }
}
