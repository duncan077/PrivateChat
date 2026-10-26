using System.Text.Json;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ChatApp.Media.Janus;

/// <summary>
/// Builds SIPSorcery peer connections configured the way Janus expects, and translates
/// between the app's codec enum and SIPSorcery's media formats.
/// </summary>
public sealed class JanusPeerFactory
{
    /// <summary>Payload types matching what the Janus videoroom plugin negotiates.</summary>
    public const int OpusPayloadType = 111;
    public const int VideoPayloadType = 96;

    public const int OpusClockRate = 48_000;
    public const int VideoClockRate = 90_000;

    private readonly List<RTCIceServer> _iceServers;

    public JanusPeerFactory(string iceServersJson)
    {
        _iceServers = ParseIceServers(iceServersJson);
    }

    public IReadOnlyList<RTCIceServer> IceServers => _iceServers;

    public RTCPeerConnection Create() => new(new RTCConfiguration
    {
        iceServers = _iceServers,

        // RTP/SAVPF. Without the feedback profile the SDP advertises no RTCP feedback
        // mechanisms, and a sender with no feedback throttles itself to its floor.
        X_UseRtpFeedbackProfile = true
    });

    /// <summary>
    /// Converts the server's browser-shaped ICE JSON into SIPSorcery's typed form.
    /// <para>
    /// Credentials are separate fields here rather than embedded in the URL, so the
    /// percent-encoding the previous transport needed is unnecessary.
    /// </para>
    /// </summary>
    internal static List<RTCIceServer> ParseIceServers(string? iceServersJson)
    {
        var fallback = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        };

        if (string.IsNullOrWhiteSpace(iceServersJson))
            return fallback;

