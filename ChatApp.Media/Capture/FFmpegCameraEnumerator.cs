using System.Runtime.InteropServices;
using ChatApp.Core.Media;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Capture;

/// <summary>
/// Enumerates cameras through FFmpeg's device-list API.
/// <para>
/// Uses <c>avdevice_list_input_sources</c> rather than parsing <c>-list_devices</c>
/// stderr. On Windows this returns dshow's stable "alternative name" device paths
/// (<c>@device_sw_{…}</c>) instead of friendly names, which matters because the chosen
/// camera is persisted in settings and two identical webcams share a friendly name.
/// </para>
/// </summary>
public sealed unsafe class FFmpegCameraEnumerator : IMediaDeviceEnumerator
{
    private readonly ILogger _logger;

    /// <summary>
    /// FFmpeg has no device hot-plug notification, so this never fires. The settings
    /// dialog re-enumerates when it opens.
    /// </summary>
    public event Action? DevicesChanged
    {
        add { }
        remove { }
    }

    public FFmpegCameraEnumerator(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public IReadOnlyList<MediaDeviceInfo> GetDevices(MediaDeviceKind kind)
    {
        if (kind != MediaDeviceKind.VideoInput)
            return [];

        try
        {
            FFmpegCaptureSource.EnsureDevicesRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFmpeg unavailable; cannot enumerate cameras.");
            return [];
        }

        var formatName = OperatingSystem.IsWindows() ? "dshow" : "v4l2";
        var inputFormat = ffmpeg.av_find_input_format(formatName);

        if (inputFormat == null)
        {
            _logger.LogWarning("FFmpeg input '{Format}' unavailable; is avdevice present?", formatName);
            return [];
        }

        AVDeviceInfoList* list = null;
        try
        {
            var result = ffmpeg.avdevice_list_input_sources(inputFormat, null, null, &list);

            if (result < 0 || list == null)
            {
                _logger.LogDebug(
                    "{Format} does not implement device listing ({Error}).",
                    formatName, FFmpegLoaderDescribe(result));
                return [];
            }

            var devices = new List<MediaDeviceInfo>(list->nb_devices);

            for (var i = 0; i < list->nb_devices; i++)
            {
                var device = list->devices[i];
                if (device == null)
                    continue;

                if (!HasVideo(device))
                    continue;

                var name = Marshal.PtrToStringAnsi((IntPtr)device->device_name);
                var description = Marshal.PtrToStringAnsi((IntPtr)device->device_description);

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                devices.Add(new MediaDeviceInfo(
                    DeviceId: name,
                    Label: string.IsNullOrWhiteSpace(description) ? name : description,
                    Kind: "videoinput"));
            }

            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera enumeration failed.");
            return [];
        }
        finally
        {
            if (list != null)
                ffmpeg.avdevice_free_list_devices(&list);
        }
    }

    /// <summary>
    /// dshow reports microphones through the same list, so audio-only entries are
    /// filtered out. Older FFmpeg builds leave the media-type array empty, in which case
    /// we keep the entry rather than silently hiding a working camera.
    /// </summary>
    private static bool HasVideo(AVDeviceInfo* device)
    {
        if (device->nb_media_types == 0 || device->media_types == null)
            return true;

        for (var i = 0; i < device->nb_media_types; i++)
        {
            if (device->media_types[i] == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return true;
        }

        return false;
    }

    private static string FFmpegLoaderDescribe(int errorCode)
        => errorCode < 0 ? Codec.FFmpegLoader.DescribeError(errorCode) : "no devices";
}
