using PrivateChatApp.Services;
using Shared.Models;

namespace PrivateChatApp.State;

/// <summary>
/// Manages the WebRTC call lifecycle: join, leave, participants, media state.
/// Acts as the bridge between ChatClientService hub events and the WebRTC JS layer.
/// </summary>
public class CallStore : IDisposable
{
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

    public bool IsInCall { get; private set; }
    public Guid? ActiveRoomId { get; private set; }
    public JanusSessionInfo? Session { get; private set; }
    public List<Guid> Participants { get; private set; } = [];
    public bool IsMuted { get; set; }
    public bool IsCameraOff { get; set; }
    public bool IsScreenSharing { get; set; }
    public string? Error { get; private set; }

    public event Action? OnChange;

    public CallStore(ChatClientService chat, AuthStateProvider auth)
    {
        _chat = chat;
        _auth = auth;

        _chat.OnUserJoinedVoiceVideo += HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo += HandleParticipantLeft;
        _chat.OnWebRtcSignalReceived += HandleWebRtcSignal;
    }

    /// <summary>
    /// Called by VoiceVideo.razor to initiate a call.
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
            IsMuted = false;
            IsCameraOff = false;
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

    /// <summary>
    /// Fired when a WebRTC signal is received from another user via the gRPC hub.
    /// The VoiceVideo page subscribes to this to forward signals to the JS layer.
    /// </summary>
    public event Action<Guid, string, string>? OnWebRtcSignal;

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

    private void NotifyChanged() => OnChange?.Invoke();

    public void Dispose()
    {
        _chat.OnUserJoinedVoiceVideo -= HandleParticipantJoined;
        _chat.OnUserLeftVoiceVideo -= HandleParticipantLeft;
        _chat.OnWebRtcSignalReceived -= HandleWebRtcSignal;
    }
}
