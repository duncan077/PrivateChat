using ChatAppBlazor.Services;
using Microsoft.Maui.Storage;
using Shared.Models;

namespace ChatAppBlazor.State;

public record MediaDeviceInfo(string DeviceId, string Label, string Kind);

/// <summary>
/// Manages the WebRTC call lifecycle: join, leave, participants, media state.
/// Acts as the bridge between ChatClientService hub events and the WebRTC JS layer.
/// </summary>
public class CallStore : IDisposable
{
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private const string PrefPrefix = "call.settings.";
    private const string PrefSelectedMicId = PrefPrefix + "selectedMicId";
    private const string PrefSelectedCameraId = PrefPrefix + "selectedCameraId";
    private const string PrefSelectedResolution = PrefPrefix + "selectedResolution";
    private const string PrefSelectedFps = PrefPrefix + "selectedFps";
    private const string PrefSelectedBitrate = PrefPrefix + "selectedBitrate";
    private const string PrefSelectedVideoCodec = PrefPrefix + "selectedVideoCodec";
    private const string PrefScreenShareAudioEnabled = PrefPrefix + "screenShareAudioEnabled";
    private const string PrefGpuAccelerationEnabled = PrefPrefix + "gpuAccelerationEnabled";
    private const string PrefForcePlatformHevcEncoderSupport = PrefPrefix + "forcePlatformHevcEncoderSupport";
    private const string PrefEnableWebRtcH265SendFeature = PrefPrefix + "enableWebRtcH265SendFeature";
    private const string PrefLegacyForcePlatformHevcDecoderSupport = PrefPrefix + "forcePlatformHevcDecoderSupport";
    private const string PrefVolume = PrefPrefix + "volume";
    private const string PrefStreamVolume = PrefPrefix + "streamVolume";
    private const string PrefPushToTalkEnabled = PrefPrefix + "pushToTalkEnabled";
    private const string PrefPushToTalkKey = PrefPrefix + "pushToTalkKey";
    private const string PrefPushToTalkSoundEnabled = PrefPrefix + "pushToTalkSoundEnabled";

    public bool IsInCall { get; private set; }
    public Guid? ActiveRoomId { get; private set; }
    public JanusSessionInfo? Session { get; private set; }
    public List<Guid> Participants { get; private set; } = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _roomCallParticipants = [];
    public bool IsMuted { get; set; }
    public bool IsCameraOff { get; set; }
    public bool IsScreenSharing { get; set; }
    public bool UseFloatingOverlayInActiveRoom { get; private set; }
    public string? Error { get; private set; }

    // Device & settings state
    public List<MediaDeviceInfo> Microphones { get; private set; } = [];
    public List<MediaDeviceInfo> Cameras { get; private set; } = [];
    public string? SelectedMicId { get; set; }
    public string? SelectedCameraId { get; set; }
    public string SelectedResolution { get; set; } = "1280x720";
    public int SelectedFps { get; set; } = 30;
    public int SelectedBitrate { get; set; } = 2000; // kbps
    public string SelectedVideoCodec { get; set; } = "h264";
    public bool ScreenShareAudioEnabled { get; set; } = true;
    public bool GpuAccelerationEnabled { get; set; } = true;
    public bool ForcePlatformHevcEncoderSupport { get; set; } = true;
    public bool EnableWebRtcH265SendFeature { get; set; } = true;
    public double Volume { get; set; } = 1.0; // Mic/output-to-peers volume
    public double StreamVolume { get; set; } = 1.0; // Incoming stream playback volume
    public bool ShowSettings { get; set; }

    // Push-to-talk
    public bool PushToTalkEnabled { get; set; }
    public string PushToTalkKey { get; set; } = "Space";
    public bool PushToTalkSoundEnabled { get; set; } = true;

    public event Action? OnChange;

