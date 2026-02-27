using System.Net.Http.Headers;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace PrivateChatApp.Services;

public class GrpcChannelProvider : IDisposable
{
    private readonly AuthStateProvider _auth;
    private readonly object _lock = new();
    private GrpcChannel? _channel;

    public string ServerUrl { get; set; } = "https://localhost:5001";

    public GrpcChannelProvider(AuthStateProvider auth)
    {
        _auth = auth;
    }

    public GrpcChannel GetChannel()
    {
        if (_channel != null) return _channel;

        lock (_lock)
        {
            if (_channel != null) return _channel;

            var innerHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            };

            var authHandler = new AuthDelegatingHandler(_auth)
            {
                InnerHandler = innerHandler
            };

            _channel = GrpcChannel.ForAddress(ServerUrl, new GrpcChannelOptions
            {
                HttpHandler = authHandler
            });

            return _channel;
        }
    }

    public IClientFilter[] GetAuthFilters()
    {
        return [new AuthClientFilter(_auth)];
    }

    public void ResetChannel()
    {
        lock (_lock)
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

public class AuthDelegatingHandler : DelegatingHandler
{
    private readonly AuthStateProvider _auth;

    public AuthDelegatingHandler(AuthStateProvider auth)
    {
        _auth = auth;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _auth.Token;
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// MagicOnion client filter placeholder. Auth is handled at the HTTP layer
/// by AuthDelegatingHandler on the shared GrpcChannel, so this filter is
/// a pass-through. Kept for future per-call metadata injection if needed.
/// </summary>
public class AuthClientFilter : IClientFilter
{
    private readonly AuthStateProvider _auth;

    public AuthClientFilter(AuthStateProvider auth)
    {
        _auth = auth;
    }

    public async ValueTask<ResponseContext> SendAsync(RequestContext context, Func<RequestContext, ValueTask<ResponseContext>> next)
    {
        return await next(context);
    }
}
