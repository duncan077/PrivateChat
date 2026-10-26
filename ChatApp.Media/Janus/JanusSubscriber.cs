using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ChatApp.Media.Janus;

/// <summary>
/// Receives one complete, already-depacketized frame from a remote feed.
/// <para>
/// SIPSorcery reassembles RTP for us, so handlers get whole access units rather than
/// fragments. The buffer is only valid for the duration of the call.
/// </para>
/// </summary>
public delegate void JanusFrameHandler(
    JanusSubscriber subscriber,
    bool isAudio,
    ReadOnlySpan<byte> frame,
    uint rtpTimestamp,
    VideoCodec videoCodec,
    int audioChannels);

/// <summary>
/// One inbound feed: a dedicated Janus handle plus a recvonly peer connection.
/// <para>
/// Janus offers, we answer — the reverse of the publisher. Every subscriber gets its own
/// handle because Janus scopes subscription state per handle.
/// </para>
/// </summary>
public sealed class JanusSubscriber : IDisposable
{
    private readonly JanusTransport _transport;
    private readonly JanusPeerFactory _factory;
    private readonly long _sessionId;
    private readonly long _roomId;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <summary>Mids we have already asked Janus for on this feed.</summary>
    private readonly HashSet<string> _subscribedMids = [];

    private RTCPeerConnection? _pc;
    private JanusIceTrickler? _trickler;
    private int _disposed;

    public long HandleId { get; }
    public long FeedId { get; }
    public PublisherDisplay Display { get; }

    /// <summary>True once Janus has offered and we have answered.</summary>
    public bool IsNegotiated { get; private set; }

    /// <summary>
    /// Codec negotiated for this feed's video, reported by SIPSorcery rather than assumed
    /// from our own preference — the sender chooses it independently.
    /// </summary>
    public VideoCodec VideoCodec { get; private set; } = VideoCodec.H264;

    /// <summary>Channel count negotiated for this feed's audio.</summary>
    public int AudioChannels { get; private set; } = 1;

    /// <summary>
    /// Whether Janus's offer actually contained a video m-line.
    /// <para>
    /// Janus sometimes omits one even though the publisher is sending video — the offer
    /// reflects the streams the subscription asked for, and a publisher that added video
    /// after we joined is not retroactively included. Distinguishing this from "video
    /// negotiated but not arriving" is what tells a re-subscribe from a network problem.
    /// </para>
    /// </summary>
    public bool HasNegotiatedVideo { get; private set; }

    /// <summary>
    /// Raw RTP packets seen per media type, counted before depacketization.
    /// <para>
    /// Distinguishes "the sender is not transmitting" from "packets arrive but never
    /// reassemble into a frame" — indistinguishable from the frame callbacks alone.
    /// </para>
    /// </summary>
    public long AudioRtpPackets { get; private set; }

    public long VideoRtpPackets { get; private set; }

    /// <summary>
    /// Video bytes seen on the wire, counted before depacketization.
    /// <para>
    /// This is what the receive-side congestion controller measures against: how much is
    /// actually arriving decides whether raising the advertised ceiling would buy
    /// anything. Payload only — RTP headers are not counted, so it slightly understates
    /// what the link is carrying.
    /// </para>
    /// </summary>
    public long VideoBytesReceived { get; private set; }

    /// <summary>
    /// Loss and jitter as most recently reported back to the sender. These are the
    /// numbers the sender's congestion controller reacts to, so a collapsing inbound
    /// bitrate is explained here or not at all.
    /// </summary>
    public double VideoLossPercent { get; private set; }

    public uint VideoJitter { get; private set; }

    public int VideoPacketsLost { get; private set; }

    /// <summary>
    /// Counts reception reports emitted for this feed.
    /// <para>
    /// The controller runs faster than RTCP reports arrive, so it needs to tell a fresh
    /// measurement from the same one seen again; acting twice on one bad report cuts the
    /// target far further than the loss justified.
    /// </para>
    /// </summary>
    public long VideoReportSequence { get; private set; }

    public event JanusFrameHandler? FrameReceived;

    public JanusSubscriber(
        JanusTransport transport,
        JanusPeerFactory factory,
        long sessionId,
        long handleId,
        long roomId,
        long feedId,
        PublisherDisplay display,
        ILogger? logger = null)
    {
        _transport = transport;
        _factory = factory;
        _sessionId = sessionId;
        _roomId = roomId;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        HandleId = handleId;
        FeedId = feedId;
        Display = display;
    }