    /// <summary>
    /// Fired when the Janus session is lost and a new session has been acquired.
    /// WebRtcBridge subscribes to this to re-initialize JS with new credentials.
    /// Payload: (sessionInfo, roomId)
    /// </summary>
    public event Action<JanusSessionInfo, Guid>? OnJanusReconnect;

    /// <summary>
    /// Fired when a WebRTC signal is received from another user via the gRPC hub.
    /// The call panel subscribes to this to forward signals to the JS layer.
    /// </summary>
    public event Action<Guid, string, string>? OnWebRtcSignal;
    private bool _isAutoRejoiningAfterMove;

    public CallStore(ChatClientService chat, AuthStateProvider auth)
    {
        _chat = chat;
        _auth = auth;
        LoadSettingsPreferences();

        _chat.OnUserJoinedVoiceVideo += HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo += HandleParticipantLeft;
        _chat.OnUserKickedFromVoiceVideo += HandleKickedFromVoiceVideo;
        _chat.OnWebRtcSignalReceived += HandleWebRtcSignal;
        _chat.OnUserMoved += HandleUserMoved;
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;
    }

    /// <summary>
    /// Called by ActiveCallPanel to initiate a call.
    /// Returns the JanusSessionInfo for the WebRTC JS layer.
    /// </summary>
    public async Task<JanusSessionInfo?> JoinCallAsync(Guid roomId)
    {
        if (roomId == Guid.Empty) return null;

        try
        {
            if (IsInCall)
            {
                if (ActiveRoomId == roomId && Session != null)
                {
                    return Session;
                }

                if (ActiveRoomId != null)
                {
                    try
                    {
                        await _chat.LeaveVoiceVideoAsync(ActiveRoomId.Value);
                    }
                    catch
                    {
                        // Continue switching call state even if server-side leave is transiently failing.
                    }

                    RemoveRoomParticipant(ActiveRoomId.Value, _auth.CurrentUser?.Id ?? Guid.Empty);
                }

                Session = null;
                ActiveRoomId = null;
                IsInCall = false;
                Participants.Clear();
                IsScreenSharing = false;
                UseFloatingOverlayInActiveRoom = false;
            }

            Error = null;
            Session = await _chat.JoinVoiceVideoAsync(roomId);
            ActiveRoomId = roomId;
            IsInCall = true;
            IsMuted = true;
            IsCameraOff = true;
            IsScreenSharing = false;
            UseFloatingOverlayInActiveRoom = false;
            Participants.Clear();
            AddRoomParticipant(roomId, _auth.CurrentUser?.Id ?? Guid.Empty);
            await RefreshRoomParticipantsAsync(roomId);
            NotifyChanged();
            return Session;
        }
        catch (Exception ex)
        {
            Error = $"Failed to join call: {ex.Message}";
            NotifyChanged();
            return null;
        }
    }

    public async Task LeaveCallAsync()
    {
        if (!IsInCall || ActiveRoomId == null) return;
        var roomId = ActiveRoomId.Value;

        try
        {
            await _chat.LeaveVoiceVideoAsync(roomId);
        }
        catch { }
        finally
        {
            IsInCall = false;
            ActiveRoomId = null;
            Session = null;
            Participants.Clear();
            IsMuted = false;
            IsCameraOff = false;
            IsScreenSharing = false;
            UseFloatingOverlayInActiveRoom = false;
            Error = null;
            RemoveRoomParticipant(roomId, _auth.CurrentUser?.Id ?? Guid.Empty);
            NotifyChanged();
        }
    }

    public void SetUseFloatingOverlayInActiveRoom(bool useFloatingOverlay)
    {
        if (UseFloatingOverlayInActiveRoom == useFloatingOverlay)
        {
            return;
        }

        UseFloatingOverlayInActiveRoom = useFloatingOverlay;
        NotifyChanged();
    }

