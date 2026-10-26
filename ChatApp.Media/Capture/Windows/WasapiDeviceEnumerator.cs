using System.Runtime.Versioning;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// Enumerates WASAPI endpoints and raises <see cref="DevicesChanged"/> when the set
/// changes.
/// <para>
/// Replaces the browser's <c>enumerateDevices</c>. Device ids are WASAPI endpoint id
/// strings, which are stable across reboots and replugs — important because the app
/// persists the user's chosen microphone and speaker.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiDeviceEnumerator : IMediaDeviceEnumerator, IDisposable
{
    private readonly ILogger _logger;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notificationClient;
    private bool _disposed;

    public event Action? DevicesChanged;

    public WasapiDeviceEnumerator(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _notificationClient = new NotificationClient(() => DevicesChanged?.Invoke());

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (Exception ex)
        {
            // Losing hot-plug notifications is survivable: the settings dialog can still
            // enumerate on demand.
            _logger.LogWarning(ex, "Could not register for audio device change notifications.");
        }
    }

    public IReadOnlyList<MediaDeviceInfo> GetDevices(MediaDeviceKind kind)
    {
        if (kind == MediaDeviceKind.VideoInput)
            return [];

        var flow = kind == MediaDeviceKind.AudioInput ? DataFlow.Capture : DataFlow.Render;
        var label = kind == MediaDeviceKind.AudioInput ? "audioinput" : "audiooutput";

        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            var result = new List<MediaDeviceInfo>(devices.Count);

            foreach (var device in devices)
            {
                try
                {
                    result.Add(new MediaDeviceInfo(device.ID, device.FriendlyName, label));
                }
                catch (Exception ex)
                {
                    // A device can vanish mid-enumeration; skip it rather than failing
                    // the whole list.
                    _logger.LogDebug(ex, "Skipping an audio endpoint that disappeared during enumeration.");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enumerate {Kind} devices.", kind);
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        }
        catch
        {
            // Already torn down.
        }

        _enumerator.Dispose();
    }

    /// <summary>
    /// WASAPI fires several notifications for one physical change (state, default role,
    /// property). The consumer is expected to debounce; raising each one keeps this
    /// class simple and lets the UI decide.
    /// </summary>
    private sealed class NotificationClient(Action onChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => onChanged();
        public void OnDeviceRemoved(string deviceId) => onChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => onChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
