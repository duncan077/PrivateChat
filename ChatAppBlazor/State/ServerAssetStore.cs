using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>
/// Server-wide custom emoji and stickers.
///
/// Propagation is pull-based: the list refreshes on connect, on reconnect, and whenever
/// a picker opens. There is no push event, because a unary service cannot address the
/// hub's groups (their names are hub-prefixed) and a broadcast that silently reached
/// nobody would be worse than an explicit refresh.
/// </summary>
public class ServerAssetStore : IDisposable
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly MarkdownRenderService _markdown;

    public ServerAssetStore(ApiClientService api, ChatClientService chat, MarkdownRenderService markdown)
    {
        _api = api;
        _chat = chat;
        _markdown = markdown;

        // The renderer turns :name: into an <img>; it asks us to resolve the name.
        _markdown.ResolveCustomEmoji = name => EmojiByName.TryGetValue(name, out var asset) ? asset.Url : null;

        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;
    }

    public Dictionary<string, CustomAssetInfo> EmojiByName { get; private set; } = new(StringComparer.Ordinal);
    public List<CustomAssetInfo> Stickers { get; private set; } = [];

    /// <summary>
    /// Bumped on every change. Rendered message HTML is memoized, and a newly added
    /// emoji has to invalidate messages that already rendered its token as text.
    /// </summary>
    public int RegistryVersion { get; private set; }

    public string? Error { get; private set; }

    public event Action? OnChange;

    public bool TryGetEmoji(string name, out CustomAssetInfo asset) => EmojiByName.TryGetValue(name, out asset!);

    public async Task RefreshAsync()
    {
        try
        {
            var assets = await _api.GetCustomAssetsAsync();

            EmojiByName = assets
                .Where(a => a.Kind == CustomAssetKind.Emoji)
                .GroupBy(a => a.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            Stickers = assets.Where(a => a.Kind == CustomAssetKind.Sticker).ToList();

            RegistryVersion++;
            // Invalidates memoized message HTML so a newly added emoji shows up in
            // messages that already rendered its token as plain text.
            _markdown.RegistryVersion = RegistryVersion;
            Error = null;
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load custom emoji: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[ServerAssetStore] {Error}");
        }
    }

    public async Task<bool> CreateAsync(CustomAssetKind kind, string name, string url)
    {
        try
        {
            await _api.CreateCustomAssetAsync(kind, name, url);
            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            Error = ExtractMessage(ex);
            OnChange?.Invoke();
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid assetId)
    {
        try
        {
            await _api.DeleteCustomAssetAsync(assetId);
            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            Error = ExtractMessage(ex);
            OnChange?.Invoke();
            return false;
        }
    }

    /// <summary>Server-side validation messages arrive wrapped; show the useful part.</summary>
    private static string ExtractMessage(Exception ex)
    {
        var message = ex.Message;
        var marker = message.IndexOf("Detail=\"", StringComparison.Ordinal);
        if (marker < 0) return message;

        var start = marker + "Detail=\"".Length;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : message;
    }

    private async void HandleConnectionStateChanged(bool connected)
    {
        if (connected) await RefreshAsync();
    }

    public void Dispose()
    {
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
    }
}
