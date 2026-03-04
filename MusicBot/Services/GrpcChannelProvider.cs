using System.Net.Http.Headers;
using Grpc.Net.Client;

namespace MusicBot.Services;

public sealed class GrpcChannelProvider : IDisposable
{
    private readonly AuthSession _auth;
    private readonly object _sync = new();
    private GrpcChannel? _channel;

    public string ServerUrl { get; set; } = "https://chat.duncan.com.ar";

    public GrpcChannelProvider(AuthSession auth)
    {
        _auth = auth;
    }

    public GrpcChannel GetChannel()
    {
        if (_channel != null) return _channel;

        lock (_sync)
        {
            if (_channel != null) return _channel;

            var sockets = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            };

            var authHandler = new BotAuthDelegatingHandler(_auth)
            {
                InnerHandler = sockets
            };

            _channel = GrpcChannel.ForAddress(ServerUrl, new GrpcChannelOptions
            {
                HttpHandler = authHandler
            });
        }

        return _channel;
    }

    public void ResetChannel()
    {
        lock (_sync)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}

internal sealed class BotAuthDelegatingHandler : DelegatingHandler
{
    private readonly AuthSession _auth;

    public BotAuthDelegatingHandler(AuthSession auth)
    {
        _auth = auth;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_auth.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

