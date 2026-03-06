using System.Text.Json;
using System.Linq;
using Microsoft.JSInterop;
using ChatAppBlazor.Services;
using ChatAppBlazor.State;

namespace ChatAppBlazor.WebRtc;

/// <summary>
/// C# ↔ JavaScript boundary for all WebRTC operations.
/// Registered as SCOPED in MAUI Blazor Hybrid — must match IJSRuntime's lifetime.
/// In MAUI the single BlazorWebView creates one scope for the entire app lifetime,
/// so scoped ≈ singleton here.  The JS state persists in the WebView across page
/// navigation; DisposeAsync intentionally does NOT tear down an active call.
/// </summary>
public class WebRtcBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly CallStore _callStore;
    private readonly GrpcChannelProvider _channelProvider;
    private DotNetObjectReference<WebRtcBridge>? _dotNetRef;
    private string? _authToken;
    private string? _userId;
    private string? _userDisplayName;
    private Dictionary<string, string> _participantDirectory = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _participantAvatars = new(StringComparer.OrdinalIgnoreCase);

    public WebRtcBridge(
        IJSRuntime js,
        CallStore callStore,
        GrpcChannelProvider channelProvider)
    {
        _js = js;
        _callStore = callStore;
        _channelProvider = channelProvider;

        // Subscribe to Janus reconnection events from CallStore
        _callStore.OnJanusReconnect += HandleJanusReconnect;
        _callStore.OnWebRtcSignal += HandleStoreWebRtcSignal;
    }

    /// <summary>
    /// Initialize the WebRTC session with Janus. Called after CallStore.JoinCallAsync.
    /// </summary>
    public async Task InitAsync(
        long janusSessionId,
        long janusHandleId,
        long janusRoomId,
        string serverBaseUrl,
        string authToken,
        string userId,
        string userDisplayName,
        IReadOnlyDictionary<string, string>? participantDirectory,
        IReadOnlyDictionary<string, string>? participantAvatarDirectory,
        string iceServers)
    {
        var resolvedServerBaseUrl = ResolveServerBaseUrl(serverBaseUrl);

        _authToken = authToken;
        _userId = userId;
        _userDisplayName = userDisplayName;
        _participantDirectory = NormalizeParticipantDirectory(participantDirectory);
        _participantAvatars = NormalizeParticipantAvatarDirectory(participantAvatarDirectory);

        _dotNetRef?.Dispose();
        _dotNetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("webrtcManager.init", new
        {
            janusSessionId,
            janusHandleId,
            janusRoomId,
            serverBaseUrl = resolvedServerBaseUrl,
            authToken,
            userId,
            userDisplayName,
            participantDirectory = _participantDirectory,
            participantAvatarDirectory = _participantAvatars,
            iceServers,
            dotNetRef = _dotNetRef
        });
    }

    public async Task StopAsync(string reason = "unspecified")
    {
        try
        {
            Console.WriteLine($"[WebRtcBridge] StopAsync called (reason={reason})");
            await _js.InvokeVoidAsync("webrtcManager.stop", reason);
        }
        catch { }
    }

    public async Task ToggleMuteAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.toggleMute");
    }

    public async Task ToggleCameraAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.toggleCamera");
    }

    public async Task StartScreenShareAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.startScreenShare");
    }

    public async Task StopScreenShareAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.stopScreenShare");
    }

    public async Task DeliverSignalAsync(Guid fromUserId, string signalType, string payload)
    {
        await _js.InvokeVoidAsync("webrtcManager.deliverSignal",
            fromUserId.ToString(), signalType, payload);
    }

    public async Task UpdateSettingsAsync(
        string? micDeviceId,
        string? cameraDeviceId,
        string? resolution,
        int fps = 30,
        int bitrateKbps = 2000,
        string? preferredVideoCodec = "h264",
        bool screenShareAudioEnabled = true,
        bool screenShareAutoGainEnabled = true)
    {
        await _js.InvokeVoidAsync("webrtcManager.updateSettings", new
        {
            micDeviceId,
            cameraDeviceId,
            resolution,
            fps,
            bitrateKbps,
            preferredVideoCodec,
            screenShareAudioEnabled,
            screenShareAutoGainEnabled
        });
    }

    /// <summary>
    /// Clone local video to the floating overlay container, or clear it.
    /// </summary>
    public async Task MoveVideoToFloatAsync(string containerId)
    {
        await _js.InvokeVoidAsync("webrtcManager.moveVideoToFloat", containerId);
    }

    public async Task ClearFloatVideoAsync(string containerId)
    {
        await _js.InvokeVoidAsync("webrtcManager.clearFloatVideo", containerId);
    }

    public async Task RebindVideoElementsAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.rebindVideoElements");
    }

    // ─── JS → C# callbacks ─────────────────────────────────────────────────────

    [JSInvokable]
    public void OnMuteChanged(bool isMuted)
    {
        _callStore.UpdateMuteState(isMuted);
    }

    [JSInvokable]
    public void OnCameraChanged(bool isCameraOff)
    {
        _callStore.UpdateCameraState(isCameraOff);
    }

    [JSInvokable]
    public void OnScreenShareChanged(bool isScreenSharing)
    {
        _callStore.UpdateScreenShareState(isScreenSharing);
    }

    [JSInvokable]
    public void OnPushToTalkChanged(bool enabled)
    {
        _callStore.UpdatePushToTalkState(enabled);
    }

    [JSInvokable]
    public void OnError(string message)
    {
        Console.Error.WriteLine($"[WebRTC Error] {message}");
        _callStore.SetError(message);
    }

    [JSInvokable]
    public void OnDevicesEnumerated(string devicesJson)
    {
        try
        {
            var devices = JsonSerializer.Deserialize<List<MediaDeviceInfo>>(devicesJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            _callStore.UpdateDevices(devices);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRTC] Failed to parse devices: {ex.Message}");
        }
    }

    [JSInvokable]
    public void OnRemoteParticipantSeen(string userId)
    {
        if (!Guid.TryParse(userId, out var participantId))
            return;

        _callStore.EnsureRemoteParticipant(participantId);
    }

    /// <summary>
    /// Called from JS when keepalive failures or polling detect session loss.
    /// Triggers CallStore to re-join and get new Janus credentials.
    /// </summary>
    [JSInvokable]
    public void OnMusicBotStateChanged(bool isPlaying, string? title)
    {
        // Music bot feature removed; keep callback to avoid JS interop errors on older scripts.
    }

    [JSInvokable]
    public void OnJanusSessionLost()
    {
        Console.WriteLine("[WebRtcBridge] Janus session lost, triggering reconnection...");
        _ = _callStore.HandleJanusSessionLostAsync();
    }

    private async void HandleStoreWebRtcSignal(Guid fromUserId, string signalType, string payload)
    {
        try
        {
            await DeliverSignalAsync(fromUserId, signalType, payload);
        }
        catch
        {
            // Ignore transient JS/native signaling races.
        }
    }

    /// <summary>
    /// Called by CallStore.OnJanusReconnect when a new Janus session is acquired.
    /// Stops the old JS session and re-initializes with new credentials.
    /// </summary>
    private async void HandleJanusReconnect(Shared.Models.JanusSessionInfo session, Guid roomId)
    {
        try
        {
            Console.WriteLine($"[WebRtcBridge] Re-initializing JS with new Janus session {session.JanusSessionId}...");
            await StopAsync("janus-reconnect");
            await InitAsync(
                session.JanusSessionId,
                session.JanusHandleId,
                session.JanusRoomId,
                session.ServerBaseUrl,
                _authToken ?? "",
                _userId ?? "",
                _userDisplayName ?? _userId ?? "",
                _participantDirectory,
                _participantAvatars,
                session.IceServers
            );
            await ApplyStoredSettingsAsync();
            await UpdateParticipantProfilesAsync(_participantDirectory, _participantAvatars);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRtcBridge] Reconnect init failed: {ex.Message}");
            _callStore.SetError($"Call reconnection failed: {ex.Message}");
        }
    }

    public async Task EnumerateDevicesAsync()
    {
        try
        {
            var devices = await _js.InvokeAsync<MediaDeviceInfo[]>("webrtcManager.enumerateDevices");
            if (devices is { Length: > 0 })
            {
                _callStore.UpdateDevices(devices.ToList());
            }
        }
        catch
        {
            // Some platforms may only update via JS->.NET callback once initialized.
        }
    }

    public async Task SetVolumeAsync(double level)
    {
        await _js.InvokeVoidAsync("webrtcManager.setVolume", level);
    }

    public async Task SetStreamVolumeAsync(double level)
    {
        await _js.InvokeVoidAsync("webrtcManager.setStreamVolume", level);
    }

    public async Task SetPushToTalkAsync(bool enabled, string key)
    {
        await _js.InvokeVoidAsync("webrtcManager.setPushToTalk", enabled, key);
    }

    public async Task SetPushToTalkSoundEnabledAsync(bool enabled)
    {
        await _js.InvokeVoidAsync("webrtcManager.setPushToTalkSoundEnabled", enabled);
    }

    public async Task<IReadOnlyList<string>> GetSupportedVideoCodecsAsync()
    {
        try
        {
            var codecs = await _js.InvokeAsync<string[]>("webrtcManager.getSupportedVideoCodecs");
            if (codecs == null || codecs.Length == 0)
                return [];

            return codecs
                .Select(static c => (c ?? string.Empty).Trim().ToLowerInvariant())
                .Where(static c => c is "h264" or "h265" or "vp8" or "vp9")
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task UpdateParticipantDirectoryAsync(IReadOnlyDictionary<string, string>? participantDirectory)
        => await UpdateParticipantProfilesAsync(participantDirectory, _participantAvatars);

    public async Task UpdateParticipantProfilesAsync(
        IReadOnlyDictionary<string, string>? participantDirectory,
        IReadOnlyDictionary<string, string>? participantAvatarDirectory)
    {
        _participantDirectory = NormalizeParticipantDirectory(participantDirectory);
        _participantAvatars = NormalizeParticipantAvatarDirectory(participantAvatarDirectory);
        await _js.InvokeVoidAsync("webrtcManager.updateParticipantProfiles", _participantDirectory, _participantAvatars);
    }

    public async Task ApplyStoredSettingsAsync()
    {
        await UpdateSettingsAsync(
            _callStore.SelectedMicId,
            _callStore.SelectedCameraId,
            _callStore.SelectedResolution,
            _callStore.SelectedFps,
            _callStore.SelectedBitrate,
            _callStore.SelectedVideoCodec,
            _callStore.ScreenShareAudioEnabled,
            _callStore.ScreenShareAutoGainEnabled);

        await SetVolumeAsync(_callStore.Volume);
        await SetStreamVolumeAsync(_callStore.StreamVolume);
        await SetPushToTalkAsync(_callStore.PushToTalkEnabled, _callStore.PushToTalkKey);
        await SetPushToTalkSoundEnabledAsync(_callStore.PushToTalkSoundEnabled);
    }

    public async ValueTask DisposeAsync()
    {
        _callStore.OnJanusReconnect -= HandleJanusReconnect;
        _callStore.OnWebRtcSignal -= HandleStoreWebRtcSignal;
        // Do NOT stop the call here — the JS layer persists in the WebView
        // and the call should survive page navigation.  Only explicit
        // "Leave Call" (via StopAsync + LeaveCallAsync) tears it down.
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }

    private string ResolveServerBaseUrl(string? serverBaseUrl)
    {
        var fallback = NormalizeBaseUrl(_channelProvider.ServerUrl);
        var candidate = NormalizeBaseUrl(serverBaseUrl);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
        {
            return fallback;
        }

        if (!IsLoopbackHost(candidateUri.Host))
        {
            return candidate;
        }

        // Server returned localhost/127.0.0.1, which is not reachable from remote clients.
        return fallback;
    }

    private static string NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "http://localhost:5000";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return "http://localhost:5000";
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> NormalizeParticipantDirectory(IReadOnlyDictionary<string, string>? participantDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (participantDirectory == null) return result;

        foreach (var (key, value) in participantDirectory)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            result[key.Trim()] = value.Trim();
        }

        return result;
    }

    private Dictionary<string, string> NormalizeParticipantAvatarDirectory(IReadOnlyDictionary<string, string>? participantAvatarDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (participantAvatarDirectory == null) return result;

        foreach (var (key, value) in participantAvatarDirectory)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key.Trim()] = NormalizeAvatarUrl(value.Trim());
        }

        return result;
    }

    private string NormalizeAvatarUrl(string avatarUrl)
    {
        if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
        {
            return avatarUrl;
        }

        var baseUrl = _channelProvider.ServerUrl.TrimEnd('/');
        var path = avatarUrl.StartsWith('/') ? avatarUrl : $"/{avatarUrl}";
        return $"{baseUrl}{path}";
    }
}
