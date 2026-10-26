using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Checks the SDP reading that decides whether Janus is actually offering video.
/// <para>
/// This replaced <c>OnVideoFormatsNegotiated</c>, which reports the local recvonly
/// track's formats and so answers "yes" for every subscriber regardless of the offer —
/// making the audio-only healing path dead code.
/// </para>
/// </summary>
public class SdpMediaInspectorTests
{
    private const string AudioOnlyOffer = """
        v=0
        o=- 1785822401207368 1 IN IP4 147.15.112.147
        s=VideoRoom 518816296
        t=0 0
        m=audio 9 UDP/TLS/RTP/SAVPF 111
        c=IN IP4 147.15.112.147
        a=sendonly
        a=mid:0
        a=rtpmap:111 opus/48000/2
        """;

    private const string AudioAndVideoOffer = """
        v=0
        o=- 1785822401207368 1 IN IP4 147.15.112.147
        s=VideoRoom 518816296
        t=0 0
        m=audio 9 UDP/TLS/RTP/SAVPF 111
        c=IN IP4 147.15.112.147
        a=sendonly
        a=mid:0
        a=rtpmap:111 opus/48000/2
        m=video 9 UDP/TLS/RTP/SAVPF 96
        c=IN IP4 147.15.112.147
        a=sendonly
        a=mid:1
        a=rtpmap:96 H264/90000
        """;

    [Fact]
    public void Detects_an_offer_with_no_video_m_line()
    {
        Assert.False(SdpMediaInspector.HasSendingVideo(AudioOnlyOffer));
        Assert.True(SdpMediaInspector.HasSendingAudio(AudioOnlyOffer));
    }

    [Fact]
    public void Detects_an_offer_carrying_video()
    {
        Assert.True(SdpMediaInspector.HasSendingVideo(AudioAndVideoOffer));
        Assert.True(SdpMediaInspector.HasSendingAudio(AudioAndVideoOffer));
    }

    [Fact]
    public void A_rejected_media_section_does_not_count()
    {
        // Port zero is how a peer declines an m-line while keeping its position in the
        // BUNDLE ordering, so the section is present but carries nothing.
        var sdp = AudioAndVideoOffer.Replace(
            "m=video 9 UDP/TLS/RTP/SAVPF 96", "m=video 0 UDP/TLS/RTP/SAVPF 96");

        Assert.False(SdpMediaInspector.HasSendingVideo(sdp));
    }

    [Theory]
    [InlineData("a=inactive")]
    [InlineData("a=recvonly")]
    public void A_non_sending_direction_does_not_count(string direction)
    {
        // recvonly from Janus means it wants to receive, not send — nothing will arrive.
        var sdp = AudioAndVideoOffer.Replace("a=sendonly\na=mid:1", $"{direction}\na=mid:1");

        Assert.False(SdpMediaInspector.HasSendingVideo(sdp));
    }

    [Fact]
    public void A_section_without_a_direction_defaults_to_sending()
    {
        // SDP omits the direction when it is sendrecv, which still means media arrives.
        var sdp = AudioAndVideoOffer.Replace("a=sendonly\na=mid:1", "a=mid:1");

        Assert.True(SdpMediaInspector.HasSendingVideo(sdp));
    }

    [Fact]
    public void Directions_do_not_leak_between_sections()
    {
        // The audio section being inactive must not suppress the video section below it.
        var sdp = AudioAndVideoOffer.Replace("a=sendonly\na=mid:0", "a=inactive\na=mid:0");

        Assert.True(SdpMediaInspector.HasSendingVideo(sdp));
        Assert.False(SdpMediaInspector.HasSendingAudio(sdp));
    }

    [Fact]
    public void Handles_crlf_and_empty_input()
    {
        Assert.True(SdpMediaInspector.HasSendingVideo(AudioAndVideoOffer.Replace("\n", "\r\n")));

        Assert.False(SdpMediaInspector.HasSendingVideo(null));
        Assert.False(SdpMediaInspector.HasSendingVideo(string.Empty));
        Assert.False(SdpMediaInspector.HasSendingVideo("   "));
    }
}
