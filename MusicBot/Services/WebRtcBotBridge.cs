using Microsoft.JSInterop;
using Shared.Models;

namespace MusicBot.Services;

public sealed class WebRtcBotBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly GrpcChannelProvider _channelProvider;
    private DotNetObjectReference<WebRtcBotBridge>? _dotNetRef;

    public long CurrentJanusSessionId { get; private set; }
    public long CurrentJanusHandleId { get; private set; }
    public long CurrentJanusRoomId { get; private set; }
    public Guid? CurrentRoomId { get; private set; }

    public event Action<string>? Error;
    public event Action<bool, string?>? MusicBotStateChanged;
    public event Action? JanusSessionLost;

    public WebRtcBotBridge(IJSRuntime js, GrpcChannelProvider channelProvider)
    {
        _js = js;
        _channelProvider = channelProvider;
    }

    public async Task InitAsync(JanusSessionInfo session, string authToken, Guid userId, string displayName)
    {
        CurrentJanusSessionId = session.JanusSessionId;
        CurrentJanusHandleId = session.JanusHandleId;
        CurrentJanusRoomId = session.JanusRoomId;
        CurrentRoomId = session.RoomId;

        _dotNetRef?.Dispose();
        _dotNetRef = DotNetObjectReference.Create(this);

        await _js.InvokeVoidAsync("webrtcManager.init", new
        {
            janusSessionId = session.JanusSessionId,
            janusHandleId = session.JanusHandleId,
            janusRoomId = session.JanusRoomId,
            botMode = true,
            serverBaseUrl = string.IsNullOrWhiteSpace(session.ServerBaseUrl) ? _channelProvider.ServerUrl : session.ServerBaseUrl,
            authToken,
            userId = userId.ToString("D"),
            userDisplayName = string.IsNullOrWhiteSpace(displayName) ? userId.ToString("D") : displayName,
            participantDirectory = new Dictionary<string, string>(),
            iceServers = session.IceServers ?? "[]",
            dotNetRef = _dotNetRef
        });

        // Keep publisher lightweight for bot use.
        await _js.InvokeVoidAsync("webrtcManager.updateSettings", new
        {
            micDeviceId = (string?)null,
            cameraDeviceId = (string?)null,
            resolution = "1280x720",
            fps = 30,
            bitrateKbps = 256,
            preferredVideoCodec = "h264",
            screenShareAudioEnabled = true
        });
    }

    public async Task StartMusicPlaybackAsync(string streamUrl, string title, double volume = 0.9)
    {
        await StartMusicPlaybackAsync([streamUrl], title, volume);
    }

    public async Task StartMusicPlaybackAsync(IReadOnlyList<string> streamUrls, string title, double volume = 0.9)
    {
        if (streamUrls == null || streamUrls.Count == 0)
        {
            throw new InvalidOperationException("No stream URLs available for playback.");
        }

        await _js.InvokeVoidAsync("webrtcManager.startMusicBotPlayback", streamUrls.ToArray(), title, volume);
    }

    public async Task StopMusicPlaybackAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.stopMusicBotPlayback");
    }

    public async Task StopAsync(string reason)
    {
        await _js.InvokeVoidAsync("webrtcManager.stop", reason);
        CurrentJanusSessionId = 0;
        CurrentJanusHandleId = 0;
        CurrentJanusRoomId = 0;
        CurrentRoomId = null;
    }

    [JSInvokable]
    public void OnError(string message) => Error?.Invoke(message);

    [JSInvokable]
    public void OnMusicBotStateChanged(bool isPlaying, string? title) => MusicBotStateChanged?.Invoke(isPlaying, title);

    [JSInvokable]
    public void OnJanusSessionLost()
    {
        // Invalidate local binding so runtime doesn't short-circuit rejoin checks.
        CurrentJanusSessionId = 0;
        CurrentJanusHandleId = 0;
        CurrentJanusRoomId = 0;
        CurrentRoomId = null;
        JanusSessionLost?.Invoke();
    }

    [JSInvokable] public void OnMuteChanged(bool _) { }
    [JSInvokable] public void OnCameraChanged(bool _) { }
    [JSInvokable] public void OnScreenShareChanged(bool _) { }
    [JSInvokable] public void OnPushToTalkChanged(bool _) { }
    [JSInvokable] public void OnDevicesEnumerated(string _) { }
    [JSInvokable] public void OnRemoteParticipantSeen(string _) { }

    public ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
        _dotNetRef = null;
        return ValueTask.CompletedTask;
    }
}
