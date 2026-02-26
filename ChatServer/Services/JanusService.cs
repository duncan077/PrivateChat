using System.Text;
using System.Text.Json;

namespace ChatServer.Services;

public class JanusService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _janusUrl;

    public JanusService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _janusUrl = config["Janus:Url"] ?? "http://localhost:8088/janus";
    }

    public async Task<long> CreateSessionAsync()
    {
        var resp = await PostAsync(_janusUrl, new
        {
            janus = "create",
            transaction = NewTxn()
        });
        return resp.GetProperty("data").GetProperty("id").GetInt64();
    }

    public async Task<long> AttachPluginAsync(long sessionId, string plugin = "janus.plugin.videoroom")
    {
        var resp = await PostAsync($"{_janusUrl}/{sessionId}", new
        {
            janus = "attach",
            plugin,
            transaction = NewTxn()
        });
        return resp.GetProperty("data").GetProperty("id").GetInt64();
    }

    public async Task EnsureVideoRoomAsync(long sessionId, long handleId, long roomId)
    {
        var exists = await SendPluginMessageAsync(sessionId, handleId, new
        {
            request = "exists",
            room = roomId
        });

        if (exists.TryGetProperty("plugindata", out var pd)
            && pd.TryGetProperty("data", out var data)
            && data.TryGetProperty("exists", out var ex)
            && ex.GetBoolean())
        {
            return;
        }

        await SendPluginMessageAsync(sessionId, handleId, new
        {
            request = "create",
            room = roomId,
            description = $"Room {roomId}",
            publishers = 20,
            bitrate = 512000,
            videocodec = "vp8",
            audiocodec = "opus",
            is_private = false,
            permanent = false
        });
    }

    public async Task<JsonElement> SendPluginMessageAsync(long sessionId, long handleId, object body, object? jsep = null)
    {
        var msg = new Dictionary<string, object>
        {
            ["janus"] = "message",
            ["transaction"] = NewTxn(),
            ["body"] = body
        };
        if (jsep != null)
            msg["jsep"] = jsep;

        return await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", msg);
    }

    public async Task TrickleAsync(long sessionId, long handleId, object candidate)
    {
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
        {
            janus = "trickle",
            candidate,
            transaction = NewTxn()
        });
    }

    public async Task TrickleCompletedAsync(long sessionId, long handleId)
    {
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
        {
            janus = "trickle",
            candidate = (object?)null,
            transaction = NewTxn()
        });
    }

    public async Task<JsonElement?> LongPollAsync(long sessionId)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        try
        {
            var resp = await client.GetAsync($"{_janusUrl}/{sessionId}?maxev=1");
            var body = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return null;
        }
    }

    public async Task DestroySessionAsync(long sessionId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}", new
            {
                janus = "destroy",
                transaction = NewTxn()
            });
        }
        catch { }
    }

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        var client = _httpClientFactory.CreateClient();
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string NewTxn() => Guid.NewGuid().ToString("N");
}
