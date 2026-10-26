using System.Collections.Concurrent;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Janus;

/// <summary>
/// Owns one Janus VideoRoom membership: our publisher, one subscriber per remote feed,
/// and the event loop that routes Janus events to whichever of them they belong to.
/// <para>
/// This is the C# replacement for the Janus half of <c>webrtc-manager.js</c>. It does not
/// touch capture, encoding or rendering — encoded frames go in through
/// <see cref="SendVideo"/>/<see cref="SendAudio"/> and come out via
/// <see cref="RemoteFrameReceived"/>.
/// </para>
/// </summary>
public sealed class JanusVideoRoomClient : IAsyncDisposable
{
    private readonly JanusTransport _transport;
    private readonly JanusPeerFactory _factory;
    private readonly ILogger _logger;
    private readonly MediaSessionDescriptor _descriptor;

    private readonly ConcurrentDictionary<long, JanusSubscriber> _subscribersByFeed = new();
    private readonly ConcurrentDictionary<long, JanusSubscriber> _subscribersByHandle = new();

    /// <summary>Guards the check-then-create race when two events announce the same feed.</summary>
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);

    /// <summary>How often the receive-side congestion controller measures and re-advertises.</summary>
    private static readonly TimeSpan BandwidthInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Safety net for missed publisher events. Janus does not always notify subscribers
    /// when a publisher adds a stream, so the room is re-read periodically — the same
    /// fallback the Blazor client ran. Expressed in bandwidth ticks: reconciling costs
    /// Janus round trips and cannot run at the controller's rate.
    /// </summary>
    private const int ReconcileEveryTicks = 3;

    /// <summary>Video RTP clock, for turning RTCP jitter into milliseconds.</summary>
    private const double VideoClockRate = 90_000.0;

    private CancellationTokenSource? _reconcileCts;
    private Task? _reconcileTask;

    private JanusEventLoop? _eventLoop;
    private JanusPublisher? _publisher;
    private long _privateId;
    private int _disposed;

    public bool IsRunning => _eventLoop?.IsRunning == true;

    public long PrivateId => Interlocked.Read(ref _privateId);

    /// <summary>Live subscribers, for diagnostics.</summary>
    public IReadOnlyCollection<JanusSubscriber> Subscribers => _subscribersByFeed.Values.ToArray();

    /// <summary>A remote feed became available and a subscriber was created for it.</summary>
    public event Action<JanusSubscriber>? RemoteFeedAdded;

    /// <summary>A remote feed went away and its subscriber was torn down.</summary>
    public event Action<JanusSubscriber>? RemoteFeedRemoved;

    /// <summary>Depacketized media from a remote feed. See <see cref="JanusFrameHandler"/>.</summary>
    public event JanusFrameHandler? RemoteFrameReceived;

    /// <summary>Janus asked for a keyframe; the encoder must emit an IDR.</summary>
    public event Action? KeyframeRequested;

    /// <summary>
    /// The SFU reported on the video we are sending it. The engine's send-side controller
    /// runs on this: it is the only feedback our uplink gets.
    /// </summary>
    public event Action<OutboundQualityReport>? OutboundQualityReported;

    /// <summary>The Janus session is gone; the app must re-join to get new credentials.</summary>
    public event Action? SessionLost;

    public event Action<string>? ErrorOccurred;

    public JanusVideoRoomClient(
        MediaSessionDescriptor descriptor,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _descriptor = descriptor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _transport = new JanusTransport(descriptor.ServerBaseUrl, descriptor.AuthToken, httpClient);
        _factory = new JanusPeerFactory(descriptor.IceServersJson);
    }

    /// <summary>
    /// Publishes into the room and subscribes to everyone already there.
    /// </summary>
    /// <summary>
    /// Publishes into the room and subscribes to everyone already there.
    /// <para>
    /// The video dimensions are the largest this machine will send, and are advertised as
    /// such: an H.264 receiver configures its decoder from them.
    /// </para>
    /// </summary>
    public async Task StartAsync(
        VideoCodec videoCodec,
        bool withVideo,
        int videoWidth,
        int videoHeight,
        int frameRate,
        CancellationToken cancellationToken = default)
    {
        _publisher = new JanusPublisher(
            _transport,
            _factory,
            _descriptor.JanusSessionId,
            _descriptor.JanusHandleId,
            _descriptor.JanusRoomId,
            _logger);

        _publisher.KeyframeRequested += () => KeyframeRequested?.Invoke();
        _publisher.QualityReported += report => OutboundQualityReported?.Invoke(report);
        _publisher.PacedFramesDropped += () => PacedFramesDropped?.Invoke();

        // Publish first: joinandconfigure needs the inline response, so it has to happen
        // before the event loop takes ownership of the session's event queue.
        var existing = await _publisher.JoinAsync(
            _descriptor.UserId,
            _descriptor.UserDisplayName,
            videoCodec,
            withVideo,
            videoWidth,
            videoHeight,
            frameRate,
            cancellationToken);

        Interlocked.Exchange(ref _privateId, _publisher.PrivateId);

        _eventLoop = new JanusEventLoop(_transport, _descriptor.JanusSessionId, _logger);
        _eventLoop.EventReceived += HandleEvent;
        _eventLoop.SessionLost += () => SessionLost?.Invoke();
        _eventLoop.Start();

        // Only now subscribe to pre-existing publishers: their SDP offers arrive on the
        // event loop, which must already be running or the offers are lost.
        foreach (var peer in existing)
            await EnsureSubscriberAsync(peer.Publisher, peer.Display, cancellationToken);

        // The first interval is measured from here, not from when this object was built:
        // joining takes long enough that otherwise the opening sample would spread one
        // second of traffic over the whole setup and read as a nearly idle link.
        _measuredAt = _bandwidthClock.Elapsed;

        _reconcileCts = new CancellationTokenSource();
        _reconcileTask = Task.Run(() => ReconcileLoopAsync(_reconcileCts.Token), CancellationToken.None);
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(BandwidthInterval);
        var tick = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                UpdateReceiveBandwidth();

                if (++tick % ReconcileEveryTicks != 0)
                    continue;

                await ReconcileParticipantsAsync(cancellationToken);
                await HealSubscriptionsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Participant reconciliation loop stopped.");
        }
    }

    /// <summary>True once our publishing peer connection has completed ICE and DTLS.</summary>
    public bool IsPublisherConnected => _publisher?.IsConnected == true;

    /// <summary>
    /// Bandwidth advertised to each sender via REMB. Fixed rather than measured: a real
    /// estimator needs the delay-gradient signal from transport-wide CC, which is task 9.
    /// Until then a static ceiling is what stops senders idling at their floor, and it is
    /// deliberately generous — the sender still adapts downwards on real loss.
    /// </summary>
    public int MaximumReceiveBandwidthBps
    {
        get => _downlink.MaximumBps;
        set => _downlink.SetCeiling(value);
    }

    /// <summary>What the receive-side controller currently believes the downlink carries.</summary>
    public int EstimatedReceiveBandwidthBps => _downlink.CurrentBps;

    /// <summary>Why the controller last moved, for the health log.</summary>
    public string ReceiveBandwidthReason => _downlink.LastReason;

    /// <summary>
    /// One controller for the whole downlink, not one per feed.
    /// <para>
    /// Every feed arrives over the same link from the same SFU, so per-feed estimators
    /// would each independently conclude they may have the whole path — which is exactly
    /// how N participants came to demand N times the bitrate. The total is estimated once
    /// and shared out.
    /// </para>
    /// <para>
    /// It opens just below the ceiling rather than at it. The initial estimate is used
    /// before anything has been measured, and granting the full ceiling from the first
    /// packet invites a burst into a link of unknown capacity — the queue builds, packets
    /// drop, and the controller answers with a hard cut. One step of growth closes the
    /// gap, so the caution costs about a second.
    /// </para>
    /// </summary>
    private readonly BandwidthEstimator _downlink = new(
        startBps: 16_000_000,
        minimumBps: 300_000,
        maximumBps: 20_000_000);

    /// <summary>
    /// Floor for one feed's share. Below roughly this, video stops being video, so a
    /// crowded room hands out slightly more in total rather than starving every tile.
    /// </summary>
    private const int MinimumFeedBps = 120_000;

    private readonly Dictionary<long, FeedCounters> _previousFeedCounters = [];
    private readonly System.Diagnostics.Stopwatch _bandwidthClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _measuredAt;
    private int _loggedEstimateBps;

    private readonly record struct FeedCounters(long Bytes, long Packets, long ReportSequence);

    /// <summary>
    /// Measures the downlink, folds it into the estimate, and hands each sender its share.
    /// <para>
    /// Senders treat REMB as a lease and decay their estimate when it stops arriving, so
    /// this repeats for as long as the call lasts even when the number does not move.
    /// </para>
    /// </summary>
    private void UpdateReceiveBandwidth()
    {
        var now = _bandwidthClock.Elapsed;
        var elapsed = now - _measuredAt;
        _measuredAt = now;

        if (elapsed <= TimeSpan.Zero)
            return;

        long bytesDelta = 0;
        long packetsDelta = 0;
        double lossWeightedByPackets = 0;
        double worstJitterMs = 0;
        var fresh = false;
        var videoFeeds = 0;

        foreach (var subscriber in _subscribersByFeed.Values)
        {
            var previous = _previousFeedCounters.TryGetValue(subscriber.FeedId, out var stored)
                ? stored
                : default;

            var current = new FeedCounters(
                subscriber.VideoBytesReceived,
                subscriber.VideoRtpPackets,
                subscriber.VideoReportSequence);

            _previousFeedCounters[subscriber.FeedId] = current;

            var feedPackets = Math.Max(0, current.Packets - previous.Packets);

            bytesDelta += Math.Max(0, current.Bytes - previous.Bytes);
            packetsDelta += feedPackets;

            // Loss is weighted by traffic: a quiet feed dropping packets says much less
            // about a shared downlink than the busiest one does. Summed this way the
            // result is total lost over total expected — the link's own loss rate.
            lossWeightedByPackets += subscriber.VideoLossPercent / 100.0 * feedPackets;
            worstJitterMs = Math.Max(worstJitterMs, subscriber.VideoJitter / VideoClockRate * 1000);

            if (current.ReportSequence != previous.ReportSequence)
                fresh = true;

            if (subscriber.HasNegotiatedVideo)
                videoFeeds++;
        }

        // Forget feeds that have gone, or the dictionary grows for the life of the call.
        if (_previousFeedCounters.Count > _subscribersByFeed.Count)
        {
            foreach (var feedId in _previousFeedCounters.Keys.ToArray())
            {
                if (!_subscribersByFeed.ContainsKey(feedId))
                    _previousFeedCounters.Remove(feedId);
            }
        }

        if (videoFeeds == 0)
            return;

        var total = _downlink.Update(new BandwidthSample
        {
            ObservedBps = (int)Math.Min(int.MaxValue, bytesDelta * 8 / Math.Max(elapsed.TotalSeconds, 0.001)),
            LossFraction = packetsDelta > 0 ? lossWeightedByPackets / packetsDelta : 0,
            JitterMs = worstJitterMs,
            ReportIsFresh = fresh,
            Elapsed = elapsed
        });

        var share = Math.Max(MinimumFeedBps, total / videoFeeds);

        foreach (var subscriber in _subscribersByFeed.Values)
            subscriber.SendBandwidthEstimate((uint)share);

        LogEstimateChanges(total, share, videoFeeds);
    }

    /// <summary>
    /// Logs the estimate when it has moved enough to matter. At one sample a second an
    /// unconditional line would bury everything else in the log.
    /// </summary>
    private void LogEstimateChanges(int total, int share, int videoFeeds)
    {
        if (_loggedEstimateBps != 0 && Math.Abs(total - _loggedEstimateBps) < _loggedEstimateBps * 0.15)
            return;

        _loggedEstimateBps = total;

        _logger.LogInformation(
            "Receive bandwidth: {Total} kbps across {Feeds} video feed(s) = {Share} kbps each ({Reason}).",
            total / 1000, videoFeeds, share / 1000, _downlink.LastReason);
    }

    /// <summary>Returns false when the frame was dropped because we are not connected.</summary>
    public bool SendVideo(ReadOnlySpan<byte> annexBFrame, uint rtpTimestamp)
        => _publisher?.SendVideo(annexBFrame, rtpTimestamp) == true;

    public bool SendAudio(ReadOnlySpan<byte> opusPacket, uint rtpTimestamp)
        => _publisher?.SendAudio(opusPacket, rtpTimestamp) == true;

    /// <summary>Re-caps our send bitrate, e.g. after an REMB update.</summary>
    public Task SetBitrateAsync(int bitrateBps, bool requestKeyframe = false, CancellationToken cancellationToken = default)
        => _publisher?.ConfigureAsync(bitrateBps, requestKeyframe, cancellationToken) ?? Task.CompletedTask;

    /// <summary>Rate the outbound packet pacer drains at.</summary>
    public void SetPacingRate(int bitsPerSecond) => _publisher?.SetPacingRate(bitsPerSecond);

    /// <summary>Outbound pacer queue, dropped frames and retransmissions, for the health log.</summary>
    public (int Queued, long Dropped, long Resent) PacerState => _publisher?.PacerState ?? (0, 0, 0);

    /// <summary>
    /// The pacer dropped frames because the encoder was producing more than the link
    /// carries. Receivers have lost their reference chain and need a keyframe.
    /// </summary>
    public event Action? PacedFramesDropped;

    /// <summary>Asks a remote feed's publisher for a keyframe (PLI through Janus).</summary>
    public bool RequestKeyframe(long feedId)
        => _subscribersByFeed.TryGetValue(feedId, out var subscriber) && subscriber.RequestKeyframe();

    /// <summary>
    /// Reconciles against <c>listparticipants</c>. Janus occasionally drops a publisher
    /// event, so the JS client polled this as a safety net; the same applies here.
    /// </summary>
    public async Task ReconcileParticipantsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JanusRemotePublisher> participants;
        try
        {
            participants = await _transport.ListParticipantsAsync(_descriptor.JanusRoomId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "listparticipants failed; keeping current feed set.");
            return;
        }

        LogParticipantStreams(participants);

        var live = new HashSet<long>();

        foreach (var participant in participants)
        {
            var display = PublisherDisplay.Parse(participant.Display);
            if (IsSelf(display))
                continue;

            live.Add(participant.Id);
            await EnsureSubscriberAsync(participant, display, cancellationToken);
        }

        foreach (var feedId in _subscribersByFeed.Keys)
        {
            if (!live.Contains(feedId))
                RemoveSubscriber(feedId);
        }
    }

    /// <summary>
    /// What each publisher is known to be sending, accumulated across events.
    /// <para>
    /// Deliberately sticky. Janus's <c>listparticipants</c> carries no stream list at all,
    /// so a reconcile pass that overwrote this would erase what the publisher events told
    /// us and leave every feed looking like it has no video.
    /// </para>
    /// </summary>
    private sealed class PublisherStreamHint
    {
        public bool HasVideo;
        public bool HasAudio;
        public List<JanusSubscribeStream> Streams = [];

        /// <summary>Debounce, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
        public long NextHealAt;
    }

    private readonly ConcurrentDictionary<long, PublisherStreamHint> _streamHints = new();

    /// <summary>How long to leave a re-subscribe to take effect before trying again.</summary>
    private static readonly TimeSpan HealInterval = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Cap on blind whole-feed recoveries per feed. Unlike the hint-driven heal, the
    /// forced path cannot know whether there is anything to recover, so it must not
    /// renegotiate a genuinely video-less feed forever.
    /// </summary>
    private const int MaxForcedVideoRecoveries = 3;

    private readonly ConcurrentDictionary<long, int> _forcedRecoveries = new();

    /// <summary>
    /// Merges what a publisher event says about a feed into the sticky hint.
    /// <para>
    /// An empty stream list means "no information", not "no media": writing a hint from
    /// one would fabricate "this publisher has neither audio nor video" out of an absent
    /// field, and that false negative is permanent — it reads exactly like a publisher
    /// who genuinely sends nothing, which switches the repair paths off for good. Absent
    /// stays absent so recovery can tell "unknown" from "known to have none".
    /// </para>
    /// </summary>
    private void UpdateStreamHint(JanusRemotePublisher publisher)
    {
        if (publisher.Streams.Count == 0)
            return;

        var hint = _streamHints.GetOrAdd(publisher.Id, _ => new PublisherStreamHint());

        var hasVideo = publisher.Streams.Any(s => s.IsVideo && !s.Disabled);
        var hasAudio = publisher.Streams.Any(s => s.IsAudio && !s.Disabled);

        if (hasVideo != hint.HasVideo || hasAudio != hint.HasAudio)
        {
            _logger.LogInformation(
                "Feed {FeedId} now advertises audio={HasAudio} video={HasVideo}.",
                publisher.Id, hasAudio, hasVideo);
        }

        hint.HasVideo = hasVideo;
        hint.HasAudio = hasAudio;
        hint.Streams = BuildSubscribeStreams(publisher);
    }

    /// <summary>
    /// Re-subscribes to feeds whose negotiated media does not match what is known about
    /// the publisher.
    /// <para>
    /// Two distinct failures land here. A publisher event told us there is video but our
    /// subscription negotiated none — heal by re-requesting the advertised mids. Or no
    /// stream list was ever learned at all, which is the normal case when the publisher
    /// started sending before we arrived (nothing about them changes after we join, so no
    /// event ever fires): heal blind, with the whole feed, a bounded number of times. A
    /// feed whose hint says video=false is deliberately left alone — that is a real
    /// camera-off participant, and renegotiating them repeatedly buys nothing.
    /// </para>
    /// </summary>
    private async Task HealSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;

        foreach (var subscriber in _subscribersByFeed.Values)
        {
            if (!subscriber.IsNegotiated)
                continue;

            // Video is arriving: nothing to heal, and the blind-recovery budget resets so
            // a later genuine loss gets its full allowance again.
            if (subscriber.VideoRtpPackets > 0)
            {
                _forcedRecoveries.TryRemove(subscriber.FeedId, out _);
                continue;
            }

            if (subscriber.HasNegotiatedVideo)
                continue;

            var hasHint = _streamHints.TryGetValue(subscriber.FeedId, out var hint);

            if (hasHint && hint!.HasVideo)
            {
                if (now < hint.NextHealAt)
                    continue;

                hint.NextHealAt = now + (long)HealInterval.TotalMilliseconds;

                try
                {
                    _logger.LogInformation(
                        "Feed {FeedId}: publisher advertises video but none was negotiated; healing.",
                        subscriber.FeedId);

                    await subscriber.ResubscribeAsync(hint.Streams, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not heal feed {FeedId}.", subscriber.FeedId);
                }
            }
            else if (!hasHint)
            {
                var attempts = _forcedRecoveries.GetValueOrDefault(subscriber.FeedId);
                if (attempts >= MaxForcedVideoRecoveries)
                    continue;

                _forcedRecoveries[subscriber.FeedId] = attempts + 1;

                try
                {
                    _logger.LogInformation(
                        "Feed {FeedId}: no video and no stream hint; forced recovery {Attempt}/{Max}.",
                        subscriber.FeedId, attempts + 1, MaxForcedVideoRecoveries);

                    await subscriber.ForceResubscribeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Forced recovery failed for feed {FeedId}.", subscriber.FeedId);
                }
            }

            // hasHint && !hint.HasVideo: known camera-off. Leave them alone.
        }
    }

    private string _lastParticipantSignature = string.Empty;

    /// <summary>
    /// Logs what Janus says each publisher is offering, but only when it changes.
    /// <para>
    /// A feed we subscribed to audio-only looks identical to one whose video is simply
    /// not flowing, and the difference is entirely in this list. Polled every 3 s, so it
    /// is diffed rather than printed.
    /// </para>
    /// </summary>
    private void LogParticipantStreams(IReadOnlyList<JanusRemotePublisher> participants)
    {
        var signature = string.Join(" | ", participants.Select(p =>
            $"{p.Id}:{(p.Streams.Count == 0 ? "none" : string.Join(",", p.Streams.Select(s =>
                $"{s.Type}/{s.Codec ?? "?"}@mid{s.Mid ?? "?"}{(s.Disabled ? "(disabled)" : "")}")))}"));

        if (signature == _lastParticipantSignature)
            return;

        _lastParticipantSignature = signature;
        _logger.LogInformation("Janus room streams: {Signature}", signature);
    }

    private void HandleEvent(JanusEvent evt)
    {
        // JSEP can accompany any event kind, so it is handled before the type switch.
        if (evt.Jsep is { } jsep)
            HandleJsep(evt.HandleId, jsep);

        switch (evt)
        {
            case JanusPublishersEvent publishers:
                if (publishers.PrivateId is { } privateId && privateId > 0)
                    Interlocked.Exchange(ref _privateId, privateId);

                foreach (var publisher in publishers.Publishers)
                {
                    var display = PublisherDisplay.Parse(publisher.Display);
                    if (IsSelf(display))
                        continue;

                    // Publisher events are the only place Janus reports stream lists, so
                    // this is where the hint is learned.
                    UpdateStreamHint(publisher);

                    _ = EnsureSubscriberAsync(publisher, display, CancellationToken.None);
                }
                break;

            case JanusPublisherGoneEvent gone:
                RemoveSubscriber(gone.FeedId);
                break;

            case JanusErrorEvent error:
                _logger.LogWarning("Janus error {Code}: {Reason}", error.Code, error.Reason);
                ErrorOccurred?.Invoke($"{error.Reason} (code {error.Code})");
                break;

            case JanusWebRtcStateEvent state:
                _logger.LogDebug("Handle {HandleId} webrtc {State}.", state.HandleId, state.Up ? "up" : "down");
                break;
        }
    }

    private void HandleRemoteFrame(
        JanusSubscriber subscriber,
        bool isAudio,
        ReadOnlySpan<byte> frame,
        uint rtpTimestamp,
        VideoCodec videoCodec,
        int audioChannels)
        => RemoteFrameReceived?.Invoke(subscriber, isAudio, frame, rtpTimestamp, videoCodec, audioChannels);

    private void HandleJsep(long handleId, Jsep jsep)
    {
        // An answer on the publisher handle completes a renegotiation we started.
        if (handleId == _descriptor.JanusHandleId)
        {
            _publisher?.ApplyAnswer(jsep);
            return;
        }

        if (!_subscribersByHandle.TryGetValue(handleId, out var subscriber))
        {
            _logger.LogWarning("JSEP for unknown handle {HandleId}; ignoring.", handleId);
            return;
        }

        _ = ApplyOfferSafeAsync(subscriber, jsep);
    }

    private async Task ApplyOfferSafeAsync(JanusSubscriber subscriber, Jsep offer)
    {
        try
        {
            await subscriber.ApplyOfferAsync(offer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer offer for feed {FeedId}.", subscriber.FeedId);
            ErrorOccurred?.Invoke($"Could not start remote stream: {ex.Message}");
        }
    }

    private async Task EnsureSubscriberAsync(
        JanusRemotePublisher publisher,
        PublisherDisplay display,
        CancellationToken cancellationToken)
    {
        UpdateStreamHint(publisher);

        // Already subscribed: the publisher may have added a stream since — a camera
        // switched on mid-call — and Janus only ever offers the streams that were asked
        // for, so the new one has to be requested explicitly.
        if (_subscribersByFeed.TryGetValue(publisher.Id, out var existing))
        {
            try
            {
                await existing.TryAddStreamsAsync(BuildSubscribeStreams(publisher), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not subscribe to new streams on feed {FeedId}.", publisher.Id);
            }

            return;
        }

        await _subscribeGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: the event loop and the reconcile timer can both
            // announce the same publisher, and attaching twice leaks a Janus handle.
            if (_subscribersByFeed.ContainsKey(publisher.Id))
                return;

            var handleId = await _transport.AttachHandleAsync(_descriptor.JanusSessionId, cancellationToken);

            var subscriber = new JanusSubscriber(
                _transport,
                _factory,
                _descriptor.JanusSessionId,
                handleId,
                _descriptor.JanusRoomId,
                publisher.Id,
                display,
                _logger);

            subscriber.FrameReceived += HandleRemoteFrame;

            _subscribersByFeed[publisher.Id] = subscriber;
            _subscribersByHandle[handleId] = subscriber;

            var streams = BuildSubscribeStreams(publisher);
            var wantAudio = publisher.Streams.Any(s => s.IsAudio && !s.Disabled);

            subscriber.RecordSubscribedStreams(streams);
            await subscriber.JoinAsync(streams, PrivateId, wantAudio, cancellationToken);

            _logger.LogInformation(
                "Subscribing to feed {FeedId} ({Display}) on handle {HandleId}: {Streams}.",
                publisher.Id,
                display.DisplayName,
                handleId,
                publisher.Streams.Count == 0
                    ? "legacy (no stream list)"
                    : string.Join(", ", publisher.Streams.Select(s =>
                        $"{s.Type}/{s.Codec ?? "?"}@mid{s.Mid ?? "?"}{(s.Disabled ? " (disabled)" : "")}")));

            RemoteFeedAdded?.Invoke(subscriber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to feed {FeedId}.", publisher.Id);

            // Roll back so a later announcement can retry rather than being suppressed
            // by a half-registered entry.
            RemoveSubscriber(publisher.Id);
            ErrorOccurred?.Invoke($"Could not subscribe to a participant: {ex.Message}");
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    /// <summary>
    /// Selects which of a publisher's streams to receive. Disabled streams are skipped;
    /// Janus errors on a subscribe naming them.
    /// </summary>
    private static List<JanusSubscribeStream> BuildSubscribeStreams(JanusRemotePublisher publisher)
    {
        var streams = publisher.Streams
            .Where(s => !s.Disabled && (s.IsAudio || s.IsVideo))
            .Select(s => new JanusSubscribeStream { Feed = publisher.Id, Mid = s.Mid })
            .ToList();

        // Pre-multistream Janus advertises no stream list; the bare feed id means
        // "everything this publisher has".
        if (streams.Count == 0)
            streams.Add(new JanusSubscribeStream { Feed = publisher.Id });

        return streams;
    }

    private void RemoveSubscriber(long feedId)
    {
        if (!_subscribersByFeed.TryRemove(feedId, out var subscriber))
            return;

        _subscribersByHandle.TryRemove(subscriber.HandleId, out _);

        // A Janus feed id is minted per publish, so a removed feed never returns and its
        // recovery state is dead weight.
        _streamHints.TryRemove(feedId, out _);
        _forcedRecoveries.TryRemove(feedId, out _);

        try
        {
            RemoteFeedRemoved?.Invoke(subscriber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoteFeedRemoved handler threw for feed {FeedId}.", feedId);
        }

        subscriber.Dispose();
        _logger.LogInformation("Removed feed {FeedId}.", feedId);
    }

    private bool IsSelf(PublisherDisplay display)
    {
        if (display.UserId != _descriptor.UserId)
            return false;

        // Our own screen share is a separate publisher we legitimately subscribe to for
        // local preview; only the primary feed would loop our own audio back.
        return !display.IsScreenShare;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        if (_reconcileCts is not null)
        {
            await _reconcileCts.CancelAsync();

            if (_reconcileTask is not null)
            {
                try
                {
                    await _reconcileTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { }
            }

            _reconcileCts.Dispose();
            _reconcileCts = null;
            _reconcileTask = null;
        }

        if (_eventLoop is not null)
        {
            _eventLoop.EventReceived -= HandleEvent;
            await _eventLoop.DisposeAsync();
            _eventLoop = null;
        }

        foreach (var feedId in _subscribersByFeed.Keys.ToArray())
        {
            if (_subscribersByFeed.TryRemove(feedId, out var subscriber))
            {
                _subscribersByHandle.TryRemove(subscriber.HandleId, out _);
                subscriber.Dispose();
            }
        }

        if (_publisher is not null)
        {
            try
            {
                await _publisher.UnpublishAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unpublish on shutdown failed; the session is being torn down anyway.");
            }

            _publisher.Dispose();
            _publisher = null;
        }

        _subscribeGate.Dispose();
        _transport.Dispose();
    }
}
