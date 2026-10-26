using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// The advertised profile-level-id is what a receiver builds its decoder from. Understate
/// it and a hardware decoder is configured for a stream less than half the size it is
/// about to be sent: it decodes a moment's worth and stops, while the bitrate keeps
/// reading perfectly healthy because the packets really are arriving.
/// </summary>
public class H264ProfileLevelTests
{
    private static int Level(int width, int height, int fps) => H264ProfileLevel.LevelFor(width, height, fps);

    [Fact]
    public void Macroblocks_round_up_to_whole_sixteens()
    {
        Assert.Equal(8160, H264ProfileLevel.Macroblocks(1920, 1080));   // 120 x 68
        Assert.Equal(3600, H264ProfileLevel.Macroblocks(1280, 720));    // 80 x 45
        Assert.Equal(14400, H264ProfileLevel.Macroblocks(2560, 1440));  // 160 x 90
    }

    [Theory]
    // The levels FFmpeg's own encoders pick for these, which is the cross-check that
    // matters: the SDP has to describe the stream the encoder actually produces.
    [InlineData(1280, 720, 30, 0x1F)]   // 3.1
    [InlineData(1920, 1080, 30, 0x28)]  // 4.0
    [InlineData(1920, 1080, 60, 0x2A)]  // 4.2
    [InlineData(2560, 1440, 60, 0x33)]  // 5.1
    [InlineData(3840, 2160, 30, 0x33)]  // 5.1
    [InlineData(3840, 2160, 60, 0x34)]  // 5.2
    public void The_level_covers_the_stream(int width, int height, int fps, int expected)
    {
        Assert.Equal(expected, Level(width, height, fps));
    }

    [Fact]
    public void The_old_constant_would_not_have_covered_1080p()
    {
        // 42e01f is Constrained Baseline 3.1, which permits 3600 macroblocks a frame. This
        // is the bug the whole class exists for.
        Assert.True(H264ProfileLevel.Macroblocks(1920, 1080) > 3600);
        Assert.True(Level(1920, 1080, 30) > 0x1F);
    }

    [Fact]
    public void Nothing_is_advertised_below_3_1()
    {
        // Every endpoint handles 3.1, and undercutting it gains nothing while inviting a
        // sender to hold back.
        Assert.Equal(0x1F, Level(320, 240, 15));
        Assert.Equal(0x1F, Level(640, 360, 30));
    }

    [Fact]
    public void Absurd_input_still_yields_a_usable_level()
    {
        Assert.Equal(0x34, Level(7680, 4320, 120));
        Assert.Equal(0x1F, Level(0, 0, 0));
    }

    [Fact]
    public void The_advertised_profile_is_the_one_the_encoders_produce()
    {
        // All the H.264 encoders are pinned to Main; if that changes, this must too.
        var fmtp = H264ProfileLevel.SendFmtp(1920, 1080, 60);

        Assert.Equal("packetization-mode=1;profile-level-id=4d402a", fmtp);
        Assert.StartsWith("packetization-mode=1;", fmtp);
    }

    [Fact]
    public void Receiving_advertises_the_top_level()
    {
        // Decoding is FFmpeg's job and it does not care, so there is no reason to make a
        // sender hold back.
        Assert.Equal("packetization-mode=1;profile-level-id=4d4034", H264ProfileLevel.ReceiveFmtp());
    }

    [Fact]
    public void A_higher_frame_rate_can_need_a_higher_level_at_the_same_size()
    {
        // The macroblock rate limit binds before the frame size does, which is exactly
        // what caught 1080p60.
        Assert.True(Level(1920, 1080, 60) > Level(1920, 1080, 30));
    }
}
