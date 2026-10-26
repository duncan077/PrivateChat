using ChatApp.Core.Media;
using ChatApp.Media.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatApp.Media.Tests;

/// <summary>
/// A feed nobody is looking at still receives RTP. Spending a core decoding pictures that
/// are thrown away is the difference between a call that scales to a full room and one
/// that does not, so the gate — and the keyframe request that has to follow it — are
/// pinned here.
/// </summary>
public class RemoteFeedTests
{
    /// <summary>An H.264 access unit shaped like an IDR. Real enough to reach the decoder.</summary>
    private static readonly byte[] AccessUnit = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x21, 0xFF];

    private static RemoteFeed CreateFeed() => new(
        feedId: 42,
        userId: Guid.NewGuid(),
        displayName: "Test",
        kind: RemoteStreamKind.Camera,
        policy: new VideoEncoderPolicy { AllowHardwareDecode = false },
        logger: NullLogger.Instance);

    [Fact]
    public void Skips_decoding_entirely_while_no_renderer_is_attached()
    {
        using var feed = CreateFeed();

        feed.HandleFrame(isAudio: false, AccessUnit, VideoCodec.H264, audioChannels: 1);

        // The gate sits in front of the decoder rather than in front of the emit, so a
        // hidden tile costs nothing at all — no decoder is even created.
        Assert.Equal(0, feed.VideoPacketsReceived);
        Assert.Equal(0, feed.VideoAccessUnits);
        Assert.Equal(0, feed.VideoTrackCount);
        Assert.False(feed.IsActive);
    }

    [FFmpegFact]
    public void Asks_for_a_keyframe_once_a_renderer_attaches()
    {
        using var feed = CreateFeed();

        var keyframeRequests = 0;
        feed.KeyframeNeeded += _ => keyframeRequests++;

        feed.HandleFrame(isAudio: false, AccessUnit, VideoCodec.H264, audioChannels: 1);
        Assert.Equal(0, keyframeRequests);

        feed.FrameReady += (IVideoSource _, in VideoFrame _) => { };
        feed.HandleFrame(isAudio: false, AccessUnit, VideoCodec.H264, audioChannels: 1);

        // Frames were skipped while gated, so the decoder's reference chain is broken and
        // everything until the next keyframe would be smear.
        Assert.True(keyframeRequests > 0, "attaching a renderer did not request a keyframe");
        Assert.Equal(1, feed.VideoAccessUnits);
    }
}