    /// <summary>
    /// Asks Janus to attach us to the feed. Fire-and-forget: the SDP offer arrives later
    /// on the poll loop and is applied by <see cref="ApplyOfferAsync"/>.
    /// </summary>
    public Task JoinAsync(
        IReadOnlyList<JanusSubscribeStream> streams,
        long privateId,
        bool wantAudio,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object>
        {
            ["request"] = "join",
            ["room"] = _roomId,
            ["ptype"] = "subscriber",
            ["feed"] = FeedId,
            ["streams"] = streams,
            ["offer_audio"] = wantAudio,
            ["offer_video"] = true,
            ["offer_data"] = false
        };

        // Janus rejects subscriber joins without a valid private_id once the room is
        // configured for it. Omitting it is better than sending zero, which is invalid.
        if (privateId > 0)
            body["private_id"] = privateId;
        else
            _logger.LogWarning("Subscribing to feed {FeedId} without a private_id.", FeedId);

        return _transport.SendMessageNoWaitAsync(_sessionId, HandleId, body, jsep: null, cancellationToken);
    }

    /// <summary>
    /// Re-sends the subscription so Janus re-offers with any media that was missing.
    /// <para>
    /// Unlike <see cref="TryAddStreamsAsync"/> this does not skip mids we have already
    /// asked for: the point is to recover from an offer that silently omitted an m-line,
    /// where by our own bookkeeping nothing is outstanding. Janus keys on mid, so
    /// re-requesting one we already have renegotiates rather than duplicating it.
    /// </para>
    /// </summary>
    public async Task<bool> ResubscribeAsync(
        IReadOnlyList<JanusSubscribeStream> streams,
        CancellationToken cancellationToken = default)
    {
        var wanted = streams.Where(stream => stream.Mid is not null).ToList();

        // Without mids the only expressible request is the whole feed again, which does
        // duplicate every track. Better to leave the feed as it is.
        if (wanted.Count == 0)
            return false;

        _logger.LogInformation(
            "Feed {FeedId}: re-subscribing to [{Mids}] to recover missing media.",
            FeedId, string.Join(", ", wanted.Select(s => s.Mid)));

        lock (_subscribedMids)
        {
            foreach (var stream in wanted)
                _subscribedMids.Add(stream.Mid!);
        }

        await SubscribeAsync(wanted, wantAudio: true, cancellationToken);
        return true;
    }

    /// <summary>
    /// Last-resort recovery: re-subscribes to the whole feed with no mid list.
    /// <para>
    /// For a feed whose stream list was never learned there is no mid to name, so the
    /// only expressible request is "everything this publisher has". Janus may answer by
    /// duplicating an m-line we already hold; our answer declines the duplicate (the
    /// local track set has one slot per kind), so the cost is a renegotiation round
    /// trip. The caller caps how often this is tried.
    /// </para>
    /// </summary>
    public Task ForceResubscribeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Feed {FeedId}: forced whole-feed re-subscribe to recover missing media.", FeedId);

