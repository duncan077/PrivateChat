using System.Text.Json;
using Microsoft.JSInterop;
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
    private DotNetObjectReference<WebRtcBridge>? _dotNetRef;
    private string? _authToken;
    private string? _userId;

    public WebRtcBridge(IJSRuntime js, CallStore callStore)
    {
        _js = js;
        _callStore = callStore;

        // Subscribe to Janus reconnection events from CallStore
        _callStore.OnJanusReconnect += HandleJanusReconnect;
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
        string iceServers)
    {
        _authToken = authToken;
        _userId = userId;

        _dotNetRef?.Dispose();
        _dotNetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("webrtcManager.init", new
        {
            janusSessionId,
            janusHandleId,
            janusRoomId,
            serverBaseUrl,
            authToken,
            userId,
            iceServers,
            dotNetRef = _dotNetRef
        });
    }

    public async Task StopAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("webrtcManager.stop");
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

    public async Task UpdateSettingsAsync(string? micDeviceId, string? cameraDeviceId, string? resolution, int fps = 30, int bitrateKbps = 2000)
    {
        await _js.InvokeVoidAsync("webrtcManager.updateSettings", new
        {
            micDeviceId,
            cameraDeviceId,
            resolution,
            fps,
            bitrateKbps
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

    /// <summary>
    /// Called from JS when keepalive failures or polling detect session loss.
    /// Triggers CallStore to re-join and get new Janus credentials.
    /// </summary>
    [JSInvokable]
    public void OnJanusSessionLost()
    {
        Console.WriteLine("[WebRtcBridge] Janus session lost, triggering reconnection...");
        _ = _callStore.HandleJanusSessionLostAsync();
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
            await StopAsync();
            await InitAsync(
                session.JanusSessionId,
                session.JanusHandleId,
                session.JanusRoomId,
                session.ServerBaseUrl,
                _authToken ?? "",
                _userId ?? "",
                session.IceServers
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRtcBridge] Reconnect init failed: {ex.Message}");
            _callStore.SetError($"Call reconnection failed: {ex.Message}");
        }
    }

    public async Task EnumerateDevicesAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.enumerateDevices");
    }

    public async Task SetVolumeAsync(double level)
    {
        await _js.InvokeVoidAsync("webrtcManager.setVolume", level);
    }

    public async Task SetPushToTalkAsync(bool enabled, string key)
    {
        await _js.InvokeVoidAsync("webrtcManager.setPushToTalk", enabled, key);
    }

    public async ValueTask DisposeAsync()
    {
        _callStore.OnJanusReconnect -= HandleJanusReconnect;
        // Do NOT stop the call here — the JS layer persists in the WebView
        // and the call should survive page navigation.  Only explicit
        // "Leave Call" (via StopAsync + LeaveCallAsync) tears it down.
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }
}
