using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChatApp.Core.Media;
using ChatApp.Core.Services;
using ChatApp.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatApp.Ui.ViewModels;

/// <summary>A selectable option with a stable value and a human label.</summary>
public sealed record SettingsOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The settings dialog: devices, call quality, audio behaviour, and the server URL.
/// <para>
/// Edits are staged locally and written back through <see cref="CallStore"/> only on
/// Save, so cancelling really cancels. The two volume sliders are the exception — they
/// apply live, because adjusting a volume you cannot hear the effect of is guesswork.
/// </para>
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly CallStore _calls;
    private readonly GrpcChannelProvider _channels;
    private readonly MediaEngineHost? _media;
    private readonly DispatcherTimer _meterTimer;
    private bool _disposed;

    /// <summary>
    /// Chains microphone-test restarts so a quick series of selection changes cannot
    /// overlap Stop/Start pairs — the engine serializes too, but ordering is decided
    /// here: last selection wins.
    /// </summary>
    private Task _micTestChain = Task.CompletedTask;

    /// <summary>True once the initial device load has finished and selection changes are the user's.</summary>
    private bool _devicesLoaded;

    /// <summary>
    /// Sentinel for "let the system decide". An empty id flows through the store's
    /// NormalizeDeviceId as null, which every capture backend treats as the default
    /// endpoint — so this needs no special casing outside this dialog.
    /// </summary>
    private static MediaDeviceInfo DefaultDevice(string kind) => new(string.Empty, "System default", kind);

    /// <summary>Raised when the dialog wants to close, after Save or Cancel.</summary>
    public event Action? Closed;

    // ─── Server ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ServerUrlInvalid { get; set; }

    /// <summary>True when the saved URL differs from the one in use — a sign-out is needed.</summary>
    [ObservableProperty]
    public partial bool ServerChangePending { get; set; }

    // ─── Devices ───────────────────────────────────────────────────────────────

    public ObservableCollection<MediaDeviceInfo> Microphones { get; } = [];
    public ObservableCollection<MediaDeviceInfo> Speakers { get; } = [];
    public ObservableCollection<MediaDeviceInfo> Cameras { get; } = [];

    [ObservableProperty]
    public partial MediaDeviceInfo? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial MediaDeviceInfo? SelectedSpeaker { get; set; }

    [ObservableProperty]
    public partial MediaDeviceInfo? SelectedCamera { get; set; }

    // ─── Video ─────────────────────────────────────────────────────────────────

    public ObservableCollection<SettingsOption> Resolutions { get; } =
    [
        new("3840x2160", "4K (3840×2160)"),
        new("2560x1440", "1440p (2560×1440)"),
        new("1920x1080", "1080p (1920×1080)"),
        new("1280x720", "720p (1280×720)"),
        new("640x480", "480p (640×480)"),
        new("480x360", "360p (480×360)")
    ];

    public ObservableCollection<int> FrameRates { get; } = [15, 24, 30, 60];

    /// <summary>Filled from the encoder probe, so only publishable codecs are offered.</summary>
    public ObservableCollection<SettingsOption> Codecs { get; } = [];

    public ObservableCollection<SettingsOption> ContentHints { get; } =
    [
        new("motion", "Motion (smooth video)"),
        new("detail", "Detail (sharp text)")
    ];

    public ObservableCollection<SettingsOption> GpuPreferences { get; } =
    [
        new("high-performance", "High performance"),
        new("power-saving", "Power saving"),
        new("auto", "Auto")
    ];

    [ObservableProperty]
    public partial SettingsOption? SelectedResolution { get; set; }

    [ObservableProperty]
    public partial int SelectedFps { get; set; }

    [ObservableProperty]
    public partial int BitrateKbps { get; set; }

    [ObservableProperty]
    public partial SettingsOption? SelectedCodec { get; set; }

    [ObservableProperty]
    public partial SettingsOption? SelectedContentHint { get; set; }

    [ObservableProperty]
    public partial bool GpuAccelerationEnabled { get; set; }

    [ObservableProperty]
    public partial SettingsOption? SelectedGpuPreference { get; set; }

    [ObservableProperty]
    public partial bool HideLocalPreview { get; set; }

    /// <summary>Shown only when the machine actually has more than one GPU.</summary>
    public bool ShowGpuPreference => _calls.HasMultipleHardwareGpus;

    // ─── Audio ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial double MicrophoneVolume { get; set; }

    [ObservableProperty]
    public partial double PlaybackVolume { get; set; }

    [ObservableProperty]
    public partial bool NoiseSuppressionEnabled { get; set; }

    [ObservableProperty]
    public partial bool VoiceGateEnabled { get; set; }

    [ObservableProperty]
    public partial bool VoiceGateAuto { get; set; }

    [ObservableProperty]
    public partial int VoiceGateThreshold { get; set; }

    [ObservableProperty]
    public partial bool PushToTalkEnabled { get; set; }

    [ObservableProperty]
    public partial string PushToTalkKey { get; set; } = "Space";

    [ObservableProperty]
    public partial bool PushToTalkSoundEnabled { get; set; }

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; }

    /// <summary>Live microphone level, 0–100, for the meter.</summary>
    [ObservableProperty]
    public partial int MicLevel { get; set; }

    public bool HasMediaEngine => _media is not null;

    // ─── Screen share ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool ScreenShareAudioEnabled { get; set; }

    [ObservableProperty]
    public partial bool ScreenShareAutoGainEnabled { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public SettingsViewModel(CallStore calls, GrpcChannelProvider channels, MediaEngineHost? media)
    {
        _calls = calls;
        _channels = channels;
        _media = media;

        LoadFromStore();

        // 10 Hz is fast enough for a level meter and costs nothing.
        _meterTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, HandleMeterTick);

        _ = InitializeAsync();
    }

    private void LoadFromStore()
    {
        ServerUrl = _channels.ServerUrl;

        SelectedResolution = Resolutions.FirstOrDefault(r => r.Value == _calls.SelectedResolution)
            ?? Resolutions.First(r => r.Value == "1280x720");

        SelectedFps = FrameRates.Contains(_calls.SelectedFps) ? _calls.SelectedFps : 30;
        BitrateKbps = _calls.SelectedBitrate;
        SelectedContentHint = ContentHints.FirstOrDefault(h => h.Value == _calls.SelectedVideoContentHint)
            ?? ContentHints[0];
        GpuAccelerationEnabled = _calls.GpuAccelerationEnabled;
        SelectedGpuPreference = GpuPreferences.FirstOrDefault(p => p.Value == _calls.WindowsGpuPreference)
            ?? GpuPreferences[0];
        HideLocalPreview = _calls.HideLocalPreview;

        MicrophoneVolume = _calls.Volume;
        PlaybackVolume = _calls.StreamVolume;
        NoiseSuppressionEnabled = _calls.NoiseSuppressionEnabled;
        VoiceGateEnabled = _calls.VoiceGateEnabled;
        VoiceGateAuto = _calls.VoiceGateAuto;
        VoiceGateThreshold = _calls.VoiceGateThreshold;
        PushToTalkEnabled = _calls.PushToTalkEnabled;
        PushToTalkKey = _calls.PushToTalkKey;
        PushToTalkSoundEnabled = _calls.PushToTalkSoundEnabled;
        NotificationsEnabled = _calls.NotificationsEnabled;

        ScreenShareAudioEnabled = _calls.ScreenShareAudioEnabled;
        ScreenShareAutoGainEnabled = _calls.ScreenShareAutoGainEnabled;
    }

    private async Task InitializeAsync()
    {
        if (_media is null)
            return;

        try
        {
            await LoadDevicesAsync();
            await LoadCodecsAsync();

            // The only direct start. Selection changes during the load above are
            // ignored via _devicesLoaded, so exactly one capture is opened here.
            _devicesLoaded = true;
            await _media.Engine.StartMicrophoneTestAsync(SelectedDeviceId(SelectedMicrophone));
            _meterTimer.Start();
        }
        catch (Exception ex)
        {
            Error = $"Could not read devices: {ex.Message}";
        }
    }

    private static string? SelectedDeviceId(MediaDeviceInfo? device)
        => string.IsNullOrEmpty(device?.DeviceId) ? null : device.DeviceId;

    private async Task LoadDevicesAsync()
    {
        var devices = await _media!.Engine.EnumerateDevicesAsync();

        void Fill(
            ObservableCollection<MediaDeviceInfo> target,
            MediaDeviceKind kind,
            string? selectedId,
            bool offerDefault,
            Action<MediaDeviceInfo?> select)
        {
            target.Clear();

            // Audio endpoints have an OS-level default that follows the user's system
            // choice; letting it stay unset is usually what people want. Cameras have no
            // such concept, so they get no sentinel.
            if (offerDefault)
                target.Add(DefaultDevice(kind == MediaDeviceKind.AudioInput ? "audioinput" : "audiooutput"));

            foreach (var device in devices.Where(d => d.ParsedKind == kind))
                target.Add(device);

            // No saved id selects the default entry when one exists. A saved id that no
            // longer resolves (device unplugged) falls back the same way, mirroring what
            // the engine will do at capture start.
            var match = string.IsNullOrEmpty(selectedId)
                ? null
                : target.FirstOrDefault(d => d.DeviceId == selectedId);

            select(match ?? target.FirstOrDefault());
        }

        Fill(Microphones, MediaDeviceKind.AudioInput, _calls.SelectedMicId, offerDefault: true,
            d => SelectedMicrophone = d);
        Fill(Speakers, MediaDeviceKind.AudioOutput, _calls.SelectedSpeakerId, offerDefault: true,
            d => SelectedSpeaker = d);
        Fill(Cameras, MediaDeviceKind.VideoInput, _calls.SelectedCameraId, offerDefault: false,
            d => SelectedCamera = d);
    }

    private async Task LoadCodecsAsync()
    {
        var supported = await _media!.Engine.GetSupportedVideoCodecsAsync();

        Codecs.Clear();
        foreach (var codec in supported)
        {
            // The parentheticals matter: Janus pins one codec per publisher, so a codec
            // the other side cannot decode means they see a black tile with no error.
            Codecs.Add(new SettingsOption(
                codec.ToString().ToLowerInvariant(),
                codec switch
                {
                    VideoCodec.H264 => "H.264 (works everywhere)",
                    VideoCodec.H265 => "H.265 / HEVC (needs receiver support)",
                    VideoCodec.Av1 => "AV1 (needs receiver support)",
                    VideoCodec.Vp9 => "VP9",
                    _ => "VP8"
                }));
        }

        SelectedCodec = Codecs.FirstOrDefault(c => c.Value == _calls.SelectedVideoCodec)
            ?? Codecs.FirstOrDefault();
    }

    /// <summary>Restarts the test capture when the user picks a different microphone.</summary>
    partial void OnSelectedMicrophoneChanged(MediaDeviceInfo? value)
    {
        // _devicesLoaded gates out the change raised by the initial load — the start for
        // that one is issued by InitializeAsync, and reacting here as well opened a
        // second capture that leaked.
        if (_media is null || value is null || _disposed || !_devicesLoaded)
            return;

        var deviceId = SelectedDeviceId(value);
        var engine = _media.Engine;

        // Appended to a chain rather than fired independently: two quick selection
        // changes must run their Stop/Start pairs in order, or the older device can end
        // up as the one left running.
        _micTestChain = _micTestChain.ContinueWith(async _ =>
            {
                try
                {
                    await engine.StopMicrophoneTestAsync();

                    if (!_disposed)
                        await engine.StartMicrophoneTestAsync(deviceId);
                }
                catch
                {
                    // The meter simply stays quiet; the selection itself is still saved.
                }
            }, TaskScheduler.Default)
            .Unwrap();
    }

    private async void HandleMeterTick(object? sender, EventArgs e)
    {
        if (_media is null || _disposed)
            return;

        try
        {
            var level = await _media.Engine.GetVoiceGateLevelAsync();
            MicLevel = level.Level;
        }
        catch
        {
            // A failed poll leaves the last reading in place.
        }
    }

    partial void OnMicrophoneVolumeChanged(double value) => _calls.SetVolumePreference(value);

    partial void OnPlaybackVolumeChanged(double value)
    {
        _calls.SetStreamVolumePreference(value);

        // Push straight into a running engine so the slider is audible while dragged.
        _ = _media?.ApplyCurrentSettingsAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ServerUrlInvalid = false;

        if (!string.Equals(ServerUrl?.Trim(), _channels.ServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (!_channels.TrySetServerUrl(ServerUrl ?? string.Empty))
            {
                ServerUrlInvalid = true;
                return;
            }

            // The token belongs to the old server; the UI shows a sign-out hint.
            ServerChangePending = true;
        }

        _calls.ApplyCallSettings(
            SelectedMicrophone?.DeviceId,
            SelectedCamera?.DeviceId,
            SelectedSpeaker?.DeviceId,
            SelectedResolution?.Value,
            SelectedFps,
            BitrateKbps,
            SelectedCodec?.Value,
            NoiseSuppressionEnabled,
            ScreenShareAudioEnabled,
            ScreenShareAutoGainEnabled,
            HideLocalPreview,
            GpuAccelerationEnabled,
            SelectedGpuPreference?.Value,
            _calls.ForcePlatformHevcEncoderSupport,
            _calls.EnableWebRtcH265SendFeature,
            _calls.ZeroCopyVideoCaptureEnabled,
            VoiceGateEnabled,
            VoiceGateAuto,
            VoiceGateThreshold,
            SelectedContentHint?.Value);

        _calls.SetPushToTalkPreference(PushToTalkEnabled, PushToTalkKey);
        _calls.SetPushToTalkSoundPreference(PushToTalkSoundEnabled);
        _calls.SetNotificationsEnabled(NotificationsEnabled);

        // A running call picks up what can change live (bitrate, playback volume);
        // resolution and codec changes take effect on the next join.
        if (_media is not null)
            await _media.ApplyCurrentSettingsAsync();

        Closed?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _meterTimer.Stop();

        if (_media is not null)
        {
            // Appended after any pending restart: those see _disposed and skip their
            // start, and this final stop then closes whatever is still open.
            var engine = _media.Engine;
            _micTestChain = _micTestChain
                .ContinueWith(_ => engine.StopMicrophoneTestAsync(), TaskScheduler.Default)
                .Unwrap();
        }

        GC.SuppressFinalize(this);
    }
}
