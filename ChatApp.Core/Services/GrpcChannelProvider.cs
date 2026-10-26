using System.Net.Http.Headers;
using ChatApp.Core.Abstractions;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace ChatApp.Core.Services;

/// <summary>
/// Owns the single shared <see cref="GrpcChannel"/> used by MagicOnion and the
/// REST API client. Ported verbatim from the Blazor client — nothing here was
/// WebView-bound.
/// </summary>
public class GrpcChannelProvider : IDisposable
{
    private const string PrefServerUrl = "server.url";
    private const string DefaultServerUrl = "https://chat.duncan.com.ar";

    private readonly AuthStateProvider _auth;
    private readonly IAppPreferences? _preferences;
    private readonly object _lock = new();
    private GrpcChannel? _channel;

    public string ServerUrl { get; set; } = DefaultServerUrl;

    public GrpcChannelProvider(AuthStateProvider auth, IAppPreferences? preferences = null)
    {
        _auth = auth;
        _preferences = preferences;

        // The persisted URL wins over the compiled-in default, so the client is not
        // pinned to one deployment. Read once here: everything (gRPC, REST, Janus
        // signalling) resolves its base address through this property.
        var saved = preferences?.Get(PrefServerUrl, DefaultServerUrl);
        if (!string.IsNullOrWhiteSpace(saved))
            ServerUrl = saved;
    }

    /// <summary>
    /// Persists a new server URL and drops the current channel so the next call
    /// connects there. Callers should force re-authentication afterwards: a token
    /// from one server is meaningless to another.
    /// </summary>
    public bool TrySetServerUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var normalized = parsed.GetLeftPart(UriPartial.Authority);

        if (string.Equals(normalized, ServerUrl, StringComparison.OrdinalIgnoreCase))
            return true;

        ServerUrl = normalized;
        _preferences?.Set(PrefServerUrl, normalized);
        ResetChannel();
        return true;
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

    public IClientFilter[] GetAuthFilters() => [new AuthClientFilter(_auth)];

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
        GC.SuppressFinalize(this);
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
/// by <see cref="AuthDelegatingHandler"/> on the shared channel, so this filter is
/// a pass-through. Kept for future per-call metadata injection.
/// </summary>
public class AuthClientFilter : IClientFilter
{
    private readonly AuthStateProvider _auth;

    public AuthClientFilter(AuthStateProvider auth)
    {
        _auth = auth;
    }

    public async ValueTask<ResponseContext> SendAsync(
        RequestContext context, Func<RequestContext, ValueTask<ResponseContext>> next)
        => await next(context);
}
