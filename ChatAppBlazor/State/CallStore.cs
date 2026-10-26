using ChatAppBlazor.Services;
using Microsoft.Maui.Storage;
using Shared.Models;
using System.Threading;
#if WINDOWS
using System.Management;
#endif

namespace ChatAppBlazor.State;

public record MediaDeviceInfo(string DeviceId, string Label, string Kind);

public enum CallContextKind
{
    Room = 0,
    DirectMessage = 1
}

/// <summary>
/// Manages the WebRTC call lifecycle: join, leave, participants, media state.
/// Acts as the bridge between ChatClientService hub events and the WebRTC JS layer.
/// </summary>
public partial class CallStore : IDisposable
{
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private readonly IScreenAnnotationOverlay _annotationOverlay;
    private const string PrefPrefix = "call.settings.";
    private const string PrefSelectedMicId = PrefPrefix + "selectedMicId";
    private const string PrefSelectedCameraId = PrefPrefix + "selectedCameraId";
    private const string PrefSelectedSpeakerId = PrefPrefix + "selectedSpeakerId";
    private const string PrefSelectedResolution = PrefPrefix + "selectedResolution";
    private const string PrefSelectedFps = PrefPrefix + "selectedFps";
    private const string PrefSelectedBitrate = PrefPrefix + "selectedBitrate";
    private const string PrefSelectedVideoCodec = PrefPrefix + "selectedVideoCodec";
    private const string PrefNoiseSuppressionEnabled = PrefPrefix + "noiseSuppressionEnabled";
    private const string PrefScreenShareAudioEnabled = PrefPrefix + "screenShareAudioEnabled";
    private const string PrefScreenShareAutoGainEnabled = PrefPrefix + "screenShareAutoGainEnabled";
    private const string PrefGpuAccelerationEnabled = PrefPrefix + "gpuAccelerationEnabled";
    private const string PrefWindowsGpuPreference = PrefPrefix + "windowsGpuPreference";
    private const string PrefForcePlatformHevcEncoderSupport = PrefPrefix + "forcePlatformHevcEncoderSupport";
    private const string PrefEnableWebRtcH265SendFeature = PrefPrefix + "enableWebRtcH265SendFeature";
    private const string PrefZeroCopyVideoCaptureEnabled = PrefPrefix + "zeroCopyVideoCaptureEnabled";
    private const string PrefLegacyForcePlatformHevcDecoderSupport = PrefPrefix + "forcePlatformHevcDecoderSupport";
    private const string PrefVolume = PrefPrefix + "volume";
    private const string PrefStreamVolume = PrefPrefix + "streamVolume";
    private const string PrefPushToTalkEnabled = PrefPrefix + "pushToTalkEnabled";
    private const string PrefPushToTalkKey = PrefPrefix + "pushToTalkKey";
    private const string PrefPushToTalkSoundEnabled = PrefPrefix + "pushToTalkSoundEnabled";
    private const string PrefNotificationsEnabled = PrefPrefix + "notificationsEnabled";
    private const string PrefH264ProfileOverride = PrefPrefix + "h264ProfileOverride";
    private const string PrefVideoContentHint = PrefPrefix + "videoContentHint";
    private const string PrefVoiceGateEnabled = PrefPrefix + "voiceGateEnabled";
    private const string PrefVoiceGateAuto = PrefPrefix + "voiceGateAuto";
    private const string PrefVoiceGateThreshold = PrefPrefix + "voiceGateThreshold";
    private const string PrefHideLocalPreview = PrefPrefix + "hideLocalPreview";

    public bool IsInCall { get; private set; }
    public Guid? ActiveRoomId { get; private set; }
    public CallContextKind ActiveCallKind { get; private set; } = CallContextKind.Room;
    public Guid? ActiveDirectPeerUserId { get; private set; }
    public JanusSessionInfo? Session { get; private set; }
    public List<Guid> Participants { get; private set; } = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _roomCallParticipants = [];
    public bool IsMuted { get; set; }
    public bool IsCameraOff { get; set; }
    public bool IsScreenSharing { get; set; }
    public bool UseFloatingOverlayInActiveRoom { get; private set; }

    /// <summary>
    /// View filter: hide the tiles of participants with no video track. Audio is
    /// unaffected. Session-only on purpose — a fresh call starts showing everyone.
    /// </summary>
    public bool HideNonVideoParticipants { get; private set; }

    public string? Error { get; private set; }

    // Device & settings state
    public List<MediaDeviceInfo> Microphones { get; private set; } = [];
    public List<MediaDeviceInfo> Cameras { get; private set; } = [];

