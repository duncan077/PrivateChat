using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace MusicBot.Services;

public sealed class LocalAudioProxyService : IAsyncDisposable
{
#if WINDOWS
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };
    private static readonly HashSet<string> PassThroughHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Ranges",
        "Content-Range",
        "Cache-Control",
        "ETag",
        "Last-Modified",
        "Expires",
        "Date"
    };

    private readonly object _sync = new();
    private readonly HttpClient _upstream = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

    private HttpListener? _listener;
    private CancellationTokenSource? _listenCts;
    private Task? _listenLoop;
    private string? _baseUrl;
#endif

    public IReadOnlyList<string> MapToProxyUrls(IEnumerable<string> urls)
    {
        var cleaned = urls?
            .Where(static u => !string.IsNullOrWhiteSpace(u))
            .Select(static u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        if (cleaned.Length == 0)
        {
            return cleaned;
        }

#if WINDOWS
        EnsureStarted();
        if (!string.IsNullOrWhiteSpace(_baseUrl))
        {
            return cleaned
                .Select(url => $"{_baseUrl}audio-proxy?url={Uri.EscapeDataString(url)}")
                .ToArray();
        }
#endif

        return cleaned;
    }

#if WINDOWS
    private void EnsureStarted()
    {
        lock (_sync)
        {
            if (_listener != null)
            {
                return;
            }

            var port = ReserveEphemeralPort();
            if (port <= 0)
            {
                return;
            }

            var listener = new HttpListener();
            var baseUrl = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(baseUrl);
            listener.Start();

            _listener = listener;
            _baseUrl = baseUrl;
            _listenCts = new CancellationTokenSource();
            _listenLoop = Task.Run(() => ListenLoopAsync(listener, _listenCts.Token));
        }
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (!listener.IsListening) break;
            }

            if (context != null)
            {
                _ = Task.Run(() => HandleContextAsync(context, ct), ct);
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            AddCorsHeaders(context.Response);

            var method = context.Request.HttpMethod?.ToUpperInvariant() ?? "GET";
            if (method == "OPTIONS")
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            if (method != "GET" && method != "HEAD")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var rawUrl = context.Request.QueryString["url"];
            if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var upstreamUri))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteTextAsync(context.Response, "Missing or invalid url query parameter.", ct);
                return;
            }

            if (!string.Equals(upstreamUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(upstreamUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteTextAsync(context.Response, "Only http/https URLs are supported.", ct);
                return;
            }

            using var upstreamRequest = new HttpRequestMessage(
                method == "HEAD" ? HttpMethod.Head : HttpMethod.Get,
                upstreamUri);
            if (!string.IsNullOrWhiteSpace(context.Request.Headers["Range"]))
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Range", context.Request.Headers["Range"]);
            }

            if (!string.IsNullOrWhiteSpace(context.Request.Headers["Accept"]))
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Accept", context.Request.Headers["Accept"]);
            }

            upstreamRequest.Headers.Referrer = new Uri("https://www.youtube.com/");
            upstreamRequest.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await _upstream.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await WriteTextAsync(context.Response, $"Upstream request failed: {ex.Message}", ct);
                return;
            }

            using (upstreamResponse)
            {
                context.Response.StatusCode = (int)upstreamResponse.StatusCode;
                context.Response.KeepAlive = false;
                ApplyUpstreamHeaders(upstreamResponse, context.Response);

                if (method == "HEAD" || context.Response.StatusCode == (int)HttpStatusCode.NotModified)
                {
                    return;
                }

                await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
                await upstreamStream.CopyToAsync(context.Response.OutputStream, ct);
            }
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET,HEAD,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Range,Content-Type,Accept";
        response.Headers["Access-Control-Expose-Headers"] = "Content-Length,Content-Range,Accept-Ranges,Content-Type";
    }

    private static void ApplyUpstreamHeaders(HttpResponseMessage upstreamResponse, HttpListenerResponse response)
    {
        if (upstreamResponse.Content.Headers.ContentType != null)
        {
            response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();
        }

        if (upstreamResponse.Content.Headers.ContentLength.HasValue)
        {
            var length = upstreamResponse.Content.Headers.ContentLength.Value;
            if (length >= 0)
            {
                response.ContentLength64 = length;
            }
        }

        CopySelectedHeaders(upstreamResponse.Headers, response.Headers);
        CopySelectedHeaders(upstreamResponse.Content.Headers, response.Headers);
    }

    private static void CopySelectedHeaders(HttpHeaders source, WebHeaderCollection target)
    {
        foreach (var header in source)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!PassThroughHeaders.Contains(header.Key)) continue;

            try
            {
                target[header.Key] = string.Join(",", header.Value);
            }
            catch
            {
                // Ignore individual header failures and continue streaming.
            }
        }
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, CancellationToken ct)
    {
        response.ContentType = "text/plain; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync(text.AsMemory(), ct);
        await writer.FlushAsync();
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
#endif

    public async ValueTask DisposeAsync()
    {
#if WINDOWS
        Task? listenLoop = null;
        HttpListener? listener = null;
        CancellationTokenSource? cts = null;

        lock (_sync)
        {
            listenLoop = _listenLoop;
            listener = _listener;
            cts = _listenCts;

            _listenLoop = null;
            _listener = null;
            _listenCts = null;
            _baseUrl = null;
        }

        try { cts?.Cancel(); } catch { }
        try { listener?.Close(); } catch { }
        if (listenLoop != null)
        {
            try { await listenLoop; } catch { }
        }
        cts?.Dispose();
        _upstream.Dispose();
#else
        await Task.CompletedTask;
#endif
    }
}
