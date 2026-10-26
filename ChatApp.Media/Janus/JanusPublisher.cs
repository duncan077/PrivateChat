using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ChatApp.Media.Janus;

/// <summary>
/// Our outbound half of the room: one peer connection carrying a sendonly Opus track and
/// a sendonly video track, negotiated with Janus via <c>joinandconfigure</c>.
/// <para>
/// Nothing here captures or encodes. Encoded frames are pushed in through
/// <see cref="SendVideo"/>/<see cref="SendAudio"/>; SIPSorcery packetizes them into RTP
/// and owns ICE, DTLS and SRTP.
/// </para>
/// </summary>
public sealed class JanusPublisher : IDisposable
{
    private readonly JanusTransport _transport;
    private readonly JanusPeerFactory _factory;
    private readonly long _sessionId;
    private readonly long _handleId;
    private readonly long _roomId;
    private readonly ILogger _logger;

    private RTCPeerConnection? _pc;
    private JanusIceTrickler? _trickler;
    private MediaStreamTrack? _audioTrack;
    private MediaStreamTrack? _videoTrack;
    private RtpPacer? _pacer;
    private bool _remoteDescriptionSet;
    private int _disposed;

    /// <summary>Payload type Janus accepted for our video, known once formats are negotiated.</summary>
    private int _videoPayloadType = -1;

    /// <summary>Running RTP timestamp for the video stream, advanced per frame.</summary>
    private uint _videoTimestamp;

    /// <summary>
    /// Whether the packets for our own video are produced here rather than by SIPSorcery.
    /// <para>
    /// Only H.264, and only when <see cref="PacingEnabled"/>: it is what this app
    /// negotiates in practice, and hand-writing a packetizer for a codec nobody is using
    /// would be risk without benefit. Anything else takes the library's path, which works
    /// — it simply cannot be paced.
    /// </para>
    /// </summary>
    private bool _pacedVideo;

    /// <summary>
    /// Whether to packetize and pace video here instead of handing whole frames to
    /// SIPSorcery.
    /// <para>
    /// On, because a real call showed the average rate was never the problem. A screen
    /// share alternates between near-silence and a scene change that emits one very large
    /// frame; handed straight to the socket, that frame goes out as hundreds of packets
    /// back to back at whatever rate the card manages. A log window averaging 2.8 Mbps
    /// reported 5.9% loss on a path measured to carry 5 Mbps — an average that low cannot
    /// itself cause loss, so it was being done by the bursts inside it. Spreading them is
    /// the only thing that addresses that, and it is what every other WebRTC stack does.
    /// </para>
    /// <para>
    /// The cost is owning RFC 6184, sequence numbers and ordering, where a mistake
    /// produces a stream that decodes nowhere — which looks exactly like the network fault
    /// it was built to fix. <c>CHATFLOW_PACE_VIDEO=0</c> goes back to handing whole frames
    /// to the library, without a rebuild, if that is ever the better guess.
    /// </para>
    /// </summary>
    private static bool PacingEnabled =>
        Environment.GetEnvironmentVariable("CHATFLOW_PACE_VIDEO") != "0";

    /// <summary>Our publisher id in the room, echoed back by Janus.</summary>
    public long PrivateId { get; private set; }

    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;

    /// <summary>
    /// Raised when a receiver asks for a keyframe (RTCP PLI). The encoder must respond by
    /// forcing an IDR — without this, a joining subscriber sees nothing until the next
    /// scheduled keyframe.
    /// </summary>
    public event Action? KeyframeRequested;

    /// <summary>
    /// What Janus reports about the video we are sending it.
    /// <para>
    /// This is the only congestion signal the send side has. It covers the leg between us
    /// and the SFU, which is the leg we can do something about — loss further downstream
    /// is the receiving client's own REMB to report.
    /// </para>
    /// </summary>
    public event Action<OutboundQualityReport>? QualityReported;

