using Microsoft.JSInterop;
using PrivateChatApp.State;

namespace PrivateChatApp.WebRtc;

/// <summary>
/// C# ↔ JavaScript boundary for all WebRTC operations.
/// Uses IJSRuntime to call into webrtc-manager.js and exposes [JSInvokable]
/// methods for JS to call back into C# (e.g., mute state changes, errors).
/// </summary>
public class WebRtcBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly CallStore _callStore;
    private DotNetObjectReference<WebRtcBridge>? _dotNetRef;

    public WebRtcBridge(IJSRuntime js, CallStore callStore)
    {
        _js = js;
        _callStore = callStore;
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

    public async Task UpdateSettingsAsync(string? micDeviceId, string? cameraDeviceId, string? resolution, string? fps)
    {
        await _js.InvokeVoidAsync("webrtcManager.updateSettings", new
        {
            micDeviceId,
            cameraDeviceId,
            resolution,
            fps
        });
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
    public void OnError(string message)
    {
        Console.Error.WriteLine($"[WebRTC Error] {message}");
    }

    [JSInvokable]
    public void OnDevicesEnumerated(object devices)
    {
        // Device list available for UI — could be forwarded to CallStore
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch { }
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }
}