        return SubscribeAsync(
            [new JanusSubscribeStream { Feed = FeedId }], wantAudio: true, cancellationToken);
    }

    /// <summary>Records the streams requested at join time.</summary>
    internal void RecordSubscribedStreams(IEnumerable<JanusSubscribeStream> streams)
    {
        lock (_subscribedMids)
        {
            foreach (var stream in streams)
            {
                if (stream.Mid is not null)
                    _subscribedMids.Add(stream.Mid);
            }
        }
    }

    /// <summary>
    /// Subscribes to streams that appeared after the initial join — typically the
    /// participant switching their camera on.
    /// <para>
    /// Janus multistream honours exactly the <c>streams</c> list it was given, so a video
    /// stream added later is never offered unless it is asked for explicitly. Streams
    /// without a mid are skipped: requesting one subscribes to the whole feed again and
    /// duplicates every track.
    /// </para>
    /// </summary>
    public async Task<bool> TryAddStreamsAsync(
        IReadOnlyList<JanusSubscribeStream> streams,
        CancellationToken cancellationToken = default)
    {
        List<JanusSubscribeStream> added;

        lock (_subscribedMids)
        {
            added = streams
                .Where(stream => stream.Mid is not null && !_subscribedMids.Contains(stream.Mid))
                .ToList();

            if (added.Count == 0)
                return false;

            foreach (var stream in added)
                _subscribedMids.Add(stream.Mid!);
        }

        _logger.LogInformation(
            "Feed {FeedId}: subscribing to {Count} new stream(s) [{Mids}].",
            FeedId, added.Count, string.Join(", ", added.Select(s => s.Mid)));

        await SubscribeAsync(added, wantAudio: true, cancellationToken);
        return true;
    }

    /// <summary>
    /// Tells the sender how much bandwidth we can accept.
    /// <para>
    /// Called on a timer rather than once: WebRTC senders treat REMB as a lease and fall
    /// back if it stops arriving. Silently does nothing until both SSRCs are known, which
    /// is only after the first RTP packet arrives.
    /// </para>
    /// </summary>
    public bool SendBandwidthEstimate(uint bitrateBps)
    {
        var peer = _pc;
        if (peer is null || peer.connectionState != RTCPeerConnectionState.connected)
            return false;

        var localSsrc = peer.VideoLocalTrack?.Ssrc ?? 0;
        var remoteSsrc = peer.VideoRemoteTrack?.Ssrc ?? 0;

        if (localSsrc == 0 || remoteSsrc == 0)
            return false;

        try
        {
            peer.SendRtcpRaw(
                SDPMediaTypesEnum.video, RtcpRemb.Build(localSsrc, remoteSsrc, bitrateBps));

            AdvertisedBitrateBps = bitrateBps;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feed {FeedId}: REMB could not be sent.", FeedId);
            return false;
        }
    }

    /// <summary>Bandwidth most recently advertised to the sender, 0 if none yet.</summary>
    public uint AdvertisedBitrateBps { get; private set; }

    /// <summary>
    /// Asks the publisher for a keyframe via RTCP PLI, relayed by Janus. Needed when we
    /// join mid-GOP, and when decode gating skipped frames and broke the reference
    /// chain. No-ops until both SSRCs are known from the first RTP.
    /// </summary>
    public bool RequestKeyframe()
    {
        var peer = _pc;
        if (peer is null || peer.connectionState != RTCPeerConnectionState.connected)
            return false;

        var localSsrc = peer.VideoLocalTrack?.Ssrc ?? 0;
        var remoteSsrc = peer.VideoRemoteTrack?.Ssrc ?? 0;

        if (localSsrc == 0 || remoteSsrc == 0)
            return false;

        try
        {
            peer.SendRtcpFeedback(
                SDPMediaTypesEnum.video,
                new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feed {FeedId}: PLI could not be sent.", FeedId);
            return false;
        }
    }

    /// <summary>Updates which of the publisher's streams we want.</summary>
    public Task SubscribeAsync(
        IReadOnlyList<JanusSubscribeStream> streams,
        bool wantAudio,
        CancellationToken cancellationToken = default)
        => _transport.SendMessageNoWaitAsync(
            _sessionId,
            HandleId,
            new
            {
                request = "subscribe",
                streams,
                offer_audio = wantAudio,
                offer_video = true,
                offer_data = false
            },
            jsep: null,
            cancellationToken);

    /// <summary>
    /// Applies Janus's offer, produces an answer and sends <c>start</c>.
    /// Also handles renegotiation, e.g. when the remote adds a screen-share stream.
    /// </summary>
    public async Task ApplyOfferAsync(Jsep offer, CancellationToken cancellationToken = default)
    {
        RTCPeerConnection peer;
        bool isRenegotiation;

        lock (_gate)
        {
            // Janus can deliver a renegotiation offer that was already in flight when the
            // feed went away.
            if (_disposed != 0)
            {
                _logger.LogDebug("Ignoring an offer for disposed feed {FeedId}.", FeedId);
                return;
            }

            isRenegotiation = _pc is not null;
            _pc ??= CreatePeerConnection();
            peer = _pc;
        }

        // Read from the offer, not from the peer connection: our own recvonly track makes
        // SIPSorcery report video as negotiated even when Janus offered none.
        HasNegotiatedVideo = SdpMediaInspector.HasSendingVideo(offer.Sdp);

        if (!HasNegotiatedVideo)
        {
            _logger.LogInformation(
                "Feed {FeedId}: Janus offered no sending video m-line.", FeedId);
        }

        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            sdp = offer.Sdp,
            type = RTCSdpType.offer
        });

        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogError("Feed {FeedId}: Janus offer rejected ({Result}).", FeedId, result);
            return;
        }

        var answer = peer.createAnswer();
        await peer.setLocalDescription(answer);

        // As with the publisher: answering before gathering finishes hands Janus nothing
        // but LAN addresses, leaving the connection dependent on our own checks crossing
        // the NAT unaided.
        var answerSdp = await JanusPeerFactory.DescribeWithCandidatesAsync(
            peer, answer.sdp, _logger, cancellationToken);

        await _transport.SendMessageNoWaitAsync(
            _sessionId,
            HandleId,
            new { request = "start", room = _roomId },
            Jsep.Answer(answerSdp),
            cancellationToken);

        // Janus now has our answer, so it will accept the candidates that were gathered
        // too late to appear in it — including the server-reflexive one it needs in order
        // to check towards us at all.
        if (_trickler is { } trickler)
            await trickler.ReleaseAsync(cancellationToken);

        IsNegotiated = true;

        _logger.LogInformation(
            "Subscriber {Action} for feed {FeedId} (handle {HandleId}).",
            isRenegotiation ? "renegotiated" : "started", FeedId, HandleId);
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        var peer = _factory.Create();

        _trickler = new JanusIceTrickler(_transport, _sessionId, HandleId, _logger);
        _trickler.Attach(peer);

        // Recvonly tracks must exist BEFORE setRemoteDescription. SIPSorcery answers an
        // offer by matching its m-lines against the tracks it has; with none added it
        // rejects the whole offer as NoMatchingMediaType and no media ever flows.
        peer.addTrack(new MediaStreamTrack(
            JanusPeerFactory.CreateOpusFormat(), MediaStreamStatusEnum.RecvOnly));

        // Offer every codec we can decode rather than only our publishing preference —
        // the sender picked theirs independently, and the answer keeps whichever matches.
        peer.addTrack(new MediaStreamTrack(
            JanusPeerFactory.CreateDecodableVideoFormats(), MediaStreamStatusEnum.RecvOnly));

        peer.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count == 0)
                return;

            VideoCodec = JanusPeerFactory.ToVideoCodec(formats[0].Codec);
            _logger.LogInformation("Feed {FeedId} negotiated video {Codec}.", FeedId, VideoCodec);
        };

        peer.OnAudioFormatsNegotiated += formats =>
        {
            if (formats.Count == 0)
                return;

            // WebRTC sends mono unless the fmtp carries stereo=1. The RFC-mandated "/2"
            // in the rtpmap is not the stream's channel count.
            var format = formats[0];
            AudioChannels =
                format.Parameters?.Contains("stereo=1", StringComparison.OrdinalIgnoreCase) == true ? 2 : 1;

            _logger.LogInformation(
                "Feed {FeedId} negotiated {Codec} {Channels}ch.", FeedId, format.Codec, AudioChannels);
        };

        // SIPSorcery reassembles RTP, so these deliver whole frames rather than fragments.
        peer.OnVideoFrameReceived += (_, timestamp, frame, format) =>
        {
            if (frame is { Length: > 0 })
            {
                FrameReceived?.Invoke(
                    this, isAudio: false, frame, timestamp,
                    JanusPeerFactory.ToVideoCodec(format.Codec), AudioChannels);
            }
        };

        peer.OnAudioFrameReceived += encoded =>
        {
            if (encoded.EncodedAudio is { Length: > 0 })
            {
                FrameReceived?.Invoke(
                    this, isAudio: true, encoded.EncodedAudio, 0, VideoCodec, AudioChannels);
            }
        };

        peer.OnRtpPacketReceived += (_, mediaType, packet) =>
        {
            if (mediaType == SDPMediaTypesEnum.video)
            {
                VideoRtpPackets++;
                VideoBytesReceived += packet?.Payload?.Length ?? 0;
            }
            else if (mediaType == SDPMediaTypesEnum.audio)
            {
                AudioRtpPackets++;
            }
        };

        // What we tell the sender about reception. A sender that drops from 1080p to
        // 960x516 is reacting to something, and this is the only place it can read: our
        // reported loss fraction and jitter.
        peer.OnSendReport += (mediaType, report) =>
        {
            if (mediaType != SDPMediaTypesEnum.video)
                return;

            var reception = report.ReceiverReport?.ReceptionReports?.FirstOrDefault()
                ?? report.SenderReport?.ReceptionReports?.FirstOrDefault();

            if (reception is null)
                return;

            VideoLossPercent = reception.FractionLost * 100.0 / 256.0;
            VideoJitter = reception.Jitter;
            VideoPacketsLost = reception.PacketsLost;
            VideoReportSequence++;

            _logger.LogDebug(
                "Feed {FeedId} reporting to sender: {Loss:F1}% loss this interval, " +
                "{Lost} lost total, jitter {Jitter}.",
                FeedId, VideoLossPercent, VideoPacketsLost, VideoJitter);
        };

        peer.onconnectionstatechange += state =>
            _logger.LogDebug("Subscriber feed {FeedId} connection state: {State}.", FeedId, state);

        peer.oniceconnectionstatechange += state =>
            _logger.LogDebug("Subscriber feed {FeedId} ICE connection: {State}.", FeedId, state);

        return peer;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        lock (_gate)
        {
            if (_pc is not null)
            {
                _pc.close();
                _pc.Dispose();
                _pc = null;
            }
        }
    }
}
