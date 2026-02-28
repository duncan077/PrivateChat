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
    private const string PrefVolume = PrefPrefix + "volume";
    private const string PrefPushToTalkEnabled = PrefPrefix + "pushToTalkEnabled";
    private const string PrefPushToTalkKey = PrefPrefix + "pushToTalkKey";

    public bool IsInCall { get; private set; }
    public Guid? ActiveRoomId { get; private set; }
    public JanusSessionInfo? Session { get; private set; }
    public List<Guid> Participants { get; private set; } = [];
    public bool IsMuted { get; set; }
    public bool IsCameraOff { get; set; }
    public bool IsScreenSharing { get; set; }
    public string? Error { get; private set; }

    // Device & settings state
    public List<MediaDeviceInfo> Microphones { get; private set; } = [];
    public List<MediaDeviceInfo> Cameras { get; private set; } = [];
    public string? SelectedMicId { get; set; }
    public string? SelectedCameraId { get; set; }
    public string SelectedResolution { get; set; } = "1280x720";
    public int SelectedFps { get; set; } = 30;
    public int SelectedBitrate { get; set; } = 2000; // kbps
    public double Volume { get; set; } = 1.0;
    public bool ShowSettings { get; set; }

    // Push-to-talk
    public bool PushToTalkEnabled { get; set; }
    public string PushToTalkKey { get; set; } = "Space";

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

    public CallStore(ChatClientService chat, AuthStateProvider auth)
    {
        _chat = chat;
        _auth = auth;
        LoadSettingsPreferences();

        _chat.OnUserJoinedVoiceVideo += HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo += HandleParticipantLeft;
        _chat.OnWebRtcSignalReceived += HandleWebRtcSignal;
    }

    /// <summary>
    /// Called by ActiveCallPanel to initiate a call.
    /// Returns the JanusSessionInfo for the WebRTC JS layer.
    /// </summary>
    public async Task<JanusSessionInfo?> JoinCallAsync(Guid roomId)
    {
        if (IsInCall) return null;

        try
        {
            Error = null;
            Session = await _chat.JoinVoiceVideoAsync(roomId);
            ActiveRoomId = roomId;
            IsInCall = true;
            IsMuted = true;
            IsCameraOff = true;
            IsScreenSharing = false;
            Participants.Clear();
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

        try
        {
            await _chat.LeaveVoiceVideoAsync(ActiveRoomId.Value);
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
            Error = null;
            NotifyChanged();
        }
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

    public void ApplyCallSettings(string? micDeviceId, string? cameraDeviceId, string? resolution, int fps, int bitrateKbps)
    {
        SelectedMicId = NormalizeDeviceId(micDeviceId);
        SelectedCameraId = NormalizeDeviceId(cameraDeviceId);
        SelectedResolution = NormalizeResolution(resolution);
        SelectedFps = ClampFps(fps);
        SelectedBitrate = ClampBitrate(bitrateKbps);
        SaveCallSettingsPreferences();
        NotifyChanged();
    }

    public void SetVolumePreference(double volume)
    {
        Volume = ClampVolume(volume);
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

    public void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
        NotifyChanged();
    }

    private void HandleParticipantJoined(Guid roomId, Guid userId)
    {
        if (roomId != ActiveRoomId || userId == _auth.CurrentUser?.Id) return;
        if (!Participants.Contains(userId))
        {
            Participants.Add(userId);
            NotifyChanged();
        }
    }

    private void HandleParticipantLeft(Guid roomId, Guid userId)
    {
        if (roomId != ActiveRoomId) return;
        Participants.Remove(userId);
        NotifyChanged();
    }

    private void HandleWebRtcSignal(Guid roomId, Guid fromUserId, string signalType, string payload)
    {
        if (roomId != ActiveRoomId) return;
        OnWebRtcSignal?.Invoke(fromUserId, signalType, payload);
    }

    private void LoadSettingsPreferences()
    {
        var prefs = Preferences.Default;

        SelectedMicId = NormalizeDeviceId(prefs.Get(PrefSelectedMicId, string.Empty));
        SelectedCameraId = NormalizeDeviceId(prefs.Get(PrefSelectedCameraId, string.Empty));
        SelectedResolution = NormalizeResolution(prefs.Get(PrefSelectedResolution, "1280x720"));
        SelectedFps = ClampFps(prefs.Get(PrefSelectedFps, 30));
        SelectedBitrate = ClampBitrate(prefs.Get(PrefSelectedBitrate, 2000));
        Volume = ClampVolume(prefs.Get(PrefVolume, 1.0));
        PushToTalkEnabled = prefs.Get(PrefPushToTalkEnabled, false);
        PushToTalkKey = NormalizePushToTalkKey(prefs.Get(PrefPushToTalkKey, "Space"));
    }

    private void SaveCallSettingsPreferences()
    {
        var prefs = Preferences.Default;
        prefs.Set(PrefSelectedMicId, SelectedMicId ?? string.Empty);
        prefs.Set(PrefSelectedCameraId, SelectedCameraId ?? string.Empty);
        prefs.Set(PrefSelectedResolution, SelectedResolution);
        prefs.Set(PrefSelectedFps, SelectedFps);
        prefs.Set(PrefSelectedBitrate, SelectedBitrate);
    }

    private void SaveVolumePreference()
    {
        Preferences.Default.Set(PrefVolume, Volume);
    }

    private void SavePushToTalkPreferences()
    {
        var prefs = Preferences.Default;
        prefs.Set(PrefPushToTalkEnabled, PushToTalkEnabled);
        prefs.Set(PrefPushToTalkKey, PushToTalkKey);
    }

    private static string? NormalizeDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

    private static string NormalizeResolution(string? resolution)
        => resolution switch
        {
            "1920x1080" or "1280x720" or "640x480" or "480x360" => resolution,
            _ => "1280x720"
        };

    private static int ClampFps(int fps) => Math.Clamp(fps, 1, 120);

    private static int ClampBitrate(int bitrateKbps) => Math.Clamp(bitrateKbps, 100, 20_000);

    private static double ClampVolume(double volume) => Math.Clamp(volume, 0.0, 2.0);

    private static string NormalizePushToTalkKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "Space" : key.Trim();

    private void NotifyChanged() => OnChange?.Invoke();

    public void Dispose()
    {
        _chat.OnUserJoinedVoiceVideo -= HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo -= HandleParticipantLeft;
        _chat.OnWebRtcSignalReceived -= HandleWebRtcSignal;
    }
}