    /// <summary>
    /// Audio output devices. Only Chromium exposes these and only once a media
    /// permission has been granted, so this can legitimately be empty.
    /// </summary>
    public List<MediaDeviceInfo> Speakers { get; private set; } = [];
    public string? SelectedMicId { get; set; }
    public string? SelectedCameraId { get; set; }
    public string? SelectedSpeakerId { get; set; }
    public string SelectedResolution { get; set; } = "1280x720";
    public int SelectedFps { get; set; } = 30;
    public int SelectedBitrate { get; set; } = 2000; // kbps
    public string SelectedVideoCodec { get; set; } = "h264";
    public string SelectedVideoContentHint { get; set; } = "motion";
    public bool ScreenShareAudioEnabled { get; set; } = true;
    public bool ScreenShareAutoGainEnabled { get; set; } = true;
    public bool HideLocalPreview { get; set; }
    public bool GpuAccelerationEnabled { get; set; } = true;
    public string WindowsGpuPreference { get; set; } = "high-performance";
    public bool ForcePlatformHevcEncoderSupport { get; set; } = true;
    public bool EnableWebRtcH265SendFeature { get; set; } = true;
    public bool ZeroCopyVideoCaptureEnabled { get; set; } = true;
    public bool NoiseSuppressionEnabled { get; set; } = true;
    public bool VoiceGateEnabled { get; set; }
    public bool VoiceGateAuto { get; set; } = true;
    public int VoiceGateThreshold { get; set; } = 30; // 0-100, default 30
    public double Volume { get; set; } = 1.0; // Mic/output-to-peers volume
    public double StreamVolume { get; set; } = 1.0; // Incoming stream playback volume
    public bool ShowSettings { get; set; }

    // Push-to-talk
    public bool PushToTalkEnabled { get; set; }
    public string PushToTalkKey { get; set; } = "Space";
    public bool PushToTalkSoundEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;

    // Debug: pin a specific H.264 profile-level-id + packetization-mode
    // Format: "profile-level-id:packetization-mode" e.g. "42e01f:1", or null/empty for auto.
    public string? H264ProfileOverride { get; set; }

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
    private CancellationTokenSource? _pendingSelfRemovalValidationCts;
    private static readonly int[] SelfRemovalValidationDelaysMs = [1200, 2200, 3200];

    public CallStore(ChatClientService chat, AuthStateProvider auth, IScreenAnnotationOverlay annotationOverlay)
    {
        _chat = chat;
        _auth = auth;
        _annotationOverlay = annotationOverlay;
        LoadSettingsPreferences();

        _chat.OnUserJoinedVoiceVideo += HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo += HandleParticipantLeft;
        _chat.OnUserKickedFromVoiceVideo += HandleKickedFromVoiceVideo;
        _chat.OnWebRtcSignalReceived += HandleWebRtcSignal;
        _chat.OnUserMoved += HandleUserMoved;
        _chat.OnRoomDeleted += HandleRoomDeleted;
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;
        _chat.OnScreenAnnotationStateChanged += HandleScreenAnnotationStateChanged;
        _chat.OnScreenAnnotationReceived += HandleScreenAnnotationReceived;
    }

