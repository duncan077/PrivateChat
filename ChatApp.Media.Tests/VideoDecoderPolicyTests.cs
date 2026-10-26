using ChatApp.Core.Media;
using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// The NV12 buffer layout the hardware read-back writes into.
/// <para>
/// This is worth its own tests because the failure is not a wrong pixel: FFmpeg validates
/// the destination stride with <c>av_assert0</c>, which is compiled into release builds
/// and calls <c>abort()</c>. A one-byte error here kills the process on the first frame
/// of whatever stream happens to be an odd width.
/// </para>
/// </summary>
public class Nv12LayoutTests
{
    /// <summary>FFmpeg's own rule, from av_image_get_linesize: 2 * ceil(width / 2).</summary>
    private static int ExpectedChromaStride(int width) => 2 * ((width + 1) / 2);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(640, 360)]
    [InlineData(2, 2)]
    // Odd widths: legal in AV1 and VP9, and the case that aborts if the stride is short.
    [InlineData(641, 360)]
    [InlineData(1365, 767)]
    [InlineData(1, 1)]
    public void Chroma_rows_are_wide_enough_for_ffmpeg(int width, int height)
    {
        var layout = Nv12Layout.For(width, height);

        Assert.Equal(ExpectedChromaStride(width), layout.ChromaStride);
        Assert.True(layout.ChromaStride >= layout.LumaStride, "chroma rows are never narrower than luma rows");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(641, 361)]
    [InlineData(1, 1)]
    public void The_buffer_holds_every_row_of_both_planes(int width, int height)
    {
        var layout = Nv12Layout.For(width, height);

        // Chroma is half height rounded up: an odd-height frame still has a last row.
        Assert.Equal((height + 1) / 2, layout.ChromaHeight);
        Assert.Equal(width * height, layout.LumaSize);

        // The last byte the transfer writes must be inside the allocation.
        var lastChromaRowEnd = layout.LumaSize + layout.ChromaStride * layout.ChromaHeight;
        Assert.Equal(lastChromaRowEnd, layout.Total);
        Assert.True(layout.Total >= layout.LumaSize + layout.ChromaStride);
    }
}

/// <summary>
/// Decoder selection is what decides whether a six-person call costs a core per
/// participant or almost nothing. A wrong ladder is invisible at runtime — the call still
/// works, it just quietly runs on the CPU — so the ordering is pinned here.
/// </summary>
public class VideoDecoderPolicyTests
{
    private static VideoEncoderPolicy Hardware() => new() { AllowHardwareDecode = true };
    private static VideoEncoderPolicy SoftwareOnly() => new() { AllowHardwareDecode = false };

    private static string[] Ladder(VideoEncoderPolicy policy, VideoCodec codec, OSPlatformKind platform)
        => policy.GetDecoderCandidates(codec, platform).Select(c => c.ToString()).ToArray();

    [Fact]
    public void Windows_tries_the_gpu_before_software()
    {
        Assert.Equal(["h264+d3d11va", "h264"], Ladder(Hardware(), VideoCodec.H264, OSPlatformKind.Windows));
        Assert.Equal(["hevc+d3d11va", "hevc"], Ladder(Hardware(), VideoCodec.H265, OSPlatformKind.Windows));
    }

    [Fact]
    public void Linux_uses_vaapi_rather_than_a_windows_api()
    {
        Assert.Equal(["h264+vaapi", "h264"], Ladder(Hardware(), VideoCodec.H264, OSPlatformKind.Linux));
    }

    [Fact]
    public void Android_reaches_the_gpu_through_mediacodec_decoders()
    {
        // MediaCodec has no generic-decoder hwaccel, so it is named as a decoder rather
        // than paired with a device context.
        Assert.Equal(["h264_mediacodec", "h264"], Ladder(Hardware(), VideoCodec.H264, OSPlatformKind.Android));
        Assert.Equal(["hevc_mediacodec", "hevc"], Ladder(Hardware(), VideoCodec.H265, OSPlatformKind.Android));
    }

    [Fact]
    public void Disabling_hardware_decode_leaves_only_software()
    {
        // The user-facing GPU toggle has to be able to rescue a machine with a broken
        // driver, which means no hardware candidate may survive it.
        foreach (var platform in Enum.GetValues<OSPlatformKind>())
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            var candidates = SoftwareOnly().GetDecoderCandidates(codec, platform);

            Assert.All(candidates, candidate => Assert.False(candidate.IsHardware));
        }

        Assert.Equal(["h264"], Ladder(SoftwareOnly(), VideoCodec.H264, OSPlatformKind.Windows));
    }

    [Fact]
    public void Vp8_goes_straight_to_software()
    {
        // Hardware VP8 decode is rare enough that a failed attempt costs more than the
        // software decode it was trying to avoid.
        Assert.Equal(["vp8"], Ladder(Hardware(), VideoCodec.Vp8, OSPlatformKind.Windows));
    }

    [Fact]
    public void Av1_prefers_dav1d_over_the_native_decoder_in_software()
    {
        // dav1d is several times faster, but minimal FFmpeg builds omit it — hence the
        // native decoder behind it rather than instead of it.
        Assert.Equal(["av1+d3d11va", "libdav1d", "av1"], Ladder(Hardware(), VideoCodec.Av1, OSPlatformKind.Windows));
    }

    [Fact]
    public void Every_ladder_ends_in_software()
    {
        // A machine with no GPU support at all must still have something to decode with,
        // for every codec we might negotiate.
        foreach (var platform in Enum.GetValues<OSPlatformKind>())
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            var candidates = Hardware().GetDecoderCandidates(codec, platform);

            Assert.NotEmpty(candidates);
            Assert.False(candidates[^1].IsHardware);
            Assert.DoesNotContain("mediacodec", candidates[^1].DecoderName);
        }
    }

    [Fact]
    public void Hardware_candidates_name_the_generic_decoder_not_a_wrapper()
    {
        // h264_cuvid and friends carry their own surface queues; that queue is latency on
        // every frame, which is exactly what a call cannot afford.
        var candidates = Hardware().GetDecoderCandidates(VideoCodec.H264, OSPlatformKind.Windows);

        var accelerated = candidates.Single(c => c.IsHardware);
        Assert.Equal("h264", accelerated.DecoderName);
        Assert.Equal(VideoHardwareDevice.D3d11Va, accelerated.Device);
    }
}
