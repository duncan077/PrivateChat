using System.Text.Json;
using System.Linq;
using Microsoft.JSInterop;
using ChatAppBlazor.Serialization;
using ChatAppBlazor.Services;
using ChatAppBlazor.State;
using Shared.Models;

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
        _callStore.OnAnnotationStateChanged += HandleAnnotationStateChanged;
        _callStore.OnAnnotationEvent += HandleAnnotationEvent;
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
            micDeviceId = _callStore.SelectedMicId,
            cameraDeviceId = _callStore.SelectedCameraId,
            resolution = _callStore.SelectedResolution,
            fps = _callStore.SelectedFps,
            bitrateKbps = _callStore.SelectedBitrate,
            preferredVideoCodec = _callStore.SelectedVideoCodec,
            videoContentHint = _callStore.SelectedVideoContentHint,
            hideLocalPreview = _callStore.HideLocalPreview,
            screenShareAudioEnabled = _callStore.ScreenShareAudioEnabled,
            screenShareAutoGainEnabled = _callStore.ScreenShareAutoGainEnabled,
            h264ProfileOverride = _callStore.H264ProfileOverride,
            noiseSuppressionEnabled = _callStore.NoiseSuppressionEnabled,
            voiceGateEnabled = _callStore.VoiceGateEnabled,
            voiceGateAuto = _callStore.VoiceGateAuto,
            voiceGateThreshold = _callStore.VoiceGateThreshold,
            dotNetRef = _dotNetRef
        });

        // The annotation layer holds its own copy of the callback reference, so it
        // has to be re-pointed whenever InitAsync swaps it out (join, reconnect).
        try
        {
            await InitAnnotationsAsync();
        }
        catch
        {
            // Annotation input is optional; never block joining a call.
        }
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
        bool noiseSuppressionEnabled = true,
        bool screenShareAudioEnabled = true,
        bool screenShareAutoGainEnabled = true,
        bool hideLocalPreview = false,
        string? videoContentHint = "motion",
        string? h264ProfileOverride = null,
        bool voiceGateEnabled = false,
        bool voiceGateAuto = true,
        int voiceGateThreshold = 30)
    {
        await _js.InvokeVoidAsync("webrtcManager.updateSettings", new
        {
            micDeviceId,
            cameraDeviceId,
            resolution,
            fps,
            bitrateKbps,
            preferredVideoCodec,
            noiseSuppressionEnabled,
            hideLocalPreview,
            videoContentHint,
            screenShareAudioEnabled,
            screenShareAutoGainEnabled,
            h264ProfileOverride,
            voiceGateEnabled,
            voiceGateAuto,
            voiceGateThreshold
        });
    }

    public async Task<VoiceGateLevelInfo> GetVoiceGateMicLevelAsync()
    {
        try
        {
            return await _js.InvokeAsync<VoiceGateLevelInfo>("webrtcManager.getVoiceGateMicLevel");
        }
        catch
        {
            return new VoiceGateLevelInfo();
        }
    }

    public record struct VoiceGateLevelInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("level")] int Level = 0,
        [property: System.Text.Json.Serialization.JsonPropertyName("threshold")] int Threshold = 0);

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

    // ─── Screen-share annotations ──────────────────────────────────────────────

    /// <summary>
    /// Hands the annotation input layer its .NET callback target. Safe to call more
    /// than once; the JS side just replaces the reference.
    /// </summary>
    public async Task InitAnnotationsAsync()
    {
        _dotNetRef ??= DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("screenAnnotation.init", _dotNetRef, _userId ?? "");
        await PushAnnotationStateAsync();
    }

    public async Task SetAnnotationToolAsync(string tool, string colorHex, double widthScale)
    {
        // One hop, not three — this is wired to a slider, so a single drag was 24 serialized
        // WebView messages.
        await _js.InvokeVoidAsync("screenAnnotation.setToolConfig", tool, colorHex, widthScale);
    }

    public async Task AnnotationUndoAsync() => await _js.InvokeVoidAsync("screenAnnotation.undo");

    public async Task AnnotationClearMineAsync() => await _js.InvokeVoidAsync("screenAnnotation.clearMine");

    public async Task AnnotationClearAllAsync() => await _js.InvokeVoidAsync("screenAnnotation.clearAll");

    /// <summary>
    /// Sends the JS layer the full set of annotatable shares. It keeps one canvas
    /// per entry, so this is a replace-everything push rather than a delta - a
    /// sharer that disappears from the list gets their canvas torn down.
    /// </summary>
    public async Task PushAnnotationStateAsync()
    {
        var inCall = _callStore.IsInCall;
        var sessions = new List<AnnotationSessionPayload>(_callStore.AnnotationSessions.Count);

        if (inCall)
        {
            foreach (var (sharerUserId, state) in _callStore.AnnotationSessions)
            {
                if (!state.Enabled || sharerUserId == Guid.Empty)
                {
                    continue;
                }

                sessions.Add(new AnnotationSessionPayload
                {
                    SharerUserId = sharerUserId.ToString(),
                    CaptureKind = state.CaptureKind.ToString(),
                    NeedsLocalRendering = _callStore.AnnotationsNeedLocalRendering(sharerUserId),
                    AutoFadeSeconds = state.AutoFadeSeconds
                });
            }
        }

        var payload = new AnnotationStatePayload
        {
            // Permission (some sharer allows drawing) AND intent (this user turned
            // their tools on). Without the second half the canvases would swallow
            // clicks/zoom for everyone the moment a share becomes annotatable.
            CanDraw = _callStore.CanAnnotate && _callStore.AnnotationToolsActive,
            Sessions = sessions
        };

        await _js.InvokeVoidAsync(
            "screenAnnotation.setState",
            JsonSerializer.Serialize(payload, AppJsonContext.Default.AnnotationStatePayload));
    }

    [JSInvokable]
    public void OnScreenShareTargetChanged(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(payloadJson, AppJsonContext.Default.ScreenShareTargetPayload);
            if (payload == null)
            {
                return;
            }

            _ = _callStore.SetLocalScreenShareTargetAsync(new ScreenAnnotationCaptureTarget(
                payload.TargetLabel ?? string.Empty,
                payload.DisplaySurface ?? string.Empty,
                payload.FrameWidth,
                payload.FrameHeight));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRtcBridge] Invalid screen-share target payload: {ex.Message}");
        }
    }

    [JSInvokable]
    public void OnAnnotationDrawn(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(payloadJson, AppJsonContext.Default.AnnotationPayload);
            if (payload == null)
            {
                return;
            }

            // JS knows which canvas the stroke landed on; that is the only place the
            // target can come from, since a client may have several shares on screen.
            if (!Guid.TryParse(payload.TargetSharerUserId, out var targetSharerUserId))
            {
                return;
            }

            _ = _callStore.SendAnnotationAsync(new ScreenAnnotationEvent
            {
                Kind = ParseEventKind(payload.Kind),
                StrokeId = payload.StrokeId ?? string.Empty,
                Tool = ParseTool(payload.Tool),
                Color = payload.Color,
                Width = payload.Width,
                Points = payload.Points ?? [],
                TargetSharerUserId = targetSharerUserId
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebRtcBridge] Invalid annotation payload: {ex.Message}");
        }
    }

    private async void HandleAnnotationStateChanged()
    {
        try
        {
            await PushAnnotationStateAsync();
        }
        catch
        {
            // WebView may be navigating.
        }
    }

    private async void HandleAnnotationEvent(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        // Apply the same filters screenAnnotation.applyRemote does, before paying for the
        // Guid.ToString + JSON serialize + interop hop. The hub broadcasts with .All so a
        // drawer receives its own echo, and drawing flushes roughly every 45ms.
        if (ShouldDropAnnotationEvent(fromUserId, annotationEvent))
        {
            return;
        }

        try
        {
            await _js.InvokeVoidAsync(
                "screenAnnotation.applyRemote",
                fromUserId.ToString(),
                SerializeAnnotationEvent(annotationEvent));
        }
        catch
        {
            // Ignore transient JS interop errors while navigating.
        }
    }

    private bool ShouldDropAnnotationEvent(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        // Ink is scoped to one share, and a share with no live session has no canvas
        // to receive it.
        var target = annotationEvent.TargetSharerUserId;
        if (target == Guid.Empty || _callStore.AnnotationSessions.GetValueOrDefault(target)?.Enabled != true)
        {
            return true;
        }

        // Our own echo; JS discards it on the localUserId check.
        if (Guid.TryParse(_userId, out var localUserId) && fromUserId == localUserId)
        {
            return true;
        }

        // When that sharer's overlay is rendered into their published video, JS keeps only
        // a global clear (to drop local echoes) and discards everything else.
        return !_callStore.AnnotationsNeedLocalRendering(target)
            && annotationEvent.Kind != ScreenAnnotationEventKind.ClearAll;
    }

    // Serialized through the camelCase context because the JS side reads
    // payload.kind / payload.strokeId / payload.points.
    private static string SerializeAnnotationEvent(ScreenAnnotationEvent annotationEvent)
        => JsonSerializer.Serialize(
            new AnnotationPayload
            {
                Kind = ToEventKindName(annotationEvent.Kind),
                StrokeId = annotationEvent.StrokeId,
                Tool = ToToolName(annotationEvent.Tool),
                Color = annotationEvent.Color,
                Width = annotationEvent.Width,
                Points = annotationEvent.Points,
                TargetSharerUserId = annotationEvent.TargetSharerUserId.ToString()
            },
            AppJsonContext.Default.AnnotationPayload);

    private static ScreenAnnotationEventKind ParseEventKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "begin" => ScreenAnnotationEventKind.Begin,
        "points" => ScreenAnnotationEventKind.Points,
        "end" => ScreenAnnotationEventKind.End,
        "undo" => ScreenAnnotationEventKind.Undo,
        "clear" => ScreenAnnotationEventKind.Clear,
        "clearall" => ScreenAnnotationEventKind.ClearAll,
        _ => ScreenAnnotationEventKind.Points
    };

    private static string ToEventKindName(ScreenAnnotationEventKind kind) => kind switch
    {
        ScreenAnnotationEventKind.Begin => "begin",
        ScreenAnnotationEventKind.Points => "points",
        ScreenAnnotationEventKind.End => "end",
        ScreenAnnotationEventKind.Undo => "undo",
        ScreenAnnotationEventKind.Clear => "clear",
        ScreenAnnotationEventKind.ClearAll => "clearAll",
        _ => "points"
    };

    private static ScreenAnnotationTool ParseTool(string? tool) => tool?.ToLowerInvariant() switch
    {
        "highlighter" => ScreenAnnotationTool.Highlighter,
        "arrow" => ScreenAnnotationTool.Arrow,
        "rect" => ScreenAnnotationTool.Rectangle,
        "ellipse" => ScreenAnnotationTool.Ellipse,
        _ => ScreenAnnotationTool.Pen
    };

    private static string ToToolName(ScreenAnnotationTool tool) => tool switch
    {
        ScreenAnnotationTool.Highlighter => "highlighter",
        ScreenAnnotationTool.Arrow => "arrow",
        ScreenAnnotationTool.Rectangle => "rect",
        ScreenAnnotationTool.Ellipse => "ellipse",
        _ => "pen"
    };

    internal sealed class ScreenShareTargetPayload
    {
        public string? TargetLabel { get; set; }
        public string? DisplaySurface { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
    }

    internal sealed class AnnotationPayload
    {
        public string? Kind { get; set; }
        public string? StrokeId { get; set; }
        public string? Tool { get; set; }
        public uint Color { get; set; }
        public float Width { get; set; }
        public float[]? Points { get; set; }

        /// <summary>Which share the stroke was drawn on, as a Guid string.</summary>
        public string? TargetSharerUserId { get; set; }
    }

    /// <summary>One annotatable share, as the JS canvas layer sees it.</summary>
    internal sealed class AnnotationSessionPayload
    {
        public string SharerUserId { get; set; } = string.Empty;
        public string? CaptureKind { get; set; }
        public bool NeedsLocalRendering { get; set; }
        public int AutoFadeSeconds { get; set; }
    }

    /// <summary>The complete set of annotatable shares plus this user's pen state.</summary>
    internal sealed class AnnotationStatePayload
    {
        public bool CanDraw { get; set; }
        public List<AnnotationSessionPayload> Sessions { get; set; } = [];
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
    public void OnHideLocalPreviewChanged(bool hideLocalPreview)
    {
        _callStore.SetHideLocalPreviewPreference(hideLocalPreview);
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
            var devices = JsonSerializer.Deserialize(devicesJson, AppJsonContext.Default.ListMediaDeviceInfo) ?? [];
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

    /// <summary>
    /// Raised when the publisher PeerConnection becomes usable, or stops being usable.
    /// IsInCall goes true the moment the hub join returns, which is well before a local
    /// track can reach anyone - this is what the media controls actually depend on.
    /// </summary>
    [JSInvokable]
    public void OnPublisherReadyChanged(bool isReady)
    {
        _callStore.UpdatePublisherReady(isReady);
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

    /// <summary>
    /// Routes playback to a specific output device. Empty means system default.
    /// Chromium-only; a no-op elsewhere.
    /// </summary>
    public async Task SetAudioOutputDeviceAsync(string? deviceId)
    {
        try
        {
            await _js.InvokeVoidAsync("webrtcManager.setAudioOutputDevice", deviceId ?? string.Empty);
        }
        catch
        {
            // setSinkId is unsupported on some platforms; playback stays on default.
        }
    }

    public async Task SetVolumeAsync(double level)
    {
        await _js.InvokeVoidAsync("webrtcManager.setVolume", level);
    }

    /// <summary>Current mic volume (0-200%) for one user, across all their feeds.</summary>
    public async Task<double> GetUserMicVolumeAsync(Guid userId)
    {
        return await _js.InvokeAsync<double>("webrtcManager.getUserMicVolume", userId.ToString());
    }

    /// <summary>Sets one user's mic volume (0-200%); applies live to their feeds.</summary>
    public async Task SetUserMicVolumeAsync(Guid userId, double percent)
    {
        await _js.InvokeVoidAsync("webrtcManager.setUserMicVolume", userId.ToString(), percent);
    }

    /// <summary>Visual filter only — hidden participants keep playing audio.</summary>
    public async Task SetHideNonVideoTilesAsync(bool enabled)
    {
        await _js.InvokeVoidAsync("webrtcManager.setHideNoVideoTiles", enabled);
    }

    public async Task SetStreamVolumeAsync(double level)
    {
        await _js.InvokeVoidAsync("webrtcManager.setStreamVolume", level);
    }

    public async Task StartMicTestAsync(string? micDeviceId)
    {
        await _js.InvokeVoidAsync("webrtcManager.startMicTest", micDeviceId);
    }

    public async Task StopMicTestAsync()
    {
        await _js.InvokeVoidAsync("webrtcManager.stopMicTest");
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
                .Where(static c => c is "h264" or "h265" or "hvc1" or "av1" or "vp8" or "vp9")
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task<H264CodecCapability[]> GetH264CodecCapabilitiesAsync()
    {
        try
        {
            var caps = await _js.InvokeAsync<H264CodecCapability[]>("webrtcManager.getH264CodecCapabilities");
            return caps ?? [];
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
            _callStore.NoiseSuppressionEnabled,
            _callStore.ScreenShareAudioEnabled,
            _callStore.ScreenShareAutoGainEnabled,
            _callStore.HideLocalPreview,
            _callStore.SelectedVideoContentHint,
            _callStore.H264ProfileOverride,
            _callStore.VoiceGateEnabled,
            _callStore.VoiceGateAuto,
            _callStore.VoiceGateThreshold);

        await SetVolumeAsync(_callStore.Volume);
        await SetStreamVolumeAsync(_callStore.StreamVolume);
        await SetPushToTalkAsync(_callStore.PushToTalkEnabled, _callStore.PushToTalkKey);
        await SetPushToTalkSoundEnabledAsync(_callStore.PushToTalkSoundEnabled);
    }

    public async ValueTask DisposeAsync()
    {
        _callStore.OnJanusReconnect -= HandleJanusReconnect;
        _callStore.OnWebRtcSignal -= HandleStoreWebRtcSignal;
        _callStore.OnAnnotationStateChanged -= HandleAnnotationStateChanged;
        _callStore.OnAnnotationEvent -= HandleAnnotationEvent;
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

public record H264CodecCapability(
    string ProfileLevelId,
    int PacketizationMode,
    string SdpFmtpLine);