    public JanusPublisher(
        JanusTransport transport,
        JanusPeerFactory factory,
        long sessionId,
        long handleId,
        long roomId,
        ILogger? logger = null)
    {
        _transport = transport;
        _factory = factory;
        _sessionId = sessionId;
        _handleId = handleId;
        _roomId = roomId;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Builds the offer, joins the room as a publisher and applies Janus's answer.
    /// </summary>
    public async Task<IReadOnlyList<JanusRemotePeer>> JoinAsync(
        Guid userId,
        string displayName,
        VideoCodec videoCodec,
        bool withVideo,
        int videoWidth,
        int videoHeight,
        int frameRate,
        CancellationToken cancellationToken = default)
    {
        _pc = _factory.Create();

        _audioTrack = new MediaStreamTrack(
            JanusPeerFactory.CreateOpusFormat(), MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(_audioTrack);

        if (withVideo)
        {
            var format = JanusPeerFactory.CreateVideoFormat(videoCodec, videoWidth, videoHeight, frameRate);

            _logger.LogInformation(
                "Publishing {Codec} for up to {Width}x{Height}@{Fps} as '{Fmtp}'.",
                videoCodec, videoWidth, videoHeight, frameRate, format.Parameters);

            _videoTrack = new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly);
            _pc.addTrack(_videoTrack);
        }

        _pc.onconnectionstatechange += state =>
            _logger.LogInformation("Publisher connection state: {State}.", state);

        // What Janus actually accepted for our outbound video. If this differs from what
        // the encoder is producing, receivers get undecodable frames and show black.
        _pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count == 0)
                return;

            _videoPayloadType = formats[0].FormatID;

            _pacedVideo = PacingEnabled
                && formats[0].Codec == SIPSorceryMedia.Abstractions.VideoCodecsEnum.H264;

            _logger.LogInformation(
                "Publisher negotiated outbound video {Codec} (pt {PayloadType}), sending {Path}.",
                formats[0].Codec,
                _videoPayloadType,
                _pacedVideo ? "paced from our own packetizer" : "whole frames via SIPSorcery");
        };

        // PLI/FIR from any receiver means someone needs an IDR to start decoding.
        _pc.OnReceiveReport += HandleReceiveReport;

        // Attached before the offer so no candidate is missed; it holds them until Janus
        // has our SDP.
        _trickler = new JanusIceTrickler(_transport, _sessionId, _handleId, _logger);
        _trickler.Attach(_pc);

        _pc.onicegatheringstatechange += state =>
            _logger.LogDebug("Publisher ICE gathering: {State}.", state);

