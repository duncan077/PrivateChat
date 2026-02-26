using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Controls;
using ChatApp.Services;
using Shared.Models;

namespace ChatApp.ViewModels;

public partial class VoiceVideoViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

    private WebView2Host? _webView;
    private JanusSessionInfo? _sessionInfo;
    private bool _webViewNavigated;
    private bool _webViewReady;

    public ObservableCollection<ChatRoomInfo> Rooms { get; } = [];
    public ObservableCollection<Guid> Participants { get; } = [];
    public ObservableCollection<MediaDeviceInfo> Microphones { get; } = [];
    public ObservableCollection<MediaDeviceInfo> Cameras { get; } = [];

    [ObservableProperty] private ChatRoomInfo? _selectedRoom;
    [ObservableProperty] private bool _isInCall;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isCameraOff;
    [ObservableProperty] private bool _isScreenSharing;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private MediaDeviceInfo? _selectedMicrophone;
    [ObservableProperty] private MediaDeviceInfo? _selectedCamera;
    [ObservableProperty] private string _selectedResolution = "1280x720";
    [ObservableProperty] private string _selectedFps = "30";
    [ObservableProperty] private bool _showSettings;

    public string[] AvailableResolutions { get; } =
        ["320x240", "640x480", "1280x720", "1920x1080"];

    public string[] AvailableFps { get; } =
        ["15", "24", "30", "60"];

    public VoiceVideoViewModel(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;
        _chat.OnUserJoinedVoiceVideo += OnUserJoined;
        _chat.OnUserLeftVoiceVideo += OnUserLeft;
        _chat.OnWebRtcSignalReceived += OnWebRtcSignal;
    }

    public void AttachWebView(WebView2Host webView)
    {
        if (_webView == webView) return;

        _webView = webView;
        _webViewNavigated = false;
        _webViewReady = false;

        _webView.WebMessageReceived -= OnWebViewMessage;
        _webView.WebMessageReceived += OnWebViewMessage;
        _webView.WebViewReady -= OnWebViewReady;
        _webView.WebViewReady += OnWebViewReady;
        _webView.SetNativeVisible(IsInCall);

        if (!_webViewNavigated)
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "WebView", "webrtc.html");
            _webView.NavigateToFile(htmlPath);
            _webViewNavigated = true;
        }
    }

    partial void OnIsInCallChanged(bool value)
    {
        _webView?.SetNativeVisible(value);
    }

    private void OnWebViewReady()
    {
        _webViewReady = true;
        SendToWebView("enumerateDevices", new { });
    }

    partial void OnSelectedMicrophoneChanged(MediaDeviceInfo? value)
    {
        if (value != null && _isInCall)
            ApplySettings();
    }

    partial void OnSelectedCameraChanged(MediaDeviceInfo? value)
    {
        if (value != null && _isInCall)
            ApplySettings();
    }

    partial void OnSelectedResolutionChanged(string value)
    {
        if (_isInCall)
            ApplySettings();
    }

    partial void OnSelectedFpsChanged(string value)
    {
        if (_isInCall)
            ApplySettings();
    }

    private void ApplySettings()
    {
        SendToWebView("updateSettings", new
        {
            micDeviceId = SelectedMicrophone?.DeviceId ?? "",
            cameraDeviceId = SelectedCamera?.DeviceId ?? "",
            resolution = SelectedResolution,
            fps = SelectedFps
        });
    }

    public async Task LoadRoomsAsync()
    {
        try
        {
            var rooms = await _api.GetRoomsAsync();
            Rooms.Clear();
            foreach (var room in rooms)
            {
                if (room.IsVoiceEnabled || room.IsVideoEnabled)
                    Rooms.Add(room);
            }
            
            if (Rooms.Count == 0)
            {
                StatusText = "No voice/video enabled rooms found.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load rooms: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task JoinCallAsync()
    {
        if (SelectedRoom == null)
        {
            StatusText = "Please select a room first.";
            return;
        }

        try
        {
            StatusText = "Joining...";
            await _chat.JoinRoomAsync(SelectedRoom.Id);
            _sessionInfo = await _chat.JoinVoiceVideoAsync(SelectedRoom.Id);
            IsInCall = true;
            StatusText = "Connected";

            var initMsg = JsonSerializer.Serialize(new
            {
                type = "init",
                userId = _auth.CurrentUser?.Id.ToString() ?? "",
                roomId = SelectedRoom.Id.ToString(),
                janusSessionId = _sessionInfo.JanusSessionId,
                janusHandleId = _sessionInfo.JanusHandleId,
                janusRoomId = _sessionInfo.JanusRoomId,
                serverBaseUrl = _sessionInfo.ServerBaseUrl,
                authToken = _auth.Token ?? "",
                iceServers = _sessionInfo.IceServers,
                micDeviceId = SelectedMicrophone?.DeviceId ?? "",
                cameraDeviceId = SelectedCamera?.DeviceId ?? "",
                resolution = SelectedResolution,
                fps = SelectedFps
            });

            _webView?.PostWebMessage(initMsg);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to join: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LeaveCallAsync()
    {
        if (SelectedRoom == null) return;

        try
        {
            SendToWebView("stop", new { });
            await _chat.LeaveVoiceVideoAsync(SelectedRoom.Id);
            await _chat.LeaveRoomAsync(SelectedRoom.Id);
        }
        catch { }
        finally
        {
            IsInCall = false;
            IsMuted = false;
            IsCameraOff = false;
            IsScreenSharing = false;
            Participants.Clear();
            StatusText = null;
            _sessionInfo = null;
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        SendToWebView("toggleMute", new { });
    }

    [RelayCommand]
    private void ToggleCamera()
    {
        SendToWebView("toggleCamera", new { });
    }

    [RelayCommand]
    private void ToggleScreenShare()
    {
        if (IsScreenSharing)
            SendToWebView("stopScreenShare", new { });
        else
            SendToWebView("startScreenShare", new { });
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
    }

    private void OnWebViewMessage(string json)
    {
        try
        {
            var cleaned = json.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    _webViewReady = true;
                    SendToWebView("enumerateDevices", new { });
                    break;

                case "devices":
                    UpdateDeviceList(root);
                    break;

                case "muteChanged":
                    IsMuted = root.GetProperty("isMuted").GetBoolean();
                    break;

                case "cameraChanged":
                    IsCameraOff = root.GetProperty("isCameraOff").GetBoolean();
                    break;

                case "screenShareStarted":
                    IsScreenSharing = true;
                    break;

                case "screenShareStopped":
                    IsScreenSharing = false;
                    break;

                case "streamReady":
                    break;

                case "error":
                    var message = root.GetProperty("message").GetString();
                    StatusText = message;
                    break;
            }
        }
        catch { }
    }

    private void UpdateDeviceList(JsonElement root)
    {
        if (root.TryGetProperty("microphones", out var mics))
        {
            var prevMicId = SelectedMicrophone?.DeviceId;
            Microphones.Clear();
            foreach (var mic in mics.EnumerateArray())
            {
                var info = new MediaDeviceInfo
                {
                    DeviceId = mic.GetProperty("deviceId").GetString() ?? "",
                    Label = mic.GetProperty("label").GetString() ?? "Unknown Mic"
                };
                Microphones.Add(info);
                if (info.DeviceId == prevMicId)
                    SelectedMicrophone = info;
            }
            if (SelectedMicrophone == null && Microphones.Count > 0)
                SelectedMicrophone = Microphones[0];
        }

        if (root.TryGetProperty("cameras", out var cams))
        {
            var prevCamId = SelectedCamera?.DeviceId;
            Cameras.Clear();
            foreach (var cam in cams.EnumerateArray())
            {
                var info = new MediaDeviceInfo
                {
                    DeviceId = cam.GetProperty("deviceId").GetString() ?? "",
                    Label = cam.GetProperty("label").GetString() ?? "Unknown Camera"
                };
                Cameras.Add(info);
                if (info.DeviceId == prevCamId)
                    SelectedCamera = info;
            }
            if (SelectedCamera == null && Cameras.Count > 0)
                SelectedCamera = Cameras[0];
        }
    }

    private void OnWebRtcSignal(Guid roomId, Guid fromUserId, string signalType, string payload)
    {
        if (SelectedRoom == null || roomId != SelectedRoom.Id) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SendToWebView("signal", new
            {
                fromUserId = fromUserId.ToString(),
                signalType,
                payload
            });
        });
    }

    private void OnUserJoined(Guid roomId, Guid userId)
    {
        if (SelectedRoom == null || roomId != SelectedRoom.Id) return;
        if (userId == _auth.CurrentUser?.Id) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!Participants.Contains(userId))
                Participants.Add(userId);

            SendToWebView("participantJoined", new { userId = userId.ToString() });
        });
    }

    private void OnUserLeft(Guid roomId, Guid userId)
    {
        if (SelectedRoom == null || roomId != SelectedRoom.Id) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Participants.Remove(userId);
            SendToWebView("participantLeft", new { userId = userId.ToString() });
        });
    }

    private void SendToWebView(string type, object data)
    {
        try
        {
            var dict = new Dictionary<string, object> { ["type"] = type };
            var dataJson = JsonSerializer.Serialize(data);
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(dataJson);
            if (dataDict != null)
            {
                foreach (var kv in dataDict)
                    dict[kv.Key] = kv.Value;
            }
            _webView?.PostWebMessage(JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}

public class MediaDeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public override string ToString() => Label;
}