        try
        {
            using var document = JsonDocument.Parse(iceServersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return fallback;

            var result = new List<RTCIceServer>();

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var username = GetString(entry, "username");
                var credential = GetString(entry, "credential");

                foreach (var url in EnumerateUrls(entry))
                {
                    var server = new RTCIceServer { urls = url };

                    // STUN never carries credentials.
                    if (!url.StartsWith("stun", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(username)
                        && !string.IsNullOrEmpty(credential))
                    {
                        server.username = username;
                        server.credential = credential;
                        server.credentialType = RTCIceCredentialType.password;
                    }

                    result.Add(server);
                }
            }

            return result.Count > 0 ? result : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static IEnumerable<string> EnumerateUrls(JsonElement entry)
    {
        if (!entry.TryGetProperty("urls", out var urls))
        {
            if (entry.TryGetProperty("url", out var single) && single.ValueKind == JsonValueKind.String)
            {
                var value = single.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }

            yield break;
        }

        switch (urls.ValueKind)
        {
            case JsonValueKind.String:
                var text = urls.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
                break;

            case JsonValueKind.Array:
                foreach (var item in urls.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var itemText = item.GetString();
                    if (!string.IsNullOrWhiteSpace(itemText))
                        yield return itemText;
                }
                break;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Opus as WebRTC negotiates it: 48 kHz, two channels declared per RFC 7587, with
    /// in-band FEC. Stereo is deliberately not requested — browsers send mono unless
    /// <c>stereo=1</c> is present, and matching that keeps the decoder configuration
    /// aligned with what actually arrives.
    /// </summary>
    public static AudioFormat CreateOpusFormat() => new(
        AudioCodecsEnum.OPUS,
        OpusPayloadType,
        OpusClockRate,
        channelCount: 2,
        parameters: "useinbandfec=1");

    /// <summary>
    /// The format to publish with.
    /// <para>
    /// The H.264 fmtp describes the stream this machine will actually produce, at the
    /// resolution and frame rate it is configured for — see <see cref="H264ProfileLevel"/>
    /// for why advertising a convenient constant instead is the difference between a
    /// receiver decoding and a receiver stopping after a second.
    /// </para>
    /// </summary>
    public static VideoFormat CreateVideoFormat(VideoCodec codec, int width, int height, int frameRate)
        => codec switch
        {
            VideoCodec.H265 => new VideoFormat(VideoCodecsEnum.H265, VideoPayloadType, VideoClockRate),
            VideoCodec.Av1 => new VideoFormat(VideoCodecsEnum.AV1, VideoPayloadType, VideoClockRate),
            VideoCodec.Vp9 => new VideoFormat(VideoCodecsEnum.VP9, VideoPayloadType, VideoClockRate),
            VideoCodec.Vp8 => new VideoFormat(VideoCodecsEnum.VP8, VideoPayloadType, VideoClockRate),

            _ => new VideoFormat(
                VideoCodecsEnum.H264,
                VideoPayloadType,
                VideoClockRate,
                H264ProfileLevel.SendFmtp(width, height, frameRate))
        };

    /// <summary>
    /// Every video codec this build can decode, in preference order, for the recvonly
    /// side of a subscription.
    /// <para>
    /// The answer keeps whichever entry matches the sender's offer, so listing all of
    /// them is what lets one subscriber handle an H.264 camera and a VP8 screen share.
    /// </para>
    /// <para>
    /// Every payload type here must be in the dynamic range, 96–127. Chrome happens to
    /// use 45 for AV1, but 45 is in the static range that RFC 3551 assigns by profile, and
    /// SIPSorcery drops it while parsing with "Excluding unrecognised well known media
    /// format ID 45" — taking the rest of the m-line's format list with it.
    /// </para>
    /// </summary>
    public static List<VideoFormat> CreateDecodableVideoFormats() =>
    [
        new(VideoCodecsEnum.H264, 102, VideoClockRate, H264ProfileLevel.ReceiveFmtp()),
        new(VideoCodecsEnum.VP8, 96, VideoClockRate),
        new(VideoCodecsEnum.VP9, 98, VideoClockRate),
        new(VideoCodecsEnum.H265, 100, VideoClockRate),
        new(VideoCodecsEnum.AV1, 104, VideoClockRate)
    ];

    /// <summary>Maps a negotiated SIPSorcery format back onto the app's codec enum.</summary>
    public static VideoCodec ToVideoCodec(VideoCodecsEnum codec) => codec switch
    {
        VideoCodecsEnum.H265 => VideoCodec.H265,
        VideoCodecsEnum.AV1 => VideoCodec.Av1,
        VideoCodecsEnum.VP9 => VideoCodec.Vp9,
        VideoCodecsEnum.VP8 => VideoCodec.Vp8,
        _ => VideoCodec.H264
    };

    /// <summary>
    /// How long to wait for ICE gathering before giving up and sending what we have.
    /// Gathering normally finishes in well under 100 ms; the cap only matters when the
    /// STUN/TURN server is unreachable, and a call that starts is better than one that
    /// hangs waiting.
    /// </summary>
    public static readonly TimeSpan IceGatheringTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waits for ICE gathering to finish and returns the local SDP including every
    /// candidate found.
    /// <para>
    /// <c>createOffer</c> and <c>createAnswer</c> snapshot the SDP immediately, before the
    /// STUN round trip that discovers the server-reflexive address has completed — a gap
    /// measured at ~65 ms. Sending that snapshot tells Janus about nothing but LAN
    /// addresses, so Janus can never open a connectivity check towards us, and the call
    /// depends entirely on our own checks getting through the NAT.
    /// </para>
    /// <para>
    /// The alternative is trickling candidates to Janus as they arrive. Waiting is chosen
    /// because it costs one round trip once, has no ordering hazard against the handle
    /// being ready, and needs no extra signalling path.
    /// </para>
    /// </summary>
    public static async Task<string> DescribeWithCandidatesAsync(
        RTCPeerConnection peer,
        string fallbackSdp,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (peer.iceGatheringState != RTCIceGatheringState.complete)
        {
            var gathered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void HandleStateChange(RTCIceGatheringState state)
            {
                if (state == RTCIceGatheringState.complete)
                    gathered.TrySetResult();
            }

            peer.onicegatheringstatechange += HandleStateChange;

            try
            {
                // Re-checked inside the subscription: gathering can complete between the
                // test above and the handler being attached, and the event would be lost.
                if (peer.iceGatheringState != RTCIceGatheringState.complete)
                    await gathered.Task.WaitAsync(IceGatheringTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "ICE gathering did not complete within {Timeout}s; sending the candidates " +
                    "gathered so far.",
                    IceGatheringTimeout.TotalSeconds);
            }
            finally
            {
                peer.onicegatheringstatechange -= HandleStateChange;
            }
        }

        return peer.localDescription?.sdp?.ToString() ?? fallbackSdp;
    }

    /// <summary>Janus's <c>videocodec</c> string for a codec preference.</summary>
    public static string ToJanusCodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.H265 => "h265",
        VideoCodec.Av1 => "av1",
        VideoCodec.Vp9 => "vp9",
        _ => "vp8"
    };
}