    /// <summary>
    /// Called by ActiveCallPanel to initiate a call.
    /// Returns the JanusSessionInfo for the WebRTC JS layer.
    /// </summary>
    public async Task<JanusSessionInfo?> JoinCallAsync(Guid roomId)
    {
        if (roomId == Guid.Empty) return null;
        CancelPendingSelfRemovalValidation();

        try
        {
            if (IsInCall)
            {
                if (ActiveCallKind == CallContextKind.Room && ActiveRoomId == roomId && Session != null)
                {
                    return Session;
                }

                if (ActiveRoomId != null)
                {
                    try
                    {
                        if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
                        {
                            await _chat.LeavePrivateVoiceVideoAsync(ActiveDirectPeerUserId.Value);
                        }
                        else
                        {
                            await _chat.LeaveVoiceVideoAsync(ActiveRoomId.Value);
                        }
                    }
                    catch
                    {
                        // Continue switching call state even if server-side leave is transiently failing.
                    }

                    RemoveRoomParticipant(ActiveRoomId.Value, _auth.CurrentUser?.Id ?? Guid.Empty);
                }

                Session = null;
                ActiveRoomId = null;
                ActiveCallKind = CallContextKind.Room;
                ActiveDirectPeerUserId = null;
                IsInCall = false;
                Participants.Clear();
                IsScreenSharing = false;
                UseFloatingOverlayInActiveRoom = false;
            }

            Error = null;
            Session = await _chat.JoinVoiceVideoAsync(roomId);
            ActiveRoomId = roomId;
            ActiveCallKind = CallContextKind.Room;
            ActiveDirectPeerUserId = null;
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

    public async Task<JanusSessionInfo?> JoinPrivateCallAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _auth.CurrentUser?.Id) return null;
        CancelPendingSelfRemovalValidation();

        try
        {
            if (IsInCall)
            {
                if (ActiveCallKind == CallContextKind.DirectMessage
                    && ActiveDirectPeerUserId == peerUserId
                    && Session != null)
                {
                    return Session;
                }

                if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
                {
                    try
                    {
                        await _chat.LeavePrivateVoiceVideoAsync(ActiveDirectPeerUserId.Value);
                    }
                    catch
                    {
                        // Continue switching call state even if server-side leave is transiently failing.
                    }
                }
                else if (ActiveRoomId != null)
                {
                    try
                    {
                        await _chat.LeaveVoiceVideoAsync(ActiveRoomId.Value);
                    }
                    catch
                    {
                        // Continue switching call state even if server-side leave is transiently failing.
                    }
                }

                if (ActiveRoomId != null)
                {
                    RemoveRoomParticipant(ActiveRoomId.Value, _auth.CurrentUser?.Id ?? Guid.Empty);
                }

                Session = null;
                ActiveRoomId = null;
                ActiveCallKind = CallContextKind.Room;
                ActiveDirectPeerUserId = null;
                IsInCall = false;
                Participants.Clear();
                IsScreenSharing = false;
                UseFloatingOverlayInActiveRoom = false;
            }

            Error = null;
            Session = await _chat.JoinPrivateVoiceVideoAsync(peerUserId);
            ActiveRoomId = Session.RoomId;
            ActiveCallKind = CallContextKind.DirectMessage;
            ActiveDirectPeerUserId = peerUserId;
            IsInCall = true;
            IsMuted = true;
            IsCameraOff = true;
            IsScreenSharing = false;
            UseFloatingOverlayInActiveRoom = false;
            Participants.Clear();
            AddRoomParticipant(Session.RoomId, _auth.CurrentUser?.Id ?? Guid.Empty);
            await RefreshPrivateParticipantsAsync(peerUserId);
            NotifyChanged();
            return Session;
        }
        catch (Exception ex)
        {
            Error = $"Failed to join private call: {ex.Message}";
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
            if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
            {
                await _chat.LeavePrivateVoiceVideoAsync(ActiveDirectPeerUserId.Value);
            }
            else
            {
                await _chat.LeaveVoiceVideoAsync(roomId);
            }
        }
        catch { }
        finally
        {
            CancelPendingSelfRemovalValidation();
            IsInCall = false;
            ActiveRoomId = null;
            ActiveCallKind = CallContextKind.Room;
            ActiveDirectPeerUserId = null;
            Session = null;
            Participants.Clear();
            IsMuted = false;
            IsCameraOff = false;
            IsScreenSharing = false;
            UseFloatingOverlayInActiveRoom = false;
            Error = null;
            ResetAnnotationState();
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
            if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
            {
                Session = await _chat.JoinPrivateVoiceVideoAsync(ActiveDirectPeerUserId.Value);
            }
            else
            {
                Session = await _chat.JoinVoiceVideoAsync(roomId);
            }
            ActiveRoomId = Session.RoomId;
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
        var wasScreenSharing = IsScreenSharing;
        IsScreenSharing = isScreenSharing;
        NotifyChanged();

        if (wasScreenSharing && !isScreenSharing)
        {
            // Tear down the annotation overlay and release the session for everyone.
            _ = ClearLocalScreenShareTargetAsync();
        }
    }

    /// <summary>
    /// True once the publisher PeerConnection is negotiated and a local track can
    /// actually reach the other participants.
    ///
    /// Distinct from <see cref="IsInCall"/>, which flips as soon as the hub join
    /// returns - seconds earlier. The camera and screen-share controls key off this
    /// one: acting on them in between turned the capture on locally and published
    /// nothing, which looked like working video to the person who pressed it.
    /// </summary>
    public bool IsPublisherReady { get; private set; }

    public void UpdatePublisherReady(bool isReady)
    {
        if (IsPublisherReady == isReady)
        {
            return;
        }

        IsPublisherReady = isReady;
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
        Speakers = devices.Where(d => d.Kind == "audiooutput").ToList();
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
        string? speakerDeviceId,
        string? resolution,
        int fps,
        int bitrateKbps,
        string? videoCodec,
        bool noiseSuppressionEnabled,
        bool screenShareAudioEnabled,
        bool screenShareAutoGainEnabled,
        bool hideLocalPreview,
        bool gpuAccelerationEnabled,
        string? windowsGpuPreference,
        bool forcePlatformHevcEncoderSupport,
        bool enableWebRtcH265SendFeature,
        bool zeroCopyVideoCaptureEnabled,
        bool voiceGateEnabled,
        bool voiceGateAuto,
        int voiceGateThreshold,
        string? videoContentHint,
        string? h264ProfileOverride = null)
    {
        SelectedMicId = NormalizeDeviceId(micDeviceId);
        SelectedCameraId = NormalizeDeviceId(cameraDeviceId);
        SelectedSpeakerId = NormalizeDeviceId(speakerDeviceId);
        SelectedResolution = NormalizeResolution(resolution);
        SelectedFps = ClampFps(fps);
        SelectedBitrate = ClampBitrate(bitrateKbps);
        SelectedVideoCodec = NormalizeVideoCodec(videoCodec);
        SelectedVideoContentHint = NormalizeVideoContentHint(videoContentHint);
        NoiseSuppressionEnabled = noiseSuppressionEnabled;
        VoiceGateEnabled = voiceGateEnabled;
        VoiceGateAuto = voiceGateAuto;
        VoiceGateThreshold = Math.Clamp(voiceGateThreshold, 0, 100);
        ScreenShareAudioEnabled = screenShareAudioEnabled;
        ScreenShareAutoGainEnabled = screenShareAutoGainEnabled;
        HideLocalPreview = hideLocalPreview;
        GpuAccelerationEnabled = gpuAccelerationEnabled;
        WindowsGpuPreference = NormalizeWindowsGpuPreference(windowsGpuPreference);
        ForcePlatformHevcEncoderSupport = forcePlatformHevcEncoderSupport;
        EnableWebRtcH265SendFeature = enableWebRtcH265SendFeature;
        ZeroCopyVideoCaptureEnabled = zeroCopyVideoCaptureEnabled;
        H264ProfileOverride = string.IsNullOrWhiteSpace(h264ProfileOverride) ? null : h264ProfileOverride.Trim();
        SaveCallSettingsPreferences();
        NotifyChanged();
    }

    public void ToggleHideNonVideoParticipants()
    {
        HideNonVideoParticipants = !HideNonVideoParticipants;
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

    public void SetNotificationsEnabled(bool enabled)
    {
        NotificationsEnabled = enabled;
        SaveNotificationsPreference();
        NotifyChanged();
    }

    public void SetHideLocalPreviewPreference(bool hideLocalPreview)
    {
        if (HideLocalPreview == hideLocalPreview)
        {
            return;
        }

        HideLocalPreview = hideLocalPreview;
        SaveCallSettingsPreferences();
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
                    // Janus/room participant lists can be eventually consistent while tracks are
                    // renegotiated (screen-share start/stop, codec update, transient publisher swap).
                    // Do not trigger reconnect/teardown from this signal alone.
                    Console.WriteLine($"[CallStore] Self missing from participant refresh for room {roomId}; ignoring transient presence mismatch.");
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

    public async Task RefreshPrivateParticipantsAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || ActiveRoomId == null) return;

        try
        {
            var participants = await _chat.GetPrivateVoiceVideoParticipantsAsync(peerUserId);
            var contextId = ActiveRoomId.Value;
            _roomCallParticipants[contextId] = participants
                .Where(id => id != Guid.Empty)
                .ToHashSet();

            if (IsInCall && ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId == peerUserId)
            {
                var myId = _auth.CurrentUser?.Id;
                Participants = participants
                    .Where(id => id != Guid.Empty && id != myId)
                    .Distinct()
                    .ToList();
            }

            NotifyChanged();
        }
        catch
        {
            // Ignore transient fetch failures.
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

    public void ApplyRoomParticipantSnapshot(IEnumerable<RoomCallParticipantsInfo> snapshots)
    {
        var changed = false;

        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || snapshot.RoomId == Guid.Empty)
            {
                continue;
            }

            var nextParticipants = snapshot.ParticipantUserIds
                .Where(id => id != Guid.Empty)
                .ToHashSet();

            if (IsInCall
                && ActiveCallKind == CallContextKind.Room
                && ActiveRoomId == snapshot.RoomId
                && _auth.CurrentUser?.Id is Guid selfId
                && selfId != Guid.Empty)
            {
                // Keep local presence stable if server snapshot is briefly stale.
                nextParticipants.Add(selfId);
            }

            if (nextParticipants.Count == 0)
            {
                changed = _roomCallParticipants.Remove(snapshot.RoomId) || changed;
            }
            else if (!_roomCallParticipants.TryGetValue(snapshot.RoomId, out var currentParticipants)
                     || !currentParticipants.SetEquals(nextParticipants))
            {
                _roomCallParticipants[snapshot.RoomId] = nextParticipants;
                changed = true;
            }

            if (IsInCall && ActiveRoomId == snapshot.RoomId)
            {
                var myId = _auth.CurrentUser?.Id;
                var visibleParticipants = nextParticipants
                    .Where(id => id != Guid.Empty && id != myId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();

                var currentVisibleParticipants = Participants
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();

                if (!visibleParticipants.SequenceEqual(currentVisibleParticipants))
                {
                    Participants = visibleParticipants.ToList();
                    changed = true;
                }
            }
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    public bool IsActiveDirectCallWith(Guid userId)
    {
        if (!IsInCall || userId == Guid.Empty)
        {
            return false;
        }

        return ActiveCallKind == CallContextKind.DirectMessage
            && ActiveDirectPeerUserId == userId;
    }

    public bool IsUserConnectedInRoom(Guid roomId, Guid userId)
    {
        if (roomId == Guid.Empty || userId == Guid.Empty) return false;
        return _roomCallParticipants.TryGetValue(roomId, out var users) && users.Contains(userId);
    }

    private void HandleParticipantJoined(Guid roomId, Guid userId)
    {
        var changed = AddRoomParticipant(roomId, userId);
        if (roomId == ActiveRoomId && userId == _auth.CurrentUser?.Id)
        {
            CancelPendingSelfRemovalValidation();
        }

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
            // Ignore self-left events that can be emitted while Janus swaps publisher handles
            // during renegotiation/reconfiguration. Reconnection should only be driven by
            // explicit Janus-session loss or kick/move events.
            Console.WriteLine($"[CallStore] Received self left event for room {roomId}; ignoring transient self-leave.");
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

    public async Task SendWebRtcSignalAsync(
        Guid targetUserId,
        string signalType,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!IsInCall || ActiveRoomId == null || targetUserId == Guid.Empty || string.IsNullOrWhiteSpace(signalType))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
        {
            await _chat.SendPrivateWebRtcSignalAsync(ActiveDirectPeerUserId.Value, targetUserId, signalType, payload);
            return;
        }

        await _chat.SendWebRtcSignalAsync(ActiveRoomId.Value, targetUserId, signalType, payload);
    }

    private void HandleKickedFromVoiceVideo(Guid roomId, Guid userId, string reason)
    {
        if (roomId != ActiveRoomId || userId != _auth.CurrentUser?.Id)
        {
            return;
        }

        // Move operations emit a synthetic kicked event for the source room.
        // Ignore it so auto-rejoin-to-target can complete without being torn down.
        if (_isAutoRejoiningAfterMove
            || (!string.IsNullOrWhiteSpace(reason)
                && reason.Contains("moved to another room", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        EndCallLocally(
            roomId,
            string.IsNullOrWhiteSpace(reason) ? "You were removed from this call." : reason,
            "kicked-event");
    }

    /// <summary>
    /// A deleted room takes its call with it. The server has already dropped every
    /// participant's presence, annotation session and media session by the time this
    /// arrives, so staying "in" the call would leave the panel up over something that
    /// no longer exists anywhere - with the media dying underneath it and no explanation.
    /// </summary>
    private void HandleRoomDeleted(Guid roomId)
    {
        if (!IsInCall || ActiveCallKind != CallContextKind.Room || ActiveRoomId != roomId)
        {
            return;
        }

        EndCallLocally(roomId, "This room was deleted.", "room-deleted");
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

            // Move events can arrive before room-subscription state is updated on this client.
            // Ensure the target room is joined before trying to auto-join voice/video there.
            try
            {
                await _chat.JoinRoomAsync(targetRoomId);
            }
            catch
            {
                // Best effort: JoinCallAsync below will still report a meaningful error if it cannot proceed.
            }

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
        if (!connected || !IsInCall) return;

        try
        {
            if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
            {
                await _chat.EnsurePrivateVoiceVideoPresenceAsync(ActiveDirectPeerUserId.Value);
                await RefreshPrivateParticipantsAsync(ActiveDirectPeerUserId.Value);
                await ResyncAnnotationsAfterReconnectAsync();
                return;
            }

            if (ActiveRoomId == null) return;
            var roomId = ActiveRoomId.Value;
            await _chat.JoinRoomAsync(roomId);
            await _chat.EnsureVoiceVideoPresenceAsync(roomId);
            await RefreshRoomParticipantsAsync(roomId);
            await ResyncAnnotationsAfterReconnectAsync();
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
        SelectedSpeakerId = NormalizeDeviceId(prefs.Get(PrefSelectedSpeakerId, string.Empty));
        SelectedResolution = NormalizeResolution(prefs.Get(PrefSelectedResolution, "1280x720"));
        SelectedFps = ClampFps(prefs.Get(PrefSelectedFps, 30));
        SelectedBitrate = ClampBitrate(prefs.Get(PrefSelectedBitrate, 2000));
        SelectedVideoCodec = NormalizeVideoCodec(prefs.Get(PrefSelectedVideoCodec, "h264"));
        SelectedVideoContentHint = NormalizeVideoContentHint(prefs.Get(PrefVideoContentHint, "motion"));
        NoiseSuppressionEnabled = prefs.Get(PrefNoiseSuppressionEnabled, true);
        VoiceGateEnabled = prefs.Get(PrefVoiceGateEnabled, false);
        VoiceGateAuto = prefs.Get(PrefVoiceGateAuto, true);
        VoiceGateThreshold = Math.Clamp(prefs.Get(PrefVoiceGateThreshold, 30), 0, 100);
        ScreenShareAudioEnabled = prefs.Get(PrefScreenShareAudioEnabled, true);
        ScreenShareAutoGainEnabled = prefs.Get(PrefScreenShareAutoGainEnabled, true);
        HideLocalPreview = prefs.Get(PrefHideLocalPreview, false);
        GpuAccelerationEnabled = prefs.Get(PrefGpuAccelerationEnabled, true);
        WindowsGpuPreference = NormalizeWindowsGpuPreference(prefs.Get(PrefWindowsGpuPreference, "high-performance"));
        ForcePlatformHevcEncoderSupport = prefs.Get(
            PrefForcePlatformHevcEncoderSupport,
            prefs.Get(PrefLegacyForcePlatformHevcDecoderSupport, true));
        EnableWebRtcH265SendFeature = prefs.Get(PrefEnableWebRtcH265SendFeature, true);
        ZeroCopyVideoCaptureEnabled = prefs.Get(PrefZeroCopyVideoCaptureEnabled, true);
        Volume = ClampVolume(prefs.Get(PrefVolume, 1.0));
        StreamVolume = ClampVolume(prefs.Get(PrefStreamVolume, 1.0));
        PushToTalkEnabled = prefs.Get(PrefPushToTalkEnabled, false);
        PushToTalkKey = NormalizePushToTalkKey(prefs.Get(PrefPushToTalkKey, "Space"));
        PushToTalkSoundEnabled = prefs.Get(PrefPushToTalkSoundEnabled, true);
        NotificationsEnabled = prefs.Get(PrefNotificationsEnabled, true);
        var h264Override = prefs.Get(PrefH264ProfileOverride, string.Empty);
        H264ProfileOverride = string.IsNullOrWhiteSpace(h264Override) ? null : h264Override;
    }

    private void SaveCallSettingsPreferences()
    {
        var prefs = Preferences.Default;
        prefs.Set(PrefSelectedMicId, SelectedMicId ?? string.Empty);
        prefs.Set(PrefSelectedCameraId, SelectedCameraId ?? string.Empty);
        prefs.Set(PrefSelectedSpeakerId, SelectedSpeakerId ?? string.Empty);
        prefs.Set(PrefSelectedResolution, SelectedResolution);
        prefs.Set(PrefSelectedFps, SelectedFps);
        prefs.Set(PrefSelectedBitrate, SelectedBitrate);
        prefs.Set(PrefSelectedVideoCodec, SelectedVideoCodec);
        prefs.Set(PrefVideoContentHint, SelectedVideoContentHint);
        prefs.Set(PrefNoiseSuppressionEnabled, NoiseSuppressionEnabled);
        prefs.Set(PrefVoiceGateEnabled, VoiceGateEnabled);
        prefs.Set(PrefVoiceGateAuto, VoiceGateAuto);
        prefs.Set(PrefVoiceGateThreshold, VoiceGateThreshold);
        prefs.Set(PrefScreenShareAudioEnabled, ScreenShareAudioEnabled);
        prefs.Set(PrefScreenShareAutoGainEnabled, ScreenShareAutoGainEnabled);
        prefs.Set(PrefHideLocalPreview, HideLocalPreview);
        prefs.Set(PrefGpuAccelerationEnabled, GpuAccelerationEnabled);
        prefs.Set(PrefWindowsGpuPreference, WindowsGpuPreference);
        prefs.Set(PrefForcePlatformHevcEncoderSupport, ForcePlatformHevcEncoderSupport);
        prefs.Set(PrefEnableWebRtcH265SendFeature, EnableWebRtcH265SendFeature);
        prefs.Set(PrefZeroCopyVideoCaptureEnabled, ZeroCopyVideoCaptureEnabled);
        prefs.Set(PrefH264ProfileOverride, H264ProfileOverride ?? string.Empty);
    }

    public static string BuildWindowsWebView2Arguments(IPreferences? preferences = null)
    {
        var prefs = preferences ?? Preferences.Default;
        var enableGpu = prefs.Get(PrefGpuAccelerationEnabled, true);
        var windowsGpuPreference = NormalizeWindowsGpuPreference(prefs.Get(PrefWindowsGpuPreference, "high-performance"));
        var hasMultipleWindowsGpus = HasMultipleWindowsHardwareGpus();
        var enableHevcEncoder =  true;
        var enableWebRtcH265Send = enableHevcEncoder;
        var enableZeroCopyVideoCapture = prefs.Get(PrefZeroCopyVideoCaptureEnabled, true);

        // Deliberately NOT conditioned on the selected codec. Requesting the HEVC gates
        // only while H265 was already chosen was circular: the picker can only offer
        // H265 once Chromium advertises it, and Chromium only advertises it once these
        // flags are set - so any preference that had drifted to another codec could
        // never get back the flags that would make H265 selectable again. The two
        // switches read above stay the way to turn HEVC off; the codec choice is not.
        // (AV1 is already unconditional, further down.)

        var disabledFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WebRtcHideLocalIpsWithMdns",
            "CalculateNativeWinOcclusion"
        };
        var enabledFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var args = new List<string>
        {
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
            if (enableZeroCopyVideoCapture)
            {
                args.Add("--enable-zero-copy");
            }
            else
            {
                args.Add("--disable-zero-copy");
            }
            args.Add("--disable-software-rasterizer");
            args.Add("--enable-direct3d-present-monitors");

            if (!hasMultipleWindowsGpus || windowsGpuPreference == "high-performance")
            {
                args.Add("--force-high-performance-gpu");
            }
            else if (windowsGpuPreference == "power-saving")
            {
                args.Add("--force_low_power_gpu");
            }

            // AV1 hardware-encode bridge and software encoder suppression.
            enabledFeatures.Add("PlatformAV1EncoderSupport");
            disabledFeatures.Add("SvtAv1VideoEncoder");
        }
        else
        {
            args.Add("--disable-gpu");
            args.Add("--disable-gpu-compositing");
        }

        // Only expose HEVC send path when GPU acceleration is enabled - there is no
        // software HEVC encoder worth using for realtime.
        if (enableGpu && enableHevcEncoder)
        {
            enabledFeatures.Add("PlatformHEVCEncoderSupport");
            enabledFeatures.Add("PlatformHEVCDecoderSupport");
            if (enableWebRtcH265Send)
            {
                enabledFeatures.Add("WebRtcAllowH265Send");
                enabledFeatures.Add("WebRtcAllowH265Receive");
            }
        }

        if (disabledFeatures.Count > 0)
        {
            args.Add($"--disable-features={string.Join(",", disabledFeatures)}");
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

    private void SaveNotificationsPreference()
    {
        Preferences.Default.Set(PrefNotificationsEnabled, NotificationsEnabled);
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
            "hvc1" => "hvc1",
            "av1" => "av1",
            _ => "h264"
        };

    private static string NormalizeVideoContentHint(string? hint)
        => (hint ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "detail" => "detail",
            _ => "motion"
        };

    private static string NormalizePushToTalkKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "Space" : key.Trim();

    public static IReadOnlyList<string> GetWindowsHardwareGpuNames()
    {
#if WINDOWS
        try
        {
            var names = new List<string>();
            var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var controllers = searcher.Get();

            foreach (ManagementObject controller in controllers)
            {
                var rawName = controller["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var name = rawName.Trim();
                if (name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (uniqueNames.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch
        {
            return [];
        }
#else
        return [];
#endif
    }

    public static bool HasMultipleWindowsHardwareGpus()
        => GetWindowsHardwareGpuNames().Count > 1;

    private static string NormalizeWindowsGpuPreference(string? preference)
        => (preference ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "power-saving" => "power-saving",
            _ => "high-performance"
        };

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

    private void EndCallLocally(Guid roomId, string? reason, string source = "unknown")
    {
        CancelPendingSelfRemovalValidation();
        Console.WriteLine($"[CallStore] Ending call locally (room={roomId}, source={source}, reason={reason ?? "none"}).");
        IsInCall = false;
        ActiveRoomId = null;
        ActiveCallKind = CallContextKind.Room;
        ActiveDirectPeerUserId = null;
        Session = null;
        Participants.Clear();
        IsMuted = false;
        IsCameraOff = false;
        IsScreenSharing = false;
        UseFloatingOverlayInActiveRoom = false;
        Error = reason;
        // LeaveCallAsync does this on the way out; the forced paths through here - kick,
        // room deleted, self-removal validation - never did, so the annotation toolbar
        // and the desktop overlay outlived the call that granted them.
        ResetAnnotationState();
        RemoveRoomParticipant(roomId, _auth.CurrentUser?.Id ?? Guid.Empty);
        NotifyChanged();
    }

    private void NotifyChanged() => OnChange?.Invoke();

    private void CancelPendingSelfRemovalValidation()
    {
        if (_pendingSelfRemovalValidationCts == null) return;

        try
        {
            _pendingSelfRemovalValidationCts.Cancel();
        }
        catch { }

        _pendingSelfRemovalValidationCts.Dispose();
        _pendingSelfRemovalValidationCts = null;
    }

    private void ScheduleSelfRemovalValidation(Guid roomId, string source)
    {
        if (!IsInCall || ActiveRoomId != roomId) return;
        var myId = _auth.CurrentUser?.Id;
        if (myId == null || myId == Guid.Empty)
        {
            EndCallLocally(roomId, "You were removed from this call.", $"validate-{source}-no-self");
            return;
        }

        CancelPendingSelfRemovalValidation();
        var cts = new CancellationTokenSource();
        _pendingSelfRemovalValidationCts = cts;
        _ = ValidateSelfStillInCallAsync(roomId, myId.Value, source, cts);
    }

    private async Task ValidateSelfStillInCallAsync(Guid roomId, Guid myId, string source, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var hadSuccessfulPresenceCheck = false;
        Console.WriteLine($"[CallStore] Starting self-removal validation (room={roomId}, source={source}).");

        try
        {
            for (var i = 0; i < SelfRemovalValidationDelaysMs.Length; i++)
            {
                try
                {
                    await Task.Delay(SelfRemovalValidationDelaysMs[i], token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested || !IsInCall || ActiveRoomId != roomId)
                {
                    return;
                }

                try
                {
                    Guid[] participants;
                    if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
                    {
                        participants = await _chat.GetPrivateVoiceVideoParticipantsAsync(ActiveDirectPeerUserId.Value);
                    }
                    else
                    {
                        participants = await _chat.GetVoiceVideoParticipantsAsync(roomId);
                    }
                    hadSuccessfulPresenceCheck = true;
                    if (token.IsCancellationRequested || !IsInCall || ActiveRoomId != roomId)
                    {
                        return;
                    }

                    if (participants.Contains(myId))
                    {
                        _roomCallParticipants[roomId] = participants
                            .Where(id => id != Guid.Empty)
                            .ToHashSet();
                        Participants = participants
                            .Where(id => id != Guid.Empty && id != myId)
                            .Distinct()
                            .ToList();
                        Error = null;
                        Console.WriteLine($"[CallStore] Self-removal validation cleared (room={roomId}, source={source}, attempt={i + 1}).");
                        NotifyChanged();
                        return;
                    }

                    Console.WriteLine($"[CallStore] Self still absent (room={roomId}, source={source}, attempt={i + 1}/{SelfRemovalValidationDelaysMs.Length}).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallStore] Self-removal validation check failed (room={roomId}, source={source}, attempt={i + 1}): {ex.Message}");
                }
            }

            if (!token.IsCancellationRequested && IsInCall && ActiveRoomId == roomId)
            {
                if (!hadSuccessfulPresenceCheck)
                {
                    Console.WriteLine($"[CallStore] Self-removal validation ended with no successful presence checks (room={roomId}, source={source}); keeping call active.");
                    return;
                }

                try
                {
                    Console.WriteLine($"[CallStore] Self absent after validation (room={roomId}, source={source}); attempting soft rejoin before local teardown.");
                    if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
                    {
                        Session = await _chat.JoinPrivateVoiceVideoAsync(ActiveDirectPeerUserId.Value);
                    }
                    else
                    {
                        Session = await _chat.JoinVoiceVideoAsync(roomId);
                    }
                    ActiveRoomId = Session.RoomId;
                    Console.WriteLine($"[CallStore] Soft rejoin succeeded after self-removal validation (session={Session.JanusSessionId}).");
                    OnJanusReconnect?.Invoke(Session, roomId);
                    NotifyChanged();
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallStore] Soft rejoin failed after self-removal validation (room={roomId}, source={source}): {ex.Message}");
                }

                EndCallLocally(roomId, "You were removed from this call.", $"validated-{source}");
            }
        }
        finally
        {
            if (ReferenceEquals(_pendingSelfRemovalValidationCts, cts))
            {
                _pendingSelfRemovalValidationCts = null;
            }

            cts.Dispose();
        }
    }

    public void Dispose()
    {
        CancelPendingSelfRemovalValidation();
        _chat.OnUserJoinedVoiceVideo -= HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo -= HandleParticipantLeft;
        _chat.OnUserKickedFromVoiceVideo -= HandleKickedFromVoiceVideo;
        _chat.OnWebRtcSignalReceived -= HandleWebRtcSignal;
        _chat.OnUserMoved -= HandleUserMoved;
        _chat.OnRoomDeleted -= HandleRoomDeleted;
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
        _chat.OnScreenAnnotationStateChanged -= HandleScreenAnnotationStateChanged;
        _chat.OnScreenAnnotationReceived -= HandleScreenAnnotationReceived;
        ResetAnnotationState();
    }
}