    /// <summary>
    /// Called by WebRtcBridge when Janus session is lost.
    /// Re-joins the voice/video room to get a new Janus session, then fires OnJanusReconnect.
    /// </summary>
    public async Task HandleJanusSessionLostAsync()
    {
        if (!IsInCall || ActiveRoomId == null) return;

        var roomId = ActiveRoomId.Value;
        Console.WriteLine($"[CallStore] Janus session lost for room {roomId}, re-joining...");

        try
        {
            Session = await _chat.JoinVoiceVideoAsync(roomId);
            Console.WriteLine($"[CallStore] Got new Janus session: {Session.JanusSessionId}");
            OnJanusReconnect?.Invoke(Session, roomId);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CallStore] Janus reconnect failed: {ex.Message}");
            Error = $"Call reconnection failed: {ex.Message}";
            NotifyChanged();
        }
    }

    public void UpdateMuteState(bool isMuted)
    {
        IsMuted = isMuted;
        NotifyChanged();
    }

    public void UpdateCameraState(bool isCameraOff)
    {
        IsCameraOff = isCameraOff;
        NotifyChanged();
    }

    public void UpdateScreenShareState(bool isScreenSharing)
    {
        IsScreenSharing = isScreenSharing;
        NotifyChanged();
    }

    public void SetError(string message)
    {
        Error = message;
        NotifyChanged();
    }

    public void ClearError()
    {
        Error = null;
        NotifyChanged();
    }

    public void UpdateDevices(List<MediaDeviceInfo> devices)
    {
        Microphones = devices.Where(d => d.Kind == "audioinput").ToList();
        Cameras = devices.Where(d => d.Kind == "videoinput").ToList();
        NotifyChanged();
    }

    public void UpdatePushToTalkState(bool enabled)
    {
        PushToTalkEnabled = enabled;
        SavePushToTalkPreferences();
        NotifyChanged();
    }

    public void ApplyCallSettings(
        string? micDeviceId,
        string? cameraDeviceId,
        string? resolution,
        int fps,
        int bitrateKbps,
        string? videoCodec,
        bool screenShareAudioEnabled,
        bool gpuAccelerationEnabled,
        bool forcePlatformHevcEncoderSupport,
        bool enableWebRtcH265SendFeature)
    {
        SelectedMicId = NormalizeDeviceId(micDeviceId);
        SelectedCameraId = NormalizeDeviceId(cameraDeviceId);
        SelectedResolution = NormalizeResolution(resolution);
        SelectedFps = ClampFps(fps);
        SelectedBitrate = ClampBitrate(bitrateKbps);
        SelectedVideoCodec = NormalizeVideoCodec(videoCodec);
        ScreenShareAudioEnabled = screenShareAudioEnabled;
        GpuAccelerationEnabled = gpuAccelerationEnabled;
        ForcePlatformHevcEncoderSupport = forcePlatformHevcEncoderSupport;
        EnableWebRtcH265SendFeature = enableWebRtcH265SendFeature;
        SaveCallSettingsPreferences();
        NotifyChanged();
    }

    public void SetVolumePreference(double volume)
    {
        Volume = ClampVolume(volume);
        SaveVolumePreference();
        NotifyChanged();
    }

    public void SetStreamVolumePreference(double volume)
    {
        StreamVolume = ClampVolume(volume);
        SaveVolumePreference();
        NotifyChanged();
    }

    public void SetPushToTalkPreference(bool enabled, string? key)
    {
        PushToTalkEnabled = enabled;
        PushToTalkKey = NormalizePushToTalkKey(key);
        SavePushToTalkPreferences();
        NotifyChanged();
    }

    public void SetPushToTalkSoundPreference(bool enabled)
    {
        PushToTalkSoundEnabled = enabled;
        SavePushToTalkPreferences();
        NotifyChanged();
    }

    public void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
        NotifyChanged();
    }

    public async Task RefreshRoomParticipantsAsync(Guid roomId)
    {
        if (roomId == Guid.Empty) return;

        try
        {
            var participants = await _chat.GetVoiceVideoParticipantsAsync(roomId);
            _roomCallParticipants[roomId] = participants
                .Where(id => id != Guid.Empty)
                .ToHashSet();

            if (ActiveRoomId == roomId)
            {
                var myId = _auth.CurrentUser?.Id;
                if (myId != null && !participants.Contains(myId.Value))
                {
                    EndCallLocally(roomId, "You were removed from this call.");
                    return;
                }

                Participants = participants
                    .Where(id => id != Guid.Empty && id != myId)
                    .Distinct()
                    .ToList();
            }

            NotifyChanged();
        }
        catch
        {
            // Ignore transient fetch failures (e.g., switching rooms during reconnect).
        }
    }

    public IReadOnlyCollection<Guid> GetRoomParticipants(Guid roomId)
    {
        if (_roomCallParticipants.TryGetValue(roomId, out var users))
        {
            return users.ToArray();
        }

        return [];
    }

    public bool IsUserConnectedInRoom(Guid roomId, Guid userId)
    {
        if (roomId == Guid.Empty || userId == Guid.Empty) return false;
        return _roomCallParticipants.TryGetValue(roomId, out var users) && users.Contains(userId);
    }

    private void HandleParticipantJoined(Guid roomId, Guid userId)
    {
        var changed = AddRoomParticipant(roomId, userId);

        if (roomId == ActiveRoomId && userId != _auth.CurrentUser?.Id && !Participants.Contains(userId))
        {
            Participants.Add(userId);
            changed = true;
        }

        if (changed) NotifyChanged();
    }

    private void HandleParticipantLeft(Guid roomId, Guid userId)
    {
        if (roomId == ActiveRoomId && userId == _auth.CurrentUser?.Id)
        {
            EndCallLocally(roomId, "You were removed from this call.");
            return;
        }

        var changed = RemoveRoomParticipant(roomId, userId);

        if (roomId == ActiveRoomId)
        {
            changed = Participants.Remove(userId) || changed;
        }

        if (changed) NotifyChanged();
    }

    public void EnsureRemoteParticipant(Guid userId)
    {
        if (!IsInCall || ActiveRoomId == null || userId == _auth.CurrentUser?.Id)
            return;

        var changed = AddRoomParticipant(ActiveRoomId.Value, userId);
        if (!Participants.Contains(userId))
        {
            Participants.Add(userId);
            changed = true;
        }

        if (changed) NotifyChanged();
    }

    private void HandleWebRtcSignal(Guid roomId, Guid fromUserId, string signalType, string payload)
    {
        if (roomId != ActiveRoomId) return;
        OnWebRtcSignal?.Invoke(fromUserId, signalType, payload);
    }

    private void HandleKickedFromVoiceVideo(Guid roomId, Guid userId, string reason)
    {
        if (roomId != ActiveRoomId || userId != _auth.CurrentUser?.Id)
        {
            return;
        }

        EndCallLocally(roomId, string.IsNullOrWhiteSpace(reason) ? "You were removed from this call." : reason);
    }

    private async void HandleUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        if (_isAutoRejoiningAfterMove) return;
        if (userId != _auth.CurrentUser?.Id) return;
        if (!IsInCall || ActiveRoomId != sourceRoomId) return;
        if (targetRoomId == Guid.Empty || targetRoomId == sourceRoomId) return;

        try
        {
            _isAutoRejoiningAfterMove = true;
            var session = await JoinCallAsync(targetRoomId);
            if (session == null)
            {
                return;
            }

            // Reinitialize the JS peer state with the new Janus credentials.
            OnJanusReconnect?.Invoke(session, targetRoomId);
        }
        catch (Exception ex)
        {
            Error = $"Failed to auto-join moved call: {ex.Message}";
            NotifyChanged();
        }
        finally
        {
            _isAutoRejoiningAfterMove = false;
        }
    }

    private async void HandleConnectionStateChanged(bool connected)
    {
        if (!connected || !IsInCall || ActiveRoomId == null) return;

        var roomId = ActiveRoomId.Value;
        try
        {
            await _chat.JoinRoomAsync(roomId);
            await RefreshRoomParticipantsAsync(roomId);
        }
        catch
        {
            // Ignore transient reconnect/join races.
        }
    }

    private void LoadSettingsPreferences()
    {
        var prefs = Preferences.Default;

        SelectedMicId = NormalizeDeviceId(prefs.Get(PrefSelectedMicId, string.Empty));
        SelectedCameraId = NormalizeDeviceId(prefs.Get(PrefSelectedCameraId, string.Empty));
        SelectedResolution = NormalizeResolution(prefs.Get(PrefSelectedResolution, "1280x720"));
        SelectedFps = ClampFps(prefs.Get(PrefSelectedFps, 30));
        SelectedBitrate = ClampBitrate(prefs.Get(PrefSelectedBitrate, 2000));
        SelectedVideoCodec = NormalizeVideoCodec(prefs.Get(PrefSelectedVideoCodec, "h264"));
        ScreenShareAudioEnabled = prefs.Get(PrefScreenShareAudioEnabled, true);
        GpuAccelerationEnabled = prefs.Get(PrefGpuAccelerationEnabled, true);
        ForcePlatformHevcEncoderSupport = prefs.Get(
            PrefForcePlatformHevcEncoderSupport,
            prefs.Get(PrefLegacyForcePlatformHevcDecoderSupport, true));
        EnableWebRtcH265SendFeature = prefs.Get(PrefEnableWebRtcH265SendFeature, true);
        Volume = ClampVolume(prefs.Get(PrefVolume, 1.0));
        StreamVolume = ClampVolume(prefs.Get(PrefStreamVolume, 1.0));
        PushToTalkEnabled = prefs.Get(PrefPushToTalkEnabled, false);
        PushToTalkKey = NormalizePushToTalkKey(prefs.Get(PrefPushToTalkKey, "Space"));
        PushToTalkSoundEnabled = prefs.Get(PrefPushToTalkSoundEnabled, true);
    }

    private void SaveCallSettingsPreferences()
    {
        var prefs = Preferences.Default;
        prefs.Set(PrefSelectedMicId, SelectedMicId ?? string.Empty);
        prefs.Set(PrefSelectedCameraId, SelectedCameraId ?? string.Empty);
        prefs.Set(PrefSelectedResolution, SelectedResolution);
        prefs.Set(PrefSelectedFps, SelectedFps);
        prefs.Set(PrefSelectedBitrate, SelectedBitrate);
        prefs.Set(PrefSelectedVideoCodec, SelectedVideoCodec);
        prefs.Set(PrefScreenShareAudioEnabled, ScreenShareAudioEnabled);
        prefs.Set(PrefGpuAccelerationEnabled, GpuAccelerationEnabled);
        prefs.Set(PrefForcePlatformHevcEncoderSupport, ForcePlatformHevcEncoderSupport);
        prefs.Set(PrefEnableWebRtcH265SendFeature, EnableWebRtcH265SendFeature);
    }

    public static string BuildWindowsWebView2Arguments(IPreferences? preferences = null)
    {
        var prefs = preferences ?? Preferences.Default;
        var enableGpu = prefs.Get(PrefGpuAccelerationEnabled, true);
        var enableHevcEncoder = prefs.Get(
            PrefForcePlatformHevcEncoderSupport,
            prefs.Get(PrefLegacyForcePlatformHevcDecoderSupport, true));
        var enableWebRtcH265Send = prefs.Get(PrefEnableWebRtcH265SendFeature, true);
        var args = new List<string>
        {
            "--disable-features=WebRtcHideLocalIpsWithMdns",
            "--force-webrtc-ip-handling-policy=default_public_interface_only",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows"
        };

        if (enableGpu)
        {
            args.Add("--enable-accelerated-video-decode");
            args.Add("--enable-accelerated-video-encode");
            args.Add("--enable-gpu-rasterization");
            args.Add("--enable-zero-copy");
            args.Add("--disable-software-rasterizer");
        }
        else
        {
            args.Add("--disable-gpu");
            args.Add("--disable-gpu-compositing");
        }

        var enabledFeatures = new List<string>();
        if (enableHevcEncoder)
        {
            enabledFeatures.Add("PlatformHEVCEncoderSupport");
            if (enableWebRtcH265Send)
            {
                enabledFeatures.Add("WebRtcAllowH265Send");
            }
        }

        if (enabledFeatures.Count > 0)
        {
            args.Add($"--enable-features={string.Join(",", enabledFeatures)}");
        }

        return string.Join(" ", args);
    }

    private void SaveVolumePreference()
    {
        Preferences.Default.Set(PrefVolume, Volume);
        Preferences.Default.Set(PrefStreamVolume, StreamVolume);
    }

    private void SavePushToTalkPreferences()
    {
        var prefs = Preferences.Default;
        prefs.Set(PrefPushToTalkEnabled, PushToTalkEnabled);
        prefs.Set(PrefPushToTalkKey, PushToTalkKey);
        prefs.Set(PrefPushToTalkSoundEnabled, PushToTalkSoundEnabled);
    }

    private static string? NormalizeDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

    private static string NormalizeResolution(string? resolution)
        => resolution switch
        {
            "3840x2160" or "2560x1440" or "1920x1080" or "1280x720" or "640x480" or "480x360" => resolution,
            _ => "1280x720"
        };

    private static int ClampFps(int fps) => Math.Clamp(fps, 1, 120);

    private static int ClampBitrate(int bitrateKbps) => Math.Clamp(bitrateKbps, 1_000, 20_000);

    private static double ClampVolume(double volume) => Math.Clamp(volume, 0.0, 2.0);

    private static string NormalizeVideoCodec(string? codec)
        => (codec ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "h264" => "h264",
            "vp8" => "vp8",
            "vp9" => "vp9",
            "h265" => "h265",
            _ => "h264"
        };

    private static string NormalizePushToTalkKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "Space" : key.Trim();

    private bool AddRoomParticipant(Guid roomId, Guid userId)
    {
        if (roomId == Guid.Empty || userId == Guid.Empty) return false;

        if (!_roomCallParticipants.TryGetValue(roomId, out var users))
        {
            users = [];
            _roomCallParticipants[roomId] = users;
        }

        return users.Add(userId);
    }

    private bool RemoveRoomParticipant(Guid roomId, Guid userId)
    {
        if (roomId == Guid.Empty || userId == Guid.Empty) return false;
        if (!_roomCallParticipants.TryGetValue(roomId, out var users)) return false;

        var removed = users.Remove(userId);
        if (users.Count == 0)
        {
            _roomCallParticipants.Remove(roomId);
        }

        return removed;
    }

    private void EndCallLocally(Guid roomId, string? reason)
    {
        IsInCall = false;
        ActiveRoomId = null;
        Session = null;
        Participants.Clear();
        IsMuted = false;
        IsCameraOff = false;
        IsScreenSharing = false;
        UseFloatingOverlayInActiveRoom = false;
        Error = reason;
        RemoveRoomParticipant(roomId, _auth.CurrentUser?.Id ?? Guid.Empty);
        NotifyChanged();
    }

    private void NotifyChanged() => OnChange?.Invoke();

    public void Dispose()
    {
        _chat.OnUserJoinedVoiceVideo -= HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo -= HandleParticipantLeft;
        _chat.OnUserKickedFromVoiceVideo -= HandleKickedFromVoiceVideo;
        _chat.OnWebRtcSignalReceived -= HandleWebRtcSignal;
        _chat.OnUserMoved -= HandleUserMoved;
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
    }
}
