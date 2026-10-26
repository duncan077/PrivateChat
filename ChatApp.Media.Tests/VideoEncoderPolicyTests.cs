using ChatApp.Core.Media;

namespace ChatApp.Media.Tests;

/// <summary>
/// Encoder selection replaces the Chromium feature flags the Blazor client passed to
/// WebView2. Getting the ordering wrong silently drops the app to software encoding.
/// </summary>
public class VideoEncoderPolicyTests
{
    private static VideoEncoderPolicy Hardware() => new() { AllowHardwareEncode = true, AllowHevc = true };
    private static VideoEncoderPolicy SoftwareOnly() => new() { AllowHardwareEncode = false };

    [Fact]
    public void Prefers_hardware_then_falls_back_to_software_on_windows()
    {
        var candidates = Hardware().GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Windows);

        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264"], candidates);
    }

    [Fact]
    public void Always_ends_with_a_software_encoder()
    {
        // The last candidate must always be one that exists everywhere, or a machine
        // with no GPU support would have no encoder at all.
        foreach (var platform in new[] { OSPlatformKind.Windows, OSPlatformKind.Linux, OSPlatformKind.Android })
        {
            foreach (var codec in Enum.GetValues<VideoCodec>())
            {
                var candidates = Hardware().GetEncoderCandidates(codec, platform);

                Assert.NotEmpty(candidates);
                Assert.StartsWith("lib", candidates[^1]);
            }
        }
    }

    [Fact]
    public void Disabling_hardware_yields_only_the_software_encoder()
    {
        var candidates = SoftwareOnly().GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Windows);

        Assert.Equal(["libx264"], candidates);
    }

    [Fact]
    public void Disallowing_hevc_drops_hardware_hevc_but_keeps_a_fallback()
    {
        var policy = new VideoEncoderPolicy { AllowHardwareEncode = true, AllowHevc = false };

        var candidates = policy.GetEncoderCandidates(VideoCodec.H265, OSPlatformKind.Windows);

        Assert.DoesNotContain("hevc_nvenc", candidates);
        Assert.Equal(["libx265"], candidates);
    }

    [Fact]
    public void Allowing_hevc_keeps_the_hardware_chain()
    {
        var candidates = Hardware().GetEncoderCandidates(VideoCodec.H265, OSPlatformKind.Windows);

        Assert.Equal(["hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf", "libx265"], candidates);
    }

    [Fact]
    public void Linux_offers_vaapi_rather_than_windows_only_encoders()
    {
        var candidates = Hardware().GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Linux);

        Assert.Equal(["h264_nvenc", "h264_vaapi", "libx264"], candidates);
        Assert.DoesNotContain("h264_qsv", candidates);
        Assert.DoesNotContain("h264_mf", candidates);
    }

    [Fact]
    public void Android_uses_mediacodec()
    {
        var candidates = Hardware().GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Android);

        Assert.Equal(["h264_mediacodec", "libx264"], candidates);
    }

    [Fact]
    public void Vp8_and_vp9_have_no_hardware_path_and_go_straight_to_software()
    {
        Assert.Equal(["libvpx"], Hardware().GetEncoderCandidates(VideoCodec.Vp8, OSPlatformKind.Windows));
        Assert.Equal(["libvpx-vp9"], Hardware().GetEncoderCandidates(VideoCodec.Vp9, OSPlatformKind.Windows));
    }

    [Fact]
    public void Repeated_calls_do_not_accumulate_candidates()
    {
        // The candidate list is built fresh each call; a shared mutable list would grow
        // on every invocation.
        var policy = Hardware();

        var first = policy.GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Windows);
        var second = policy.GetEncoderCandidates(VideoCodec.H264, OSPlatformKind.Windows);

        Assert.Equal(first, second);
    }
}
