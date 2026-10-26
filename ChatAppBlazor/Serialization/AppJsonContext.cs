using System.Text.Json.Serialization;
using ChatAppBlazor.Services;
using ChatAppBlazor.State;
using ChatAppBlazor.WebRtc;

namespace ChatAppBlazor.Serialization;

/// <summary>
/// Compile-time JSON contracts for everything the app serializes itself: the
/// JS bridge payloads, the Windows host-message channel, persisted preferences,
/// and the small REST DTOs.
///
/// Options mirror what the reflection-based calls were already doing — web-style
/// camelCase with case-insensitive reads — so the wire format is unchanged. Null
/// values are omitted to match the anonymous types these replaced.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<MediaDeviceInfo>))]
[JsonSerializable(typeof(ApiClientService.UploadResponse))]
[JsonSerializable(typeof(ApiClientService.YouTubeOEmbedResponse))]
[JsonSerializable(typeof(UpdateService.UpdateInfoDto))]
[JsonSerializable(typeof(WebRtcBridge.ScreenShareTargetPayload))]
[JsonSerializable(typeof(WebRtcBridge.AnnotationPayload))]
[JsonSerializable(typeof(WebRtcBridge.AnnotationStatePayload))]
[JsonSerializable(typeof(ScreenAudioFallbackStateMessage))]
[JsonSerializable(typeof(ScreenAudioFallbackChunkMessage))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Host → WebView notification about the Windows native screen-share audio
/// capture. Optional members are omitted when unset, matching the shapes the
/// JS side already handles.
/// </summary>
internal sealed class ScreenAudioFallbackStateMessage
{
    public string Type { get; set; } = "screen-audio-fallback-state";
    public string State { get; set; } = string.Empty;
    public string? Mode { get; set; }
    public uint? ProcessId { get; set; }
    public string? WindowTitle { get; set; }
    public string? Message { get; set; }
}

/// <summary>Host → WebView PCM chunk from the Windows loopback capture.</summary>
internal sealed class ScreenAudioFallbackChunkMessage
{
    public string Type { get; set; } = "screen-audio-fallback-chunk";
    public string Format { get; set; } = "f32le";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Data { get; set; } = string.Empty;
}
