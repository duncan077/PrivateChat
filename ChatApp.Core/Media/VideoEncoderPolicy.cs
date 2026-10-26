namespace ChatApp.Core.Media;

/// <summary>
/// How the media engine should choose an encoder/decoder for this machine.
/// <para>
/// This is the native replacement for the Chromium switches the Blazor client
/// passed to WebView2 (<c>--enable-accelerated-video-encode</c>,
/// <c>PlatformHEVCEncoderSupport</c>, <c>--force-high-performance-gpu</c>, …).
/// Those flags configured a browser; these fields configure FFmpeg directly.
/// </para>
/// </summary>
public sealed record VideoEncoderPolicy
{
    public bool AllowHardwareEncode { get; init; } = true;
    public bool AllowHardwareDecode { get; init; } = true;

    /// <summary>Permit H.265 negotiation. Requires a hardware encoder in practice.</summary>
    public bool AllowHevc { get; init; } = true;

    /// <summary>
    /// Keep captured frames in GPU memory between capture and encode where the
    /// platform supports it (D3D11 on Windows, VAAPI on Linux, VideoToolbox on Apple).
    /// </summary>
    public bool AllowZeroCopyCapture { get; init; } = true;

    /// <summary>"high-performance", "power-saving" or "auto".</summary>
    public string GpuPreference { get; init; } = "high-performance";

    public bool HasMultipleHardwareGpus { get; init; }

    /// <summary>Ordered encoder names to try, most preferred first.</summary>
    public IReadOnlyList<string> GetEncoderCandidates(VideoCodec codec, OSPlatformKind platform)
    {
        if (!AllowHardwareEncode)
            return [SoftwareEncoderFor(codec)];

        List<string> candidates = platform switch
        {
            OSPlatformKind.Windows => codec switch
            {
                VideoCodec.H264 => ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"],
                VideoCodec.H265 => ["hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf"],
                VideoCodec.Av1 => ["av1_nvenc", "av1_qsv", "av1_amf"],
                _ => []
            },
            OSPlatformKind.Linux => codec switch
            {
                VideoCodec.H264 => ["h264_nvenc", "h264_vaapi"],
                VideoCodec.H265 => ["hevc_nvenc", "hevc_vaapi"],
                VideoCodec.Av1 => ["av1_nvenc", "av1_vaapi"],
                _ => []
            },
            OSPlatformKind.Android => codec switch
            {
                VideoCodec.H264 => ["h264_mediacodec"],
                VideoCodec.H265 => ["hevc_mediacodec"],
                VideoCodec.Av1 => ["av1_mediacodec"],
                _ => []
            },
            _ => []
        };

        if (codec is VideoCodec.H265 && !AllowHevc)
            candidates.Clear();

        candidates.Add(SoftwareEncoderFor(codec));
        return candidates;
    }

    private static string SoftwareEncoderFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.H265 => "libx265",
        VideoCodec.Av1 => "libsvtav1",
        VideoCodec.Vp9 => "libvpx-vp9",
        _ => "libvpx"
    };

    /// <summary>
    /// Ordered decoders to try for an incoming stream, most preferred first. Always ends
    /// with a software entry, so a machine with no usable GPU still decodes.
    /// <para>
    /// Hardware entries pair the <b>generic</b> decoder with a device context instead of
    /// naming a wrapper decoder (<c>h264_cuvid</c> and friends): the wrappers hold their
    /// own surface queues, and that queue is latency paid on every frame of a call. A
    /// device context leaves the decode loop synchronous.
    /// </para>
    /// </summary>
    public IReadOnlyList<VideoDecoderCandidate> GetDecoderCandidates(VideoCodec codec, OSPlatformKind platform)
    {
        var candidates = new List<VideoDecoderCandidate>();

        if (AllowHardwareDecode && HasHardwareDecodePath(codec))
        {
            switch (platform)
            {
                case OSPlatformKind.Windows:
                    candidates.Add(new VideoDecoderCandidate(NativeDecoderFor(codec), VideoHardwareDevice.D3d11Va));
                    break;

                case OSPlatformKind.Linux:
                    candidates.Add(new VideoDecoderCandidate(NativeDecoderFor(codec), VideoHardwareDevice.Vaapi));
                    break;

                case OSPlatformKind.Android:
                    // MediaCodec is reached through its own decoders rather than a device
                    // context; there is no generic-decoder hwaccel for it.
                    candidates.Add(new VideoDecoderCandidate(MediaCodecDecoderFor(codec)));
                    break;
            }
        }

        candidates.Add(new VideoDecoderCandidate(SoftwareDecoderFor(codec)));

        // libdav1d is the fast AV1 decoder but is absent from minimal builds; the native
        // one always is.
        if (codec == VideoCodec.Av1)
            candidates.Add(new VideoDecoderCandidate(NativeDecoderFor(codec)));

        return candidates;
    }

    /// <summary>
    /// VP8 is the one codec left out: hardware VP8 decode is rare, and a stream that fails
    /// over costs more than the software decode it was avoiding. Everything else is worth
    /// attempting — an unsupported profile falls back per stream at negotiation time.
    /// </summary>
    private static bool HasHardwareDecodePath(VideoCodec codec) => codec != VideoCodec.Vp8;

    /// <summary>FFmpeg's built-in decoder, the one that carries the hwaccel configurations.</summary>
    private static string NativeDecoderFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H265 => "hevc",
        VideoCodec.Av1 => "av1",
        VideoCodec.Vp9 => "vp9",
        VideoCodec.Vp8 => "vp8",
        _ => "h264"
    };

    private static string SoftwareDecoderFor(VideoCodec codec) => codec switch
    {
        VideoCodec.Av1 => "libdav1d",
        _ => NativeDecoderFor(codec)
    };

    private static string MediaCodecDecoderFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H265 => "hevc_mediacodec",
        VideoCodec.Av1 => "av1_mediacodec",
        VideoCodec.Vp9 => "vp9_mediacodec",
        _ => "h264_mediacodec"
    };
}

/// <summary>Hardware surface API backing a decoder. <see cref="None"/> is plain memory.</summary>
public enum VideoHardwareDevice
{
    None,

    /// <summary>Direct3D 11 video decode — Windows.</summary>
    D3d11Va,

    /// <summary>VA-API — Linux.</summary>
    Vaapi
}

/// <summary>
/// One decoder to try: an FFmpeg decoder name, optionally backed by a hardware device
/// context.
/// </summary>
public sealed record VideoDecoderCandidate(
    string DecoderName,
    VideoHardwareDevice Device = VideoHardwareDevice.None)
{
    public bool IsHardware => Device != VideoHardwareDevice.None;

    /// <summary>"h264+d3d11va" — the form health logs and the stats panel show.</summary>
    public override string ToString() => Device == VideoHardwareDevice.None
        ? DecoderName
        : $"{DecoderName}+{Device.ToString().ToLowerInvariant()}";
}

public enum OSPlatformKind
{
    Windows,
    Linux,
    Android
}
