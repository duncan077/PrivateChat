using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// SIPSorcery offers only transport-cc on its video m-line. Feedback is negotiated per
/// direction, so anything absent from our offer is something a peer will never send —
/// including the keyframe requests this client depends on for recovery.
/// </summary>
public class SdpFeedbackTests
{
    [Fact]
    public void The_offer_gains_the_feedback_sipsorcery_leaves_out()
    {
        const string sdp =
            "v=0\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=rtcp-fb:96 transport-cc\r\n" +
            "a=fmtp:96 packetization-mode=1\r\n";

        var result = SdpFeedback.AddVideoFeedback(sdp);

        Assert.Contains("a=rtcp-fb:96 nack pli\r\n", result);
        Assert.Contains("a=rtcp-fb:96 ccm fir\r\n", result);
        Assert.Contains("a=rtcp-fb:96 goog-remb\r\n", result);

        // And what was already there survives.
        Assert.Contains("a=rtcp-fb:96 transport-cc\r\n", result);
        Assert.Contains("a=fmtp:96 packetization-mode=1\r\n", result);
    }

    [Fact]
    public void Bare_nack_is_never_offered()
    {
        // Offering it invites a peer to ask for a lost packet again, and answering on this
        // SSRC means emitting a sequence number far below its neighbours — which a sender's
        // SRTP layer reads as a wrap and answers by incrementing its rollover counter for
        // good. Every packet after that is encrypted against a counter the receiver does
        // not share, so the stream dies rather than losing a frame. RTX is the mechanism
        // that avoids this, and it is not built yet.
        const string sdp =
            "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n";

        var result = SdpFeedback.AddVideoFeedback(sdp);

        Assert.DoesNotContain("a=rtcp-fb:96 nack\r\n", result);

        // "nack pli" is a different attribute and is wanted.
        Assert.Contains("a=rtcp-fb:96 nack pli\r\n", result);
    }

    [Fact]
    public void Feedback_already_offered_is_not_offered_twice()
    {
        const string sdp =
            "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=rtcp-fb:96 nack pli\r\n";

        var result = SdpFeedback.AddVideoFeedback(sdp);

        Assert.Equal(1, CountOccurrences(result, "a=rtcp-fb:96 nack pli\r\n"));
    }

    [Fact]
    public void Only_the_video_section_is_touched()
    {
        const string sdp =
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n";

        var result = SdpFeedback.AddVideoFeedback(sdp);

        Assert.DoesNotContain("a=rtcp-fb:111", result);
        Assert.Contains("a=rtcp-fb:96 nack pli", result);
    }

    [Fact]
    public void Every_video_payload_type_gets_the_feedback()
    {
        // The offer lists everything we can send; whichever is chosen must have it.
        const string sdp =
            "m=video 9 UDP/TLS/RTP/SAVPF 96 98\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=rtpmap:98 VP9/90000\r\n";

        var result = SdpFeedback.AddVideoFeedback(sdp);

        Assert.Contains("a=rtcp-fb:96 nack pli\r\n", result);
        Assert.Contains("a=rtcp-fb:98 nack pli\r\n", result);
    }

    [Fact]
    public void An_sdp_with_no_video_is_returned_unchanged()
    {
        const string sdp = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\n";

        Assert.Equal(sdp, SdpFeedback.AddVideoFeedback(sdp));
        Assert.Equal(string.Empty, SdpFeedback.AddVideoFeedback(null));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