        _pc.oniceconnectionstatechange += state =>
            _logger.LogDebug("Publisher ICE connection: {State}.", state);

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);

        // Wait for the server-reflexive candidate before offering. Without it Janus sees
        // only our LAN address and can never open a check towards us.
        var offerSdp = await JanusPeerFactory.DescribeWithCandidatesAsync(
            _pc, offer.sdp, _logger, cancellationToken);

        // SIPSorcery offers only transport-cc. Without nack in our offer, nothing ever
        // asks us to resend a lost packet, and one lost packet costs the frame it was part
        // of plus everything referencing it until the next keyframe.
        offerSdp = SdpFeedback.AddVideoFeedback(offerSdp);

        // The candidate lines on both sides are the only way to tell "we advertised
        // nowhere reachable" from "we advertised fine and the checks were refused".
        LogSdp("offer sent to Janus", offerSdp);

        var body = new Dictionary<string, object>
        {
            ["request"] = "joinandconfigure",
            ["room"] = _roomId,
            ["ptype"] = "publisher",
            ["display"] = PublisherDisplay.Encode(userId, displayName, PublisherDisplay.RolePrimary)
        };

        if (withVideo)
            body["videocodec"] = JanusPeerFactory.ToJanusCodecName(videoCodec);

        // The publisher handshake uses the waiting variant: the answer is needed inline,
        // and this runs before the poll loop owns the event queue.
        var response = await _transport.SendMessageAsync(
            _sessionId, _handleId, body, Jsep.Offer(offerSdp), cancellationToken);

        var parsed = JanusEventParser.Parse(response);

        if (parsed is JanusErrorEvent error)
            throw new InvalidOperationException($"Janus rejected joinandconfigure: {error.Reason} (code {error.Code}).");

        if (parsed is JanusPublishersEvent publishers && publishers.PrivateId is { } privateId)
            PrivateId = privateId;

        ApplyAnswer(response);

        // Only now: Janus discards trickle for a handle that has not seen our offer.
        await _trickler.ReleaseAsync(cancellationToken);

        _logger.LogInformation(
            "Published to room {RoomId} as {DisplayName} (privateId={PrivateId}, codec={Codec}, video={WithVideo}).",
            _roomId, displayName, PrivateId, videoCodec, withVideo);

        return ExtractExistingPublishers(parsed, userId);
    }

    private void HandleReceiveReport(
        System.Net.IPEndPoint endpoint,
        SDPMediaTypesEnum mediaType,
        RTCPCompoundPacket report)
    {
        if (mediaType != SDPMediaTypesEnum.video)
            return;

        var reception = report.ReceiverReport?.ReceptionReports?.FirstOrDefault()
            ?? report.SenderReport?.ReceptionReports?.FirstOrDefault();

        if (reception is not null)
        {
            // FractionLost is an 8-bit fixed-point fraction; jitter is in RTP units, which
            // for video is the 90 kHz clock.
            QualityReported?.Invoke(new OutboundQualityReport(
                LossFraction: reception.FractionLost / 256.0,
                JitterMs: reception.Jitter / 90_000.0 * 1000,
                PacketsLost: reception.PacketsLost));
        }

        if (report.Feedback is null)
            return;

        // A Generic NACK names packets that never arrived. We do not offer NACK and cannot
        // safely answer one — see SdpFeedback for why resending on this SSRC breaks the
        // stream outright — so it is counted and ignored rather than acted on.
        if (report.Feedback.Header.FeedbackMessageType == RTCPFeedbackTypesEnum.NACK)
        {
            _logger.LogDebug(
                "A NACK arrived for packet {Id} although NACK was not offered; ignoring it.",
                report.Feedback.PID);

            return;
        }

        // PLI and FIR both mean "I cannot decode, send an IDR". The payload-specific
        // feedback type lives in the RTCP header's FeedbackMessageType.
        var feedbackType = report.Feedback.Header.PayloadFeedbackMessageType;

        if (feedbackType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
        {
            _logger.LogDebug("Receiver requested a keyframe ({Type}).", feedbackType);
            KeyframeRequested?.Invoke();
        }
    }

    /// <summary>
    /// Hands an encoded video frame to the RTP layer. Returns false when the peer
    /// connection is not up, so the caller can tell "nothing encoded" from "encoded and
    /// thrown away".
    /// <para>
    /// H.264 is packetized here and queued for the pacer; everything else goes to
    /// SIPSorcery, which sends the whole frame at once.
    /// </para>
    /// </summary>
    public bool SendVideo(ReadOnlySpan<byte> annexBFrame, uint durationRtpUnits)
    {
        var pc = _pc;
        if (pc is null || _videoTrack is null || pc.connectionState != RTCPeerConnectionState.connected)
            return false;

        if (!_pacedVideo || _videoPayloadType < 0)
        {
            pc.SendVideo(durationRtpUnits, annexBFrame.ToArray());
            return true;
        }

        var packets = H264RtpPacketizer.Packetize(annexBFrame);
        if (packets.Count == 0)
            return false;

        // The timestamp is per frame, and every packet of the frame carries it. SIPSorcery
        // keeps its own bookkeeping inside SendRtpRaw, which is the same call its whole-
        // frame path ends up making.
        _videoTimestamp += durationRtpUnits;

        EnsurePacer(pc);
        return _pacer?.Enqueue(new PacedFrame(packets, _videoTimestamp, _videoPayloadType)) == true;
    }

    /// <summary>Raised when the pacer had to drop frames; the encoder owes a keyframe.</summary>
    public event Action? PacedFramesDropped;

    /// <summary>
    /// Paces at this rate, from the send-side congestion controller.
    /// <para>
    /// Kept even when there is no pacer yet: it is built on the first frame, which arrives
    /// after the rate has already been decided. Without this the first second of a share
    /// would be paced at the default rather than the negotiated one, and a share opening
    /// at 8 Mbps would drop most of its opening frames — indistinguishable from the burst
    /// loss the pacer exists to prevent.
    /// </para>
    /// </summary>
    public void SetPacingRate(int bitsPerSecond)
    {
        _pacingBps = bitsPerSecond;

        if (_pacer is { } pacer)
            pacer.TargetBps = bitsPerSecond;
    }

    private int _pacingBps = 1_000_000;

    /// <summary>Queue depth and dropped frames, for the health log.</summary>
    public (int Queued, long Dropped, long Resent) PacerState => (
        _pacer?.QueuedFrames ?? 0,
        _pacer?.DroppedFrames ?? 0,
        0);

    private void EnsurePacer(RTCPeerConnection pc)
    {
        if (_pacer is not null)
            return;

        _pacer = new RtpPacer(
            (packet, timestamp, marker, payloadType) =>
            {
                // The sequence number is taken from the track rather than left to the
                // overload that omits it: a receiver reassembles fragments by sequence, so
                // if that argument were not being filled in for us every packet would
                // arrive claiming to be the same one. Owning it is also what makes a
                // retransmission possible at all — the far end asks by sequence number.
                var sequence = _videoTrack?.GetNextSeqNum() ?? 0;

                pc.SendRtpRaw(
                    SDPMediaTypesEnum.video, packet, timestamp, marker ? 1 : 0, payloadType, sequence);
            },
            _logger);

        _pacer.FramesDropped += () => PacedFramesDropped?.Invoke();
        _pacer.TargetBps = _pacingBps;

        _logger.LogInformation(
            "Pacing outbound video at {Kbps} kbps: packets are spread rather than burst.",
            _pacingBps / 1000);
    }

    /// <summary>Hands an encoded Opus packet to the RTP packetizer.</summary>
    public bool SendAudio(ReadOnlySpan<byte> opusPacket, uint durationRtpUnits)
    {
        var pc = _pc;
        if (pc is null || _audioTrack is null || pc.connectionState != RTCPeerConnectionState.connected)
            return false;

        pc.SendAudio(durationRtpUnits, opusPacket.ToArray());
        return true;
    }

    /// <summary>
    /// Asks Janus to re-cap our sending bitrate and optionally force a keyframe.
    /// Called when the user changes quality settings or REMB moves.
    /// </summary>
    public Task ConfigureAsync(int bitrateBps, bool requestKeyframe, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object>
        {
            ["request"] = "configure",
            ["bitrate"] = bitrateBps
        };

        if (requestKeyframe)
            body["keyframe"] = true;

        return _transport.SendMessageNoWaitAsync(_sessionId, _handleId, body, jsep: null, cancellationToken);
    }

    public Task UnpublishAsync(CancellationToken cancellationToken = default)
        => _transport.SendMessageNoWaitAsync(
            _sessionId, _handleId, new { request = "unpublish" }, jsep: null, cancellationToken);

    /// <summary>Applies the JSEP answer carried by a Janus response.</summary>
    public void ApplyAnswer(System.Text.Json.JsonElement response)
    {
        if (_remoteDescriptionSet)
            return;

        // The answer rides alongside plugindata, so read it off the base event rather
        // than expecting a JSEP-only event.
        if (JanusEventParser.Parse(response).Jsep is { } jsep)
        {
            ApplyAnswer(jsep);
            return;
        }

        _logger.LogWarning("joinandconfigure returned no JSEP answer; publisher will not connect.");
    }

    public void ApplyAnswer(Jsep jsep)
    {
        if (_remoteDescriptionSet || _pc is null)
            return;

        LogSdp("answer from Janus", jsep.Sdp);

        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            sdp = jsep.Sdp,
            type = jsep.Type.Equals("offer", StringComparison.OrdinalIgnoreCase)
                ? RTCSdpType.offer
                : RTCSdpType.answer
        });

        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogError("Janus answer was rejected: {Result}.", result);
            return;
        }

        _remoteDescriptionSet = true;
    }

    /// <summary>
    /// Summarises an SDP's ICE candidates. The full SDP goes out at Debug; the candidate
    /// count is promoted to Information because an offer or answer carrying none is the
    /// difference between a call that connects and one that times out 16 s later.
    /// </summary>
    private void LogSdp(string label, string? sdp)
    {
        if (string.IsNullOrEmpty(sdp))
        {
            _logger.LogWarning("Publisher {Label}: empty SDP.", label);
            return;
        }

        var candidates = sdp
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("a=candidate:", StringComparison.Ordinal))
            .ToList();

        var kinds = candidates
            .Select(line => line.Split(' ') is { Length: > 7 } parts ? parts[7] : "?")
            .GroupBy(kind => kind)
            .Select(group => $"{group.Key}={group.Count()}");

        _logger.LogInformation(
            "Publisher {Label}: {Count} ICE candidate(s) [{Kinds}].",
            label, candidates.Count, string.Join(", ", kinds));

        _logger.LogDebug("Publisher {Label} SDP:\n{Sdp}", label, sdp);
    }

    private static IReadOnlyList<JanusRemotePeer> ExtractExistingPublishers(JanusEvent parsed, Guid selfUserId)
    {
        if (parsed is not JanusPublishersEvent publishersEvent)
            return [];

        var result = new List<JanusRemotePeer>();

        foreach (var publisher in publishersEvent.Publishers)
        {
            var display = PublisherDisplay.Parse(publisher.Display);

            // Our own primary feed is echoed back; subscribing to it would loop our audio.
            // A native screen-share feed from the same user is legitimate to subscribe to.
            if (display.UserId == selfUserId && !display.IsScreenShare)
                continue;

            result.Add(new JanusRemotePeer(publisher, display));
        }

        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Before the peer connection: the pacer's thread sends through it.
        _pacer?.Dispose();
        _pacer = null;

        if (_pc is not null)
        {
            _pc.OnReceiveReport -= HandleReceiveReport;
            _pc.close();
            _pc.Dispose();
            _pc = null;
        }

        _audioTrack = null;
        _videoTrack = null;
    }
}

/// <summary>
/// One RTCP reception report about our outbound video, as the SFU sees it.
/// </summary>
public readonly record struct OutboundQualityReport(
    double LossFraction,
    double JitterMs,
    int PacketsLost);

/// <summary>A remote publisher discovered during our own join.</summary>
public sealed record JanusRemotePeer(JanusRemotePublisher Publisher, PublisherDisplay Display)
{
    public long FeedId => Publisher.Id;
}
