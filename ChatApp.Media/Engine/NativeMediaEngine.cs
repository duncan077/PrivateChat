using System.Collections.Concurrent;
using ChatApp.Core.Media;
using ChatApp.Media.Audio;
using ChatApp.Media.Capture;
using ChatApp.Media.Codec;
using ChatApp.Media.Janus;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Engine;

/// <summary>
/// The native replacement for <c>webrtc-manager.js</c>: capture → encode → RTP → Janus
/// outbound, and Janus → decode → mix → playback inbound.
/// <para>
/// Threading. Capture callbacks arrive on device threads and are encoded inline, because
/// handing frames to a queue would add latency and the encoders are already fast enough
/// to keep up. Janus events arrive on the poll loop thread. Playback is driven by its own
/// 20 ms pump so mixing stays independent of packet arrival timing.
/// </para>
/// </summary>
public sealed class NativeMediaEngine : IMediaEngine, IVideoSourceProvider
{
    private static readonly TimeSpan PlaybackInterval = TimeSpan.FromMilliseconds(20);

    private readonly ICaptureBackendFactory _backends;
    private readonly VideoEncoderPolicy _policy;
    private readonly ILogger _logger;
    private readonly Lock _stateGate = new();

    private readonly AudioMixer _mixer = new();
    private readonly AudioFrameAssembler _assembler = new();
    private readonly ConcurrentDictionary<long, RemoteFeed> _feeds = new();
    private readonly short[] _playbackFrame = new short[OpusEncoder.FrameSizeSamples * OpusEncoder.Channels];

    // Voice activity, one detector per participant plus one for ourselves.
    private readonly SpeakingDetector _localSpeaking = new();
    private readonly ConcurrentDictionary<Guid, SpeakingDetector> _remoteSpeaking = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Local capture echoed to the UI. Separate sources per origin: camera and screen have
    // different resolutions, and one source flipping between them would resize the
    // renderer's bitmap on every frame.
    private LocalVideoSource? _localCamera;
    private LocalVideoSource? _localScreen;

    private JanusVideoRoomClient? _janus;
    private IAudioCapture? _microphone;
    private IAudioPlayback? _playback;
    private IVideoCapture? _camera;
    private IScreenCapture? _screen;
    private IMediaDeviceEnumerator? _audioDevices;

    private AudioResampler? _resampler;
    private OpusEncoder? _opusEncoder;
    private VideoEncoder? _videoEncoder;

    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;

    private MediaEngineSettings _settings = new();
    private MediaSessionDescriptor? _descriptor;
    private VideoCodec _negotiatedCodec = VideoCodec.H264;
    private uint _videoRtpTimestamp;

    // Send-side counters. Without these there is no way to distinguish "the camera is
    // not capturing" from "frames are captured but never encoded or sent".
    private long _videoFramesCaptured;
    private long _videoFramesEncoded;
    private long _videoPacketsSent;
    private long _videoBytesSent;

    /// <summary>RTP units the outbound media clock has advanced. See the health report.</summary>
    private long _videoRtpTicksSent;

    private long _previousRtpTicksSent;
    private TimeSpan _previousRtpClockAt;
    private long _previousFramesCaptured;
    private long _previousFramesEncoded;
    private long _previousPacketsSent;
    private long _previousBytesSent;
    private int _disposed;

    public bool IsRunning { get; private set; }
    public bool IsMuted { get; private set; } = true;
    public bool IsCameraOff { get; private set; } = true;
    public bool IsScreenSharing { get; private set; }

    public event Action<bool>? MuteChanged;
    public event Action<bool>? CameraChanged;
    public event Action<bool>? ScreenShareChanged;
    public event Action<bool>? PushToTalkChanged;
    public event Action<IReadOnlyList<MediaDeviceInfo>>? DevicesChanged;
    public event Action<RemoteStream>? RemoteStreamAdded;
    public event Action<RemoteStream>? RemoteStreamRemoved;
    public event Action<Guid, bool>? SpeakingChanged;
    public event Action? SessionLost;
    public event Action<string>? ErrorOccurred;

    public event Action<IVideoSource>? SourceAdded;
    public event Action<IVideoSource>? SourceRemoved;

    public IVideoSource? LocalPreview => _localCamera;

    public IVideoSource? LocalScreenShare => _localScreen;

    public IReadOnlyList<IVideoSource> RemoteSources => _feeds.Values.Cast<IVideoSource>().ToArray();

    public NativeMediaEngine(
        ICaptureBackendFactory backends,
        VideoEncoderPolicy policy,
        ILogger? logger = null)
    {
        _backends = backends;
        _policy = policy;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task StartAsync(
        MediaSessionDescriptor descriptor,
        MediaEngineSettings settings,
        IReadOnlyDictionary<string, string> participantDisplayNames,
        IReadOnlyDictionary<string, string> participantAvatarUrls,
        CancellationToken cancellationToken = default)
    {
        FFmpegLoader.EnsureAvailable();

        lock (_stateGate)
        {
            if (IsRunning)
                throw new InvalidOperationException("The media engine is already running.");

            _descriptor = descriptor;
            _settings = settings;
            _negotiatedCodec = settings.PreferredVideoCodec;
        }

        try
        {
            StartOutboundAudio();
            StartPlayback();

            _janus = new JanusVideoRoomClient(descriptor, _logger);
            _janus.RemoteFeedAdded += HandleRemoteFeedAdded;
            _janus.RemoteFeedRemoved += HandleRemoteFeedRemoved;
            _janus.RemoteFrameReceived += HandleRemoteFrame;
            _janus.KeyframeRequested += HandleKeyframeRequested;
            _janus.OutboundQualityReported += HandleOutboundQuality;
            _janus.PacedFramesDropped += HandlePacedFramesDropped;
            _janus.SessionLost += () => SessionLost?.Invoke();

            StartSendBandwidthControl();
            _janus.ErrorOccurred += message => ErrorOccurred?.Invoke(message);

            // The video m-line is negotiated up front even with the camera off, because
            // adding a track later requires a full renegotiation with Janus. The track
            // simply carries no frames until the camera starts — standard WebRTC practice
            // and what the Blazor client did.
            // The configured resolution is the largest anything will send: the camera
            // captures at it, and a share is capped to it. That is what the H.264 level
            // has to cover, because a receiver sizes its decoder from what we advertise.
            await _janus.StartAsync(
                _negotiatedCodec,
                withVideo: true,
                _settings.Width,
                _settings.Height,
                _settings.FrameRate,
                cancellationToken);

            // Janus pins one video codec per publisher, so everyone in the room must be
            // able to decode our choice or they see a black tile with no error anywhere.
            // H.264 is the only codec every WebRTC endpoint is required to carry.
            if (_negotiatedCodec != VideoCodec.H264)
            {
                _logger.LogWarning(
                    "Publishing {Codec} video: participants whose client cannot decode it " +
                    "will show a black tile. H264 is the universally-supported choice.",
                    _negotiatedCodec);
            }

            IsRunning = true;
            _logger.LogInformation("Media engine running for room {RoomId}.", descriptor.JanusRoomId);
        }
        catch
        {
            await StopAsync("start-failed", CancellationToken.None);
            throw;
        }
    }

    private void StartOutboundAudio()
    {
        _resampler = new AudioResampler();
        _opusEncoder = OpusEncoder.Create(logger: _logger);

        _microphone = _backends.CreateMicrophoneCapture();
        _microphone.SamplesCaptured += HandleMicrophoneSamples;
        _microphone.CaptureFailed += message => ErrorOccurred?.Invoke($"Microphone: {message}");

        _microphone.StartAsync(_settings.MicrophoneDeviceId).GetAwaiter().GetResult();
        _assembler.Reset();
    }

    private void StartPlayback()
    {
        _playback = _backends.CreatePlayback();
        _playback.Volume = _settings.PlaybackVolume;
        _playback.StartAsync(_settings.SpeakerDeviceId).GetAwaiter().GetResult();

        _playbackCts = new CancellationTokenSource();
        _playbackTask = Task.Run(() => PlaybackPumpAsync(_playbackCts.Token));
    }

    /// <summary>
    /// Drives mixing on a steady 20 ms cadence rather than on packet arrival, so one
    /// participant's jitter cannot disturb everyone else's playback timing.
    /// </summary>
    private async Task PlaybackPumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlaybackInterval);

        long ticks = 0;
        long written = 0;
        var nextReport = TimeSpan.FromSeconds(5);
        var nextBandwidthTick = SendBandwidthInterval;
        var nextQualityTick = ShareQualityInterval;
        var nextCaptureCheck = CaptureCheckInterval;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ticks++;

                if (_mixer.MixInto(_playbackFrame))
                {
                    written++;
                    _playback?.Write(_playbackFrame);
                }

                // The send-side controller rides this heartbeat rather than owning a timer
                // of its own. It runs for the life of the call, which is exactly the window
                // congestion control needs, and a second's worth of work here is a lock and
                // some arithmetic.
                if (_clock.Elapsed >= nextBandwidthTick)
                {
                    nextBandwidthTick = _clock.Elapsed + SendBandwidthInterval;
                    UpdateSendBandwidth();
                }

                if (_clock.Elapsed >= nextQualityTick)
                {
                    nextQualityTick = _clock.Elapsed + ShareQualityInterval;
                    UpdateShareQuality();
                }

                if (_clock.Elapsed >= nextCaptureCheck)
                {
                    nextCaptureCheck = _clock.Elapsed + CaptureCheckInterval;
                    CheckScreenCaptureAlive();
                }

                // Periodic snapshot: without it, "the audio sounds wrong" is impossible
                // to attribute between the network, the mixer and the output device.
                if (_clock.Elapsed >= nextReport)
                {
                    nextReport = _clock.Elapsed + TimeSpan.FromSeconds(5);
                    ReportPlaybackHealth(ticks, written);
                    ticks = 0;
                    written = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playback pump failed.");
            ErrorOccurred?.Invoke($"Audio playback stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs what actually reached the speakers over the last window: how many pump ticks
    /// produced audio, and each source's buffer depth and underrun count.
    /// </summary>
    private void ReportPlaybackHealth(long ticks, long written)
    {
        var sources = _mixer.GetDiagnostics();

        if (sources.Count == 0)
        {
            // Nothing to play. If audio is audible in this state it is not coming from
            // the mixer, which narrows the search considerably.
            _logger.LogDebug("Playback: {Ticks} ticks, no remote sources — writing silence.", ticks);

            // Video reporting must not hang off having remote audio: a call where nobody
            // else is heard is exactly when the outbound video path needs explaining.
            ReportVideoHealth();
            ReportOutboundVideoHealth();
            return;
        }

        // Samples/second in vs out is the decisive measurement: a source starved at a
        // constant ratio means the network is delivering less than the pump drains, which
        // no amount of buffering can fix. Expected is 48000 * channels per second.
        var expectedPerSecond = OpusEncoder.SampleRate * OpusEncoder.Channels;

        var detail = string.Join(", ", sources.Select(s =>
        {
            var inRate = (s.SubmittedSamples - GetPrevious(s.FeedId, submitted: true)) / 5.0;
            var outRate = (s.ConsumedSamples - GetPrevious(s.FeedId, submitted: false)) / 5.0;
            RememberRates(s);

            // Packet rate and samples-per-packet, measured at the point libdatachannel
            // hands frames over — before any decode or channel conversion.
            var feed = _feeds.TryGetValue(s.FeedId, out var matched) ? matched : null;
            var packets = feed?.AudioPacketsReceived ?? 0;
            var decoded = feed?.AudioSamplesDecoded ?? 0;
            var previousPackets = GetPreviousPackets(s.FeedId);
            var packetRate = (packets - previousPackets.Packets) / 5.0;
            var samplesPerPacket = packets > previousPackets.Packets
                ? (decoded - previousPackets.Samples) / (double)(packets - previousPackets.Packets)
                : 0;
            RememberPackets(s.FeedId, packets, decoded);

            return $"{s.UserId.ToString()[..8]}/{s.FeedId % 100000} buf={s.BufferedSamples / (double)OpusEncoder.Channels / 48.0:F0}ms " +
                   $"underruns={s.Underruns} in={inRate:F0}/s out={outRate:F0}/s " +
                   $"({inRate / expectedPerSecond * 100:F0}% of expected) " +
                   $"pkts={packetRate:F0}/s samples/pkt={samplesPerPacket:F0} gain={s.Gain:F2}";
        }));

        _logger.LogInformation(
            "Playback: {Written}/{Ticks} ticks produced audio. Sources: {Detail}",
            written, ticks, detail);

        ReportVideoHealth();
        ReportOutboundVideoHealth();
    }

    /// <summary>Reports what our own camera path produced over the last window.</summary>
    private void ReportOutboundVideoHealth()
    {
        var captured = Interlocked.Read(ref _videoFramesCaptured);
        if (captured == 0)
            return;

        var encoded = Interlocked.Read(ref _videoFramesEncoded);
        var packets = Interlocked.Read(ref _videoPacketsSent);
        var bytes = Interlocked.Read(ref _videoBytesSent);

        var capturedDelta = captured - _previousFramesCaptured;
        var encodedDelta = encoded - _previousFramesEncoded;
        var packetsDelta = packets - _previousPacketsSent;
        var bytesDelta = bytes - _previousBytesSent;

        _previousFramesCaptured = captured;
        _previousFramesEncoded = encoded;
        _previousPacketsSent = packets;
        _previousBytesSent = bytes;

        var connected = _janus?.IsPublisherConnected == true;
        var pacer = _janus?.PacerState ?? (Queued: 0, Dropped: 0L, Resent: 0L);

        _logger.LogInformation(
            "Outbound video: {Captured} fps captured -> {Encoded} encoded/s -> {Sent} sent/s, " +
            "{Kbps} kbps ({Encoder}), target {Target} kbps of {Ceiling} ({Reason}), publisher={State}, " +
            "pacer queue={Queued} dropped={Dropped}, resent={Resent}.",
            capturedDelta / 5,
            encodedDelta / 5,
            packetsDelta / 5,
            bytesDelta * 8 / 5 / 1000,
            _videoEncoder?.EncoderName ?? "none",
            (_uplink?.CurrentBps ?? 0) / 1000,
            (_uplink?.MaximumBps ?? 0) / 1000,
            _uplink?.LastReason ?? "no controller",
            connected ? "connected" : "NOT CONNECTED",
            pacer.Queued,
            pacer.Dropped,
            pacer.Resent);

        // Encoding into a dead transport is the failure mode the old counter hid: it
        // reported a healthy bitrate for frames that never left the machine.
        if (encodedDelta > 0 && packetsDelta == 0)
        {
            _logger.LogWarning(
                "Outbound video is being encoded but not sent — the publisher peer " +
                "connection is not connected. Nothing is reaching the room.");
        }

        // A share that captured nothing is the failure this engine used to report as a
        // perfectly healthy stream, so it is called out by name — but only where frames
        // were owed. A backend that sends on change owes nothing to a still screen.
        if (IsScreenSharing && capturedDelta == 0 && _captureWatchdog.SilenceIsSuspicious)
        {
            _logger.LogWarning(
                "Screen capture produced no frames at all in the last window. The share is " +
                "frozen at the source, not on the network.");
        }

        var restarts = _captureWatchdog.Restarts;
        if (restarts > _previousCaptureRestarts)
        {
            _logger.LogWarning(
                "Screen capture was restarted {Count} time(s) in the last window.",
                restarts - _previousCaptureRestarts);

            _previousCaptureRestarts = restarts;
        }

        // The stream's own clock against the wall clock. A receiver schedules playout from
        // the RTP timestamps, so a ratio below 1.00 means it is being told less time passed
        // than really did: frames arrive, none are lost, and the picture still stops.
        var ticks = Interlocked.Read(ref _videoRtpTicksSent);
        var clockNow = _clock.Elapsed;
        var clockElapsed = (clockNow - _previousRtpClockAt).TotalSeconds;

        if (clockElapsed > 0 && ticks > _previousRtpTicksSent)
        {
            var advanced = ticks - _previousRtpTicksSent;
            var ratio = advanced / (clockElapsed * VideoEncoder.ClockRate);

            _logger.Log(
                Math.Abs(ratio - 1.0) > 0.05 ? LogLevel.Warning : LogLevel.Information,
                "Media clock: advanced {Advanced} ticks in {Seconds:F1}s = {Ratio:F3}x real time " +
                "(1.000 is correct; below that a receiver stalls while still receiving every frame).",
                advanced, clockElapsed, ratio);
        }

        _previousRtpTicksSent = ticks;
        _previousRtpClockAt = clockNow;

        // Receivers asking for keyframes faster than we will emit them means someone is
        // failing to decode continuously, which no amount of keyframes will fix.
        var suppressed = Interlocked.Read(ref _keyframesSuppressed);
        if (suppressed > _previousKeyframesSuppressed)
        {
            _logger.LogWarning(
                "Keyframe requests are arriving faster than one every {Seconds}s: {Count} refused in " +
                "the last window. A receiver is not decoding, and more keyframes are not the answer.",
                MinimumKeyframeInterval.TotalSeconds,
                suppressed - _previousKeyframesSuppressed);

            _previousKeyframesSuppressed = suppressed;
        }

        ReportLocalPreviewHealth("camera", _localCamera);
        ReportLocalPreviewHealth("screen", _localScreen);
    }

    /// <summary>
    /// Reports whether preview frames are flowing and whether a renderer is attached.
    /// A blank tile is otherwise ambiguous between the two.
    /// </summary>
    private void ReportLocalPreviewHealth(string label, LocalVideoSource? source)
    {
        if (source is null)
            return;

        _logger.LogInformation(
            "Local {Label} preview: {Frames} frames published, renderer attached={Attached}, " +
            "{Width}x{Height}, hidden={Hidden}.",
            label,
            source.FramesPublished,
            source.HasListener,
            source.Width,
            source.Height,
            _settings.HideLocalPreview);
    }

    private readonly Dictionary<long, long> _previousVideoRtp = [];
    private readonly Dictionary<long, (long Packets, long Units, long Frames, long Bytes)> _previousVideo = [];

    /// <summary>
    /// Reports the depacketizer's throughput per feed: RTP payloads in, access units
    /// reassembled, pictures decoded, and payloads rejected. A healthy H.264 stream shows
    /// units and frames tracking each other with drops at zero.
    /// </summary>
    private void ReportVideoHealth()
    {
        foreach (var subscriber in _janus?.Subscribers ?? [])
        {
            var previousRtp = _previousVideoRtp.TryGetValue(subscriber.FeedId, out var prevRtp) ? prevRtp : 0;
            _previousVideoRtp[subscriber.FeedId] = subscriber.VideoRtpPackets;

            var videoRtpRate = (subscriber.VideoRtpPackets - previousRtp) / 5;
            var hasFeed = _feeds.TryGetValue(subscriber.FeedId, out var f) && f.VideoPacketsReceived > 0;

            if (videoRtpRate > 0 && !hasFeed)
            {
                _logger.LogWarning(
                    "Video {FeedId}: {Rate} RTP pkts/s arriving but no frames reassembled " +
                    "(negotiated {Codec}).",
                    subscriber.FeedId, videoRtpRate, subscriber.VideoCodec);
            }
            else if (videoRtpRate == 0 && subscriber.IsNegotiated)
            {
                _logger.LogDebug(
                    "Video {FeedId}: no video RTP arriving; the sender may not be publishing.",
                    subscriber.FeedId);
            }

            if (videoRtpRate > 0)
            {
                _logger.LogInformation(
                    "Video {FeedId} feedback to sender: {Loss:F1}% loss, {Lost} lost total, " +
                    "jitter={Jitter}, granting {Granted} kbps (REMB).",
                    subscriber.FeedId,
                    subscriber.VideoLossPercent,
                    subscriber.VideoPacketsLost,
                    subscriber.VideoJitter,
                    subscriber.AdvertisedBitrateBps / 1000);
            }
        }

        foreach (var feed in _feeds.Values)
        {
            if (feed.VideoPacketsReceived == 0)
                continue;

            var previous = _previousVideo.TryGetValue(feed.FeedId, out var p) ? p : (0L, 0L, 0L, 0L);
            _previousVideo[feed.FeedId] = (
                feed.VideoPacketsReceived,
                feed.VideoAccessUnits,
                feed.VideoFramesDecoded,
                feed.VideoBytesReceived);

            _logger.LogInformation(
                "Video {FeedId} ({Kind}, {Tracks} track(s)): {Packets} pkts/s -> {Units} units/s -> " +
                "{Frames} fps, {Kbps} kbps in, dropped={Dropped}, {Width}x{Height}, " +
                "decoder={Decoder} ({State}).",
                feed.FeedId,
                feed.Kind,
                feed.VideoTrackCount,
                (feed.VideoPacketsReceived - previous.Item1) / 5,
                (feed.VideoAccessUnits - previous.Item2) / 5,
                (feed.VideoFramesDecoded - previous.Item3) / 5,
                (feed.VideoBytesReceived - previous.Item4) * 8 / 5 / 1000,
                feed.VideoPacketsDropped,
                feed.Width,
                feed.Height,
                feed.VideoDecoderName,
                feed.VideoFramesDecoded > 0 ? "producing" : "waiting for keyframe");
        }
    }

    private readonly Dictionary<long, (long Packets, long Samples)> _previousPackets = [];

    private (long Packets, long Samples) GetPreviousPackets(long feedId)
        => _previousPackets.TryGetValue(feedId, out var previous) ? previous : (0, 0);

    private void RememberPackets(long feedId, long packets, long samples)
        => _previousPackets[feedId] = (packets, samples);

    private readonly Dictionary<long, (long Submitted, long Consumed)> _previousRates = [];

    private long GetPrevious(long feedId, bool submitted)
        => _previousRates.TryGetValue(feedId, out var previous)
            ? submitted ? previous.Submitted : previous.Consumed
            : 0;

    private void RememberRates(Audio.MixerSourceDiagnostics source)
    {
        _previousRates[source.FeedId] = (source.SubmittedSamples, source.ConsumedSamples);
    }

    private void HandleMicrophoneSamples(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        // Voice activity is evaluated before the mute check so the mic meter in the
        // settings dialog still moves while muted — otherwise a muted user testing their
        // microphone sees nothing and assumes it is broken.
        if (_localSpeaking.Update(samples, _clock.Elapsed) && _descriptor is { } descriptor)
            SpeakingChanged?.Invoke(descriptor.UserId, !IsMuted && _localSpeaking.IsSpeaking);

        if (IsMuted || _opusEncoder is null || _resampler is null)
            return;

        try
        {
            var converted = _resampler.Convert(samples, sampleRate, OpusEncoder.Channels);

            _assembler.Append(converted, frame =>
                _opusEncoder.Encode(frame, (payload, _, _) =>
                    // Duration in RTP units for one 20ms Opus frame at 48kHz.
                    _janus?.SendAudio(payload, OpusEncoder.FrameSizeSamples)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Microphone encode path failed.");
        }
    }

    /// <summary>
    /// Camera frames: preview first, then encode. Preview before encoding so the local
    /// tile still moves if the encoder is failing — which is exactly when you want to see
    /// that the camera itself is fine.
    /// </summary>
    private void HandleCameraFrame(ReadOnlySpan<byte> data, int width, int height, VideoPixelFormat format)
    {
        if (!_settings.HideLocalPreview)
            _localCamera?.Publish(data, width, height, format);

        EncodeAndSendVideo(data, width, height, format);
    }

    private void HandleScreenFrame(ReadOnlySpan<byte> data, int width, int height, VideoPixelFormat format)
    {
        _captureWatchdog.FrameArrived(_clock.Elapsed);

        _localScreen?.Publish(data, width, height, format);
        EncodeAndSendVideo(data, width, height, format);
    }

    /// <summary>How often the capture is checked for signs of life.</summary>
    private static readonly TimeSpan CaptureCheckInterval = TimeSpan.FromSeconds(1);

    private readonly CaptureWatchdog _captureWatchdog = new();
    private ScreenShareTarget? _shareTarget;
    private long _previousCaptureRestarts;
    private int _shareGeneration;

    /// <summary>
    /// Notices a screen capture that has stopped producing and rebuilds it. Runs on the
    /// engine's heartbeat; the restart itself is handed to a background task so the pump
    /// is never left waiting on a capture device.
    /// </summary>
    private void CheckScreenCaptureAlive()
    {
        if (!IsScreenSharing || _shareTarget is null || _screen is null)
            return;

        if (!_captureWatchdog.TryBeginRestart(_clock.Elapsed, out var silence))
            return;

        _logger.LogWarning(
            "Screen capture has produced nothing for {Seconds:F1}s — restarting it. The share " +
            "is frozen on its last frame, and because nothing is being sent the far end sees " +
            "no loss and no dropped frames.",
            silence.TotalSeconds);

        _ = Task.Run(RestartScreenCaptureAsync);
    }

    private async Task RestartScreenCaptureAsync()
    {
        var restarted = false;

        // A rebuild can land on a different backend than the one that just failed, and
        // how long it may legitimately stay silent goes with it.
        TimeSpan? stallTimeout = null;

        // The share this restart belongs to. Stopping or restarting the share moves it on,
        // and everything below checks it before touching engine state — otherwise a user
        // who stops sharing mid-restart is left with a capture running for nobody.
        var generation = Volatile.Read(ref _shareGeneration);

        try
        {
            var target = _shareTarget;
            var previous = _screen;

            if (target is null || previous is null)
                return;

            previous.FrameCaptured -= HandleScreenFrame;

            try
            {
                await previous.StopAsync();
            }
            catch (Exception ex)
            {
                // A capture that has already failed often throws on the way down; that is
                // not a reason to abandon the restart.
                _logger.LogDebug(ex, "Stopping the stalled screen capture failed.");
            }

            previous.Dispose();

            if (Volatile.Read(ref _shareGeneration) != generation)
                return;

            var replacement = _backends.CreateScreenCapture();
            if (replacement is null)
            {
                _screen = null;
                ReportRestartFailure();
                return;
            }

            replacement.FrameCaptured += HandleScreenFrame;
            replacement.CaptureFailed += message => ErrorOccurred?.Invoke($"Screen share: {message}");

            _screen = replacement;

            await replacement.StartAsync(target, _settings.FrameRate, _shareWidth, _shareHeight);

            // The share ended while the device was opening. Nobody owns this capture now,
            // so it is stopped here rather than left running until the process exits.
            if (Volatile.Read(ref _shareGeneration) != generation)
            {
                replacement.FrameCaptured -= HandleScreenFrame;
                await replacement.StopAsync();
                replacement.Dispose();

                if (ReferenceEquals(_screen, replacement))
                    _screen = null;

                return;
            }

            restarted = true;
            stallTimeout = replacement.StallTimeout;
            _restartFailures = 0;
            _logger.LogInformation("Screen capture restarted after a stall.");

            // Receivers give up on a stream that has been silent for seconds, so the first
            // frame back should be one they can decode on its own. This goes straight to
            // the encoder rather than through the PLI path: it is our own request, and
            // feeding it to the storm detector would look like a peer in trouble.
            if (_videoEncoder is { } encoder)
            {
                lock (_uplinkGate)
                    _lastKeyframeAt = _clock.Elapsed;

                encoder.RequestKeyframe();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restart the screen capture; the share stays frozen.");
            ReportRestartFailure();
        }
        finally
        {
            _captureWatchdog.RestartFinished(_clock.Elapsed, restarted, stallTimeout);
        }
    }

    private int _restartFailures;

    /// <summary>
    /// Tells the user once that the share has stopped, and then stops telling them.
    /// <para>
    /// A display that has been taken by a fullscreen application cannot be duplicated at
    /// all, so every retry until that application goes away fails the same way. Retrying
    /// is right — it is how the share comes back on its own — but one message per attempt
    /// would be a notification every five seconds for as long as the game is open.
    /// </para>
    /// </summary>
    private void ReportRestartFailure()
    {
        if (Interlocked.Increment(ref _restartFailures) == 1)
            ErrorOccurred?.Invoke("Screen sharing stopped responding. Retrying…");
    }

    private void EncodeAndSendVideo(ReadOnlySpan<byte> data, int width, int height, VideoPixelFormat format)
    {
        var encoder = _videoEncoder;
        if (encoder is null)
            return;

        try
        {
            Interlocked.Increment(ref _videoFramesCaptured);

            encoder.Encode(data, width, height, format, (payload, rtpTimestamp, _) =>
            {
                // SIPSorcery wants the frame DURATION in RTP units, not an absolute
                // timestamp, and it must be measured between frames that are actually
                // SENT.
                //
                // Measuring per captured frame instead loses time: the encoder buffers, so
                // a captured frame often produces no packet at all, and that frame's
                // interval then never appears in any duration. The media clock falls
                // permanently behind real time by however much was swallowed. A receiver
                // plays out by that clock, so frames keep arriving — none of them lost —
                // while the picture stops advancing.
                var duration = MeasureFrameDuration();

                _videoRtpTimestamp = rtpTimestamp;
                Interlocked.Increment(ref _videoFramesEncoded);

                // Counted only once the packetizer actually took it. Counting at the
                // encoder instead reports a healthy outbound bitrate while the peer
                // connection is down and every frame is being dropped.
                if (_janus?.SendVideo(payload, duration) == true)
                {
                    Interlocked.Increment(ref _videoPacketsSent);
                    Interlocked.Add(ref _videoBytesSent, payload.Length);

                    // The media clock, as the receiver will see it. Summed here so the
                    // health report can compare it against the wall clock: any ratio other
                    // than 1.00 means the stream's own timeline is drifting away from real
                    // time, and a receiver playing out by it will stall while still
                    // receiving every frame.
                    Interlocked.Add(ref _videoRtpTicksSent, duration);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video encode path failed.");
        }
    }

    /// <summary>When the previous video frame was handed to the packetizer.</summary>
    private TimeSpan? _lastVideoSentAt;

    /// <summary>
    /// The pacer had to throw frames away, so every receiver has lost its reference chain.
    /// Nothing recovers that but a keyframe, and the rate limit is what stops the answer
    /// from becoming the next problem.
    /// </summary>
    private void HandlePacedFramesDropped() => HandleKeyframeRequested();

    /// <summary>
    /// RTP units between this frame and the last, measured rather than assumed.
    /// <para>
    /// Clamped at both ends: a first frame has nothing to measure from, and a share that
    /// was paused or a capture that stalled must not emit a gap so large that receivers
    /// treat the stream as restarted.
    /// </para>
    /// </summary>
    private uint MeasureFrameDuration()
    {
        var now = _clock.Elapsed;
        var nominal = (uint)(VideoEncoder.ClockRate / Math.Max(_settings.FrameRate, 1));

        var previous = _lastVideoSentAt;
        _lastVideoSentAt = now;

        if (previous is not { } last)
            return nominal;

        var elapsed = (now - last).TotalSeconds;
        if (elapsed <= 0)
            return 1;

        var ticks = elapsed * VideoEncoder.ClockRate;

        // One second is far longer than any real inter-frame gap; past that the capture
        // stopped rather than slowed.
        return (uint)Math.Clamp(ticks, 1, VideoEncoder.ClockRate);
    }

    /// <summary>
    /// Shortest gap between forced keyframes, however many receivers ask.
    /// <para>
    /// A keyframe at share resolution is worth a second or more of the whole bitrate
    /// budget. Honouring every request means the stream becomes almost nothing but
    /// keyframes, each one a burst large enough to lose packets — which makes the next
    /// receiver ask again. Past this the requests stop being recovery and become the thing
    /// that needs recovering from, so they are refused and counted instead.
    /// </para>
    /// </summary>
    private static readonly TimeSpan MinimumKeyframeInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Null until the first keyframe. Not a sentinel TimeSpan: subtracting
    /// <see cref="TimeSpan.MinValue"/> from anything overflows, and the throw lands inside
    /// SIPSorcery's receive path where it is logged and swallowed — which silently stopped
    /// keyframes being emitted at all, turning every decode failure permanent.
    /// </summary>
    private TimeSpan? _lastKeyframeAt;

    private long _keyframesSuppressed;
    private long _previousKeyframesSuppressed;

    /// <summary>
    /// Repeated keyframe requests in this window mean a receiver has stopped decoding
    /// rather than merely joined, and are treated as congestion.
    /// </summary>
    private static readonly TimeSpan KeyframeStormWindow = TimeSpan.FromSeconds(3);

    private const int KeyframeStormRequests = 3;

    /// <summary>How long to let a back-off settle before reading the requests again.</summary>
    private static readonly TimeSpan KeyframeStormCooldown = TimeSpan.FromSeconds(5);

    private readonly Queue<TimeSpan> _recentKeyframeRequests = new();
    private TimeSpan? _lastStormBackoffAt;

    private void HandleKeyframeRequested()
    {
        var encoder = _videoEncoder;
        if (encoder is null)
            return;

        var allow = false;

        lock (_uplinkGate)
        {
            var now = _clock.Elapsed;

            NoteKeyframeRequest(now);

            if (_lastKeyframeAt is not { } last || now - last >= MinimumKeyframeInterval)
            {
                _lastKeyframeAt = now;
                allow = true;
            }
            else
            {
                _keyframesSuppressed++;
            }
        }

        if (allow)
            encoder.RequestKeyframe();
    }

    /// <summary>
    /// Records a keyframe request and, when they are coming thick and fast, backs the send
    /// rate off without waiting for RTCP to agree.
    /// <para>
    /// Called with <see cref="_uplinkGate"/> held.
    /// </para>
    /// </summary>
    private void NoteKeyframeRequest(TimeSpan now)
    {
        _recentKeyframeRequests.Enqueue(now);

        while (_recentKeyframeRequests.Count > 0 && now - _recentKeyframeRequests.Peek() > KeyframeStormWindow)
            _recentKeyframeRequests.Dequeue();

        if (_recentKeyframeRequests.Count < KeyframeStormRequests)
            return;

        if (_lastStormBackoffAt is { } previous && now - previous < KeyframeStormCooldown)
            return;

        if (_uplink is not { } uplink)
            return;

        _lastStormBackoffAt = now;
        _recentKeyframeRequests.Clear();

        var requests = KeyframeStormRequests;
        var before = uplink.CurrentBps;

        var target = uplink.ReportCongestion(
            _appliedSendBitrateBps,
            $"{requests} keyframe requests inside {KeyframeStormWindow.TotalSeconds:F0}s");

        _logger.LogWarning(
            "A receiver has stopped decoding ({Requests} keyframe requests in {Seconds:F0}s). " +
            "Backing off {Before} -> {After} kbps without waiting for RTCP, which cannot see " +
            "loss beyond the SFU. This call will not send above {Ceiling} kbps again.",
            requests, KeyframeStormWindow.TotalSeconds, before / 1000, target / 1000,
            uplink.ReceiverCeilingBps / 1000);

        ApplySendBitrate(target);
    }

    private void HandleRemoteFeedAdded(JanusSubscriber subscriber)
    {
        var kind = subscriber.Display.IsScreenShare ? RemoteStreamKind.ScreenShare : RemoteStreamKind.Camera;

        var feed = new RemoteFeed(
            subscriber.FeedId,
            subscriber.Display.UserId,
            subscriber.Display.DisplayName,
            kind,
            _policy,
            _logger);

        feed.AudioDecoded += HandleDecodedAudio;
        feed.KeyframeNeeded += HandleRemoteKeyframeNeeded;

        if (!_feeds.TryAdd(subscriber.FeedId, feed))
        {
            feed.Dispose();
            return;
        }

        _mixer.SetGain(subscriber.Display.UserId, 1.0);

        SourceAdded?.Invoke(feed);
        RemoteStreamAdded?.Invoke(new RemoteStream(
            subscriber.Display.UserId, subscriber.FeedId, kind, HasVideo: true, HasAudio: true));
    }

    private void HandleRemoteFeedRemoved(JanusSubscriber subscriber)
    {
        if (!_feeds.TryRemove(subscriber.FeedId, out var feed))
            return;

        SourceRemoved?.Invoke(feed);
        RemoteStreamRemoved?.Invoke(new RemoteStream(
            feed.UserId, feed.FeedId, feed.Kind, HasVideo: false, HasAudio: false));

        // Each feed has its own mixer source, so this one goes regardless.
        _mixer.RemoveSource(feed.FeedId);

        // Speaking state is per participant, so only clear it once their last feed goes.
        if (!_feeds.Values.Any(f => f.UserId == feed.UserId)
            && _remoteSpeaking.TryRemove(feed.UserId, out _))
        {
            SpeakingChanged?.Invoke(feed.UserId, false);
        }

        feed.Dispose();
    }

    private void HandleRemoteFrame(
        JanusSubscriber subscriber,
        bool isAudio,
        ReadOnlySpan<byte> frame,
        uint rtpTimestamp,
        VideoCodec videoCodec,
        int audioChannels)
    {
        if (!_feeds.TryGetValue(subscriber.FeedId, out var feed))
            return;

        // Already reassembled by SIPSorcery, and the codec is the one it negotiated for
        // this stream rather than our own publishing preference.
        feed.HandleFrame(isAudio, frame, videoCodec, audioChannels, rtpTimestamp);
    }

    private void HandleDecodedAudio(long feedId, Guid userId, short[] pcm, int length)
    {
        var samples = pcm.AsSpan(0, length);

        _mixer.Submit(feedId, userId, samples);

        var detector = _remoteSpeaking.GetOrAdd(userId, _ => new SpeakingDetector());
        if (detector.Update(samples, _clock.Elapsed))
            SpeakingChanged?.Invoke(userId, detector.IsSpeaking);
    }

    private void HandleRemoteKeyframeNeeded(long feedId)
    {
        var sent = _janus?.RequestKeyframe(feedId) == true;

        _logger.LogDebug(
            "Feed {FeedId} needs a keyframe; PLI {Outcome}.",
            feedId, sent ? "sent" : "not sent (peer not ready)");
    }

    public async Task StopAsync(string reason = "unspecified", CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping media engine (reason={Reason}).", reason);

        IsRunning = false;

        if (_playbackCts is not null)
        {
            await _playbackCts.CancelAsync();
            if (_playbackTask is not null)
            {
                try
                {
                    await _playbackTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Playback pump did not stop within 2s.");
                }
                catch (OperationCanceledException) { }
            }

            _playbackCts.Dispose();
            _playbackCts = null;
            _playbackTask = null;
        }

        await StopScreenShareAsync();
        await SetCameraEnabledAsync(false);

        if (_microphone is not null)
        {
            _microphone.SamplesCaptured -= HandleMicrophoneSamples;
            await _microphone.StopAsync();
            _microphone.Dispose();
            _microphone = null;
        }

        if (_playback is not null)
        {
            await _playback.StopAsync();
            _playback.Dispose();
            _playback = null;
        }

        if (_janus is not null)
        {
            _janus.RemoteFeedAdded -= HandleRemoteFeedAdded;
            _janus.RemoteFeedRemoved -= HandleRemoteFeedRemoved;
            _janus.RemoteFrameReceived -= HandleRemoteFrame;
            _janus.KeyframeRequested -= HandleKeyframeRequested;
            _janus.OutboundQualityReported -= HandleOutboundQuality;
            _janus.PacedFramesDropped -= HandlePacedFramesDropped;

            await _janus.DisposeAsync();
            _janus = null;
        }

        _uplink = null;

        foreach (var feedId in _feeds.Keys.ToArray())
        {
            if (_feeds.TryRemove(feedId, out var feed))
                feed.Dispose();
        }

        _mixer.Clear();
        _assembler.Reset();

        _opusEncoder?.Dispose();
        _opusEncoder = null;
        _resampler?.Dispose();
        _resampler = null;
        _videoEncoder?.Dispose();
        _videoEncoder = null;
    }

    public Task SetMutedAsync(bool muted)
    {
        IsMuted = muted;
        MuteChanged?.Invoke(muted);
        return Task.CompletedTask;
    }

    public async Task SetCameraEnabledAsync(bool enabled)
    {
        if (enabled == !IsCameraOff)
            return;

        if (!enabled)
        {
            if (_camera is not null)
            {
                _camera.FrameCaptured -= HandleCameraFrame;
                await _camera.StopAsync();
                _camera.Dispose();
                _camera = null;
            }

            RetireLocalSource(ref _localCamera);

            if (!IsScreenSharing)
            {
                _videoEncoder?.Dispose();
                _videoEncoder = null;
            }

            IsCameraOff = true;
            CameraChanged?.Invoke(true);
            return;
        }

        EnsureVideoEncoder();
        _lastVideoSentAt = null;

        _camera = _backends.CreateCameraCapture();
        _camera.FrameCaptured += HandleCameraFrame;
        _camera.CaptureFailed += message => ErrorOccurred?.Invoke($"Camera: {message}");

        await _camera.StartAsync(
            _settings.CameraDeviceId, _settings.Width, _settings.Height, _settings.FrameRate);

        PublishLocalSource(ref _localCamera, "local-camera", RemoteStreamKind.Camera);

        IsCameraOff = false;
        CameraChanged?.Invoke(false);
    }

    /// <summary>Creates a local tile source and tells the UI about it.</summary>
    private void PublishLocalSource(ref LocalVideoSource? slot, string id, RemoteStreamKind kind)
    {
        var descriptor = _descriptor;

        var source = new LocalVideoSource(
            id,
            descriptor?.UserId ?? Guid.Empty,
            descriptor?.UserDisplayName ?? "You",
            kind);

        slot = source;
        SourceAdded?.Invoke(source);
    }

    private void RetireLocalSource(ref LocalVideoSource? slot)
    {
        var source = slot;
        if (source is null)
            return;

        slot = null;
        source.Stop();
        SourceRemoved?.Invoke(source);
    }

    private void EnsureVideoEncoder()
    {
        _videoEncoder ??= VideoEncoder.Create(
            _negotiatedCodec,
            _settings.Width,
            _settings.Height,
            _settings.FrameRate,
            SendBitrateKbps,
            _policy,
            _settings.ContentHint,
            _logger);
    }

    /// <summary>
    /// Send-side congestion control. Fed by the SFU's reception reports and applied to the
    /// encoder — the encoder's configured bitrate is a ceiling, not an instruction.
    /// </summary>
    private BandwidthEstimator? _uplink;

    /// <summary>
    /// Guards the send-side controller. RTCP reports arrive on SIPSorcery's thread while
    /// the user can re-cap the ceiling from the UI thread at the same moment.
    /// </summary>
    private readonly Lock _uplinkGate = new();

    private long _uplinkPreviousBytes;
    private TimeSpan _uplinkMeasuredAt;

    // The same counters again, but spanning report to report rather than tick to tick.
    private long _uplinkReportBytes;
    private TimeSpan _uplinkReportAt;

    private int _appliedSendBitrateBps;

    /// <summary>
    /// Bitrate a new encoder should open at: whatever the controller currently believes,
    /// or the configured ceiling before a call has told us anything.
    /// </summary>
    private int SendBitrateKbps => (_uplink?.CurrentBps / 1000) ?? _settings.BitrateKbps;

    /// <summary>
    /// Arms the send-side controller for a call.
    /// <para>
    /// It deliberately starts below the configured cap. That figure is used before
    /// anything about the path has been measured, and opening a 12 Mbps share at 12 Mbps
    /// pushes a burst into a link of unknown capacity from the first frame: the queue
    /// builds, packets drop, and the controller answers with a hard cut. That is the shape
    /// of a share that never settles at its frame rate. Ramping costs a few seconds.
    /// </para>
    /// </summary>
    private void StartSendBandwidthControl()
    {
        var ceiling = Math.Max(MinimumSendBitrateBps, _settings.BitrateKbps * 1000);

        _uplink = new BandwidthEstimator(
            startBps: Math.Max(MinimumSendBitrateBps, (int)(ceiling * StartSendBitrateFraction)),
            minimumBps: MinimumSendBitrateBps,
            maximumBps: ceiling);

        _uplinkPreviousBytes = Interlocked.Read(ref _videoBytesSent);
        _uplinkMeasuredAt = _clock.Elapsed;
        _uplinkReportBytes = _uplinkPreviousBytes;
        _uplinkReportAt = _uplinkMeasuredAt;
        _appliedSendBitrateBps = _uplink.CurrentBps;
        _latestQuality = default;
        _qualitySequence = 0;
        _consumedQualitySequence = 0;

        _logger.LogInformation(
            "Send bandwidth control armed: starting at {Start} kbps, ceiling {Ceiling} kbps.",
            _uplink.CurrentBps / 1000, ceiling / 1000);
    }

    /// <summary>
    /// Opening estimate as a share of the configured ceiling.
    /// <para>
    /// Enough of a gap that the first seconds do not burst into a link of unknown capacity,
    /// small enough that one step of fast growth closes it — a share reaches its configured
    /// bitrate about a second in, rather than ramping through visibly soft frames.
    /// </para>
    /// </summary>
    private const double StartSendBitrateFraction = 0.80;

    /// <summary>Floor for the uplink. Below this, video is not worth sending.</summary>
    private const int MinimumSendBitrateBps = 300_000;

    /// <summary>How often the send-side controller re-measures. See <see cref="UpdateSendBandwidth"/>.</summary>
    private static readonly TimeSpan SendBandwidthInterval = TimeSpan.FromSeconds(1);

    private OutboundQualityReport _latestQuality;
    private long _qualitySequence;
    private long _consumedQualitySequence;

    /// <summary>
    /// Records the SFU's latest verdict on our outbound video. The controller runs on its
    /// own cadence rather than here: RTCP reports arrive only every few seconds, and
    /// growing once per report is what made recovering from a dip take a minute.
    /// </summary>
    private void HandleOutboundQuality(OutboundQualityReport report)
    {
        lock (_uplinkGate)
        {
            _latestQuality = report;
            _qualitySequence++;
        }
    }

    /// <summary>
    /// One turn of the send-side control loop, once a second.
    /// <para>
    /// Faster than the reports that feed it, deliberately: how much is flowing is measured
    /// here and now, and a clean report stays true until a new one contradicts it, so
    /// growth need not wait on RTCP. Cuts do wait — the estimator only acts once on each
    /// report, or one bad second would be paid for five times over.
    /// </para>
    /// </summary>
    private void UpdateSendBandwidth()
    {
        lock (_uplinkGate)
        {
            var uplink = _uplink;
            if (uplink is null)
                return;

            var now = _clock.Elapsed;
            var elapsed = now - _uplinkMeasuredAt;
            _uplinkMeasuredAt = now;

            var bytes = Interlocked.Read(ref _videoBytesSent);
            var sent = Math.Max(0, bytes - _uplinkPreviousBytes);
            _uplinkPreviousBytes = bytes;

            // Too short an interval makes the rate meaningless; report it as nothing
            // flowing and let the controller hold rather than act on noise.
            var observed = elapsed.TotalSeconds >= 0.25
                ? (int)Math.Min(int.MaxValue, sent * 8 / elapsed.TotalSeconds)
                : 0;

            var fresh = _qualitySequence != _consumedQualitySequence;
            _consumedQualitySequence = _qualitySequence;

            if (fresh)
            {
                // A cut is sized from this figure, and the loss it answers was measured
                // over the whole reporting interval — so this has to cover the same span.
                // A single second of a screen share is not representative of it: a still
                // moment reads as a near-idle link and would cut the target to a fraction
                // of what was really flowing.
                var reportSpan = now - _uplinkReportAt;
                var reportSent = Math.Max(0, bytes - _uplinkReportBytes);

                if (reportSpan.TotalSeconds >= 0.5)
                    observed = (int)Math.Min(int.MaxValue, reportSent * 8 / reportSpan.TotalSeconds);

                _uplinkReportAt = now;
                _uplinkReportBytes = bytes;
            }

            var target = uplink.Update(new BandwidthSample
            {
                ObservedBps = observed,
                LossFraction = _latestQuality.LossFraction,
                JitterMs = _latestQuality.JitterMs,
                ReportIsFresh = fresh,
                Elapsed = elapsed
            });

            ApplySendBitrate(target);
        }
    }

    /// <summary>
    /// Pushes a new target at the encoder, and at Janus so its own cap tracks ours.
    /// Small moves are ignored: every re-application is a chance to disturb the encoder's
    /// rate controller while it is still finding its operating point.
    /// </summary>
    private void ApplySendBitrate(int targetBps)
    {
        if (_appliedSendBitrateBps > 0 &&
            Math.Abs(targetBps - _appliedSendBitrateBps) < _appliedSendBitrateBps * 0.05)
        {
            return;
        }

        var previous = _appliedSendBitrateBps;
        _appliedSendBitrateBps = targetBps;

        _videoEncoder?.SetBitrate(targetBps / 1000);

        // The pacer drains at a multiple of this, so it has to follow the target rather
        // than the ceiling — pacing at 10 Mbps on a 2 Mbps link is not pacing.
        _janus?.SetPacingRate(targetBps);

        _logger.LogInformation(
            "Send bitrate {Previous} -> {Target} kbps ({Reason}).",
            previous / 1000, targetBps / 1000, _uplink?.LastReason);

        // Janus caps publishers itself, so its number has to follow ours or it becomes the
        // binding constraint the moment we try to climb.
        var janus = _janus;
        if (janus is null)
            return;

        _ = janus.SetBitrateAsync(targetBps).ContinueWith(
            task => _logger.LogDebug(task.Exception, "Janus did not accept the new send bitrate."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public async Task StartScreenShareAsync(ScreenShareTarget target, CancellationToken cancellationToken = default)
    {
        var screen = _backends.CreateScreenCapture();
        if (screen is null)
        {
            ErrorOccurred?.Invoke("Screen sharing is not supported on this platform.");
            return;
        }

        // One encoder, one outbound video track: camera and screen cannot run at once,
        // or their frames interleave into a single stream and every receiver sees the
        // picture flip between the two. The Blazor client published the share as a
        // second Janus feed (PublisherDisplay.RoleNativeScreen); until that exists here,
        // starting a share turns the camera off, visibly, rather than corrupting both.
        if (!IsCameraOff)
        {
            _logger.LogInformation("Stopping the camera: screen share and camera share one encoder.");
            await SetCameraEnabledAsync(false);
        }

        // A share keeps the monitor's aspect and detail rather than the camera preset, but
        // is capped to the configured resolution — which is what the Blazor client does
        // (getRequiredScreenShareScaleDownBy) and what the bitrate was chosen for. Encoding
        // a 1440p desktop at full size against a budget set for 1080p spends it all on
        // keyframes: each one is a burst big enough to lose packets, every loss brings
        // another keyframe request, and the share never recovers.
        //
        // The capture backend is asked for that size directly, so on Windows the GPU scales
        // the surface before it is ever copied to system memory and the encoder receives
        // frames that need no rescaling at all.
        var (width, height) = target is { Width: > 0, Height: > 0 }
            ? ScaleToFit(target.Width, target.Height, _settings.Width, _settings.Height)
            : (0, 0);

        if (width > 0 && height > 0)
        {
            if (width != target.Width || height != target.Height)
            {
                _logger.LogInformation(
                    "Sharing {SourceWidth}x{SourceHeight} at {Width}x{Height} to stay within the " +
                    "configured resolution of {LimitWidth}x{LimitHeight}.",
                    target.Width, target.Height, width, height, _settings.Width, _settings.Height);
            }

            _videoEncoder?.Dispose();

            // At the controller's current target, not the configured cap: what the path
            // has already been shown to carry is better information than the ceiling, in
            // both directions.
            _videoEncoder = VideoEncoder.Create(
                _negotiatedCodec,
                width,
                height,
                _settings.FrameRate,
                SendBitrateKbps,
                _policy,
                VideoContentHint.Detail,
                _logger);
        }
        else
        {
            EnsureVideoEncoder();
        }

        // The guard scales down from whatever the capture actually delivers, so it starts
        // from the capped size rather than the monitor's own. The target is kept so the
        // watchdog can rebuild exactly this capture if it stops producing.
        _shareWidth = width;
        _shareHeight = height;
        _shareTarget = target;
        _shareQuality.Reset();
        _previousCaptureRestarts = 0;
        _restartFailures = 0;
        Interlocked.Increment(ref _shareGeneration);

        // The frame clock restarts with the source: the gap since the camera's last frame
        // is not media time this share should account for.
        _lastVideoSentAt = null;

        _screen = screen;
        _screen.FrameCaptured += HandleScreenFrame;
        _screen.CaptureFailed += message => ErrorOccurred?.Invoke($"Screen share: {message}");

        await _screen.StartAsync(target, _settings.FrameRate, width, height, cancellationToken);

        // The stall clock starts once the device is open, so opening it slowly is never
        // mistaken for a device that opened and then died — and at the tolerance the
        // backend declares, which it only knows once it has seen which mechanism it got.
        _captureWatchdog.Started(_clock.Elapsed, _screen.StallTimeout);

        PublishLocalSource(ref _localScreen, "local-screen", RemoteStreamKind.ScreenShare);

        IsScreenSharing = true;
        ScreenShareChanged?.Invoke(true);
    }

    /// <summary>How often the share's resolution is reconsidered. The Blazor guard's cadence.</summary>
    private static readonly TimeSpan ShareQualityInterval = TimeSpan.FromSeconds(2);

    private readonly ScreenShareQualityGuard _shareQuality = new();

    /// <summary>Capture size of the running share, which the guard scales down from.</summary>
    private int _shareWidth;
    private int _shareHeight;

    /// <summary>
    /// Reconsiders how many pixels the share should be sending.
    /// <para>
    /// The frame rate is held and resolution gives way, which is the choice a browser makes
    /// too: motion that stutters reads as broken, while motion that is a little soft does
    /// not. Rebuilding the encoder is heavier than a browser's scale factor, so the guard's
    /// hysteresis matters — six seconds of agreement before either direction moves.
    /// </para>
    /// </summary>
    private void UpdateShareQuality()
    {
        if (!IsScreenSharing || _shareWidth <= 0 || _shareHeight <= 0)
            return;

        int target;
        lock (_uplinkGate)
        {
            if (_uplink is not { } uplink)
                return;

            target = uplink.CurrentBps;
        }

        var scale = _shareQuality.Update(target, _shareWidth, _shareHeight, _settings.FrameRate);
        if (scale is null)
            return;

        var (width, height) = ScreenShareQualityGuard.Sized(_shareWidth, _shareHeight, scale.Value);

        _logger.LogInformation(
            "Share now {Width}x{Height} (scale {Scale:F2}): {Reason}.",
            width, height, scale.Value, _shareQuality.LastReason);

        try
        {
            var replacement = VideoEncoder.Create(
                _negotiatedCodec,
                width,
                height,
                _settings.FrameRate,
                SendBitrateKbps,
                _policy,
                VideoContentHint.Detail,
                _logger);

            var previous = _videoEncoder;
            _videoEncoder = replacement;
            previous?.Dispose();

            // The new size means a new sequence parameter set, so receivers need a
            // keyframe to pick it up. This is a size change, not a decode failure, so it
            // goes straight to the encoder rather than through the request rate limit.
            replacement.RequestKeyframe();
        }
        catch (Exception ex)
        {
            // Keep sharing at the old size rather than dropping the stream entirely.
            _logger.LogWarning(ex, "Could not rebuild the share encoder at {Width}x{Height}.", width, height);
        }
    }

    /// <summary>
    /// Shrinks a capture size to fit within a limit, keeping its aspect ratio and never
    /// enlarging. The same rule the Blazor client applies to a share's sender.
    /// </summary>
    internal static (int Width, int Height) ScaleToFit(
        int sourceWidth, int sourceHeight, int limitWidth, int limitHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || limitWidth <= 0 || limitHeight <= 0)
            return (sourceWidth, sourceHeight);

        var scale = Math.Max(
            1.0,
            Math.Max(sourceWidth / (double)limitWidth, sourceHeight / (double)limitHeight));

        // Past a quarter size the picture is unreadable anyway; a share that far over the
        // limit is better served by the user picking a smaller target.
        scale = Math.Min(scale, 4.0);

        if (scale <= 1.0)
            return (sourceWidth, sourceHeight);

        // 4:2:0 chroma needs even dimensions, and rounding down keeps us inside the limit.
        var width = (int)Math.Round(sourceWidth / scale) & ~1;
        var height = (int)Math.Round(sourceHeight / scale) & ~1;

        return (Math.Max(2, width), Math.Max(2, height));
    }

    public async Task StopScreenShareAsync()
    {
        if (_screen is null)
            return;

        // Bumped before anything is awaited, so a restart already in flight sees the share
        // is over and throws its replacement away rather than installing it here.
        Interlocked.Increment(ref _shareGeneration);
        _captureWatchdog.Stopped();

        _screen.FrameCaptured -= HandleScreenFrame;
        await _screen.StopAsync();
        _screen.Dispose();
        _screen = null;

        RetireLocalSource(ref _localScreen);

        if (IsCameraOff)
        {
            _videoEncoder?.Dispose();
            _videoEncoder = null;
        }

        _shareWidth = 0;
        _shareHeight = 0;
        _shareTarget = null;
        _shareQuality.Reset();

        IsScreenSharing = false;
        ScreenShareChanged?.Invoke(false);
    }

    public async Task<IReadOnlyList<ScreenShareTarget>> GetScreenShareTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        using var screen = _backends.CreateScreenCapture();
        if (screen is null)
            return [];

        return await screen.GetTargetsAsync(cancellationToken);
    }

    public Task ApplySettingsAsync(MediaEngineSettings settings)
    {
        var previous = _settings;
        _settings = settings;

        if (_playback is not null)
            _playback.Volume = settings.PlaybackVolume;

        // Bitrate retunes in place; resolution or codec changes need the encoder rebuilt,
        // which in turn needs Janus renegotiation and so is deferred to the next start.
        if (settings.BitrateKbps != previous.BitrateKbps)
        {
            // The setting is a ceiling the controller works under, not a value to apply.
            // Lowering it binds at once; raising it only permits future growth, which the
            // controller earns a step at a time rather than taking on trust.
            lock (_uplinkGate)
            {
                if (_uplink is { } uplink)
                {
                    uplink.SetCeiling(Math.Max(MinimumSendBitrateBps, settings.BitrateKbps * 1000));
                    ApplySendBitrate(uplink.CurrentBps);
                }
                else
                {
                    _videoEncoder?.SetBitrate(settings.BitrateKbps);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        var enumerator = EnsureDeviceEnumerator();

        var devices = new List<MediaDeviceInfo>();
        devices.AddRange(enumerator.GetDevices(MediaDeviceKind.AudioInput));
        devices.AddRange(enumerator.GetDevices(MediaDeviceKind.AudioOutput));

        // Cameras come from a different enumerator on desktop (FFmpeg rather than WASAPI).
        devices.AddRange(enumerator.GetDevices(MediaDeviceKind.VideoInput));

        return Task.FromResult<IReadOnlyList<MediaDeviceInfo>>(devices);
    }

    /// <summary>
    /// Creates the enumerator on first use and subscribes to hot-plug notifications, so
    /// unplugging a headset mid-call refreshes the settings dialog.
    /// </summary>
    private IMediaDeviceEnumerator EnsureDeviceEnumerator()
    {
        if (_audioDevices is { } existing)
            return existing;

        var created = _backends.CreateDeviceEnumerator();
        created.DevicesChanged += HandleDevicesChanged;
        _audioDevices = created;

        return created;
    }

    private void HandleDevicesChanged()
    {
        var handler = DevicesChanged;
        if (handler is null)
            return;

        // Re-enumerating touches COM and can block briefly; keep it off the WASAPI
        // notification thread, which must return promptly.
        _ = Task.Run(async () =>
        {
            try
            {
                handler(await EnumerateDevicesAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device change refresh failed.");
            }
        });
    }

    public Task<IReadOnlyList<VideoCodec>> GetSupportedVideoCodecsAsync()
    {
        var supported = new List<VideoCodec>();

        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            try
            {
                using var probe = VideoEncoder.Create(codec, 320, 240, 30, 300, _policy, logger: _logger);
                supported.Add(codec);
            }
            catch
            {
                // Codec unavailable in this FFmpeg build or on this hardware.
            }
        }

        return Task.FromResult<IReadOnlyList<VideoCodec>>(supported);
    }

    /// <summary>
    /// Current microphone level for the settings-dialog meter, scaled to the 0–100 range
    /// the UI's voice-gate threshold uses.
    /// </summary>
    public Task<VoiceGateLevel> GetVoiceGateLevelAsync()
        => Task.FromResult(new VoiceGateLevel(
            Level: (int)Math.Clamp(_localSpeaking.Level * 100 * 3, 0, 100),
            Threshold: _settings.VoiceGateThreshold));

    public Task SetParticipantVolumeAsync(Guid userId, double gain)
    {
        _mixer.SetGain(userId, gain);
        return Task.CompletedTask;
    }

    public Task SetPushToTalkAsync(bool enabled, string key)
    {
        PushToTalkChanged?.Invoke(enabled);
        return Task.CompletedTask;
    }

    private IAudioCapture? _testMicrophone;
    private readonly SemaphoreSlim _testMicrophoneGate = new(1, 1);

    /// <summary>
    /// Feeds the settings-dialog level meter while no call is running.
    /// <para>
    /// In a call the live microphone path already updates the detector, and opening the
    /// device a second time would fail on exclusive-mode backends — so this is a no-op
    /// then. Outside a call, a temporary capture drives the same detector the meter
    /// reads; nothing is encoded or sent.
    /// </para>
    /// <para>
    /// Serialized by a gate: the dialog's initial load and its selection-changed handler
    /// can race, and two concurrent starts both passing a plain null check leaked a
    /// running capture that held the device until process exit.
    /// </para>
    /// </summary>
    public async Task StartMicrophoneTestAsync(string? deviceId)
    {
        if (IsRunning)
            return;

        await _testMicrophoneGate.WaitAsync();
        try
        {
            if (_testMicrophone is not null)
                return;

            var microphone = _backends.CreateMicrophoneCapture();
            microphone.SamplesCaptured += HandleTestMicrophoneSamples;
            microphone.CaptureFailed += message => ErrorOccurred?.Invoke($"Microphone test: {message}");

            try
            {
                await microphone.StartAsync(deviceId);
                _testMicrophone = microphone;
            }
            catch
            {
                microphone.SamplesCaptured -= HandleTestMicrophoneSamples;
                microphone.Dispose();
                throw;
            }
        }
        finally
        {
            _testMicrophoneGate.Release();
        }
    }

    private void HandleTestMicrophoneSamples(ReadOnlySpan<short> samples, int channels, int sampleRate)
        => _localSpeaking.Update(samples, _clock.Elapsed);

    public async Task StopMicrophoneTestAsync()
    {
        await _testMicrophoneGate.WaitAsync();
        try
        {
            var microphone = _testMicrophone;
            if (microphone is null)
                return;

            _testMicrophone = null;
            microphone.SamplesCaptured -= HandleTestMicrophoneSamples;
            await microphone.StopAsync();
            microphone.Dispose();
        }
        finally
        {
            _testMicrophoneGate.Release();
        }
    }

    public Task UpdateParticipantProfilesAsync(
        IReadOnlyDictionary<string, string> displayNames,
        IReadOnlyDictionary<string, string> avatarUrls)
        => Task.CompletedTask;

    /// <summary>
    /// Signalling relayed through the chat hub. Janus carries its own SDP and ICE, so
    /// nothing here needs it today; the hook exists for peer-to-peer side channels.
    /// </summary>
    public Task DeliverSignalAsync(Guid fromUserId, string signalType, string payload)
        => Task.CompletedTask;

    public Task<IReadOnlyList<MediaStreamStats>> GetStatsAsync()
    {
        var stats = _feeds.Values
            .Select(feed => new MediaStreamStats(
                feed.FeedId,
                _negotiatedCodec.ToString(),
                feed.Width,
                feed.Height,
                FrameRate: 0,
                BitrateBps: 0,
                PacketLossPercent: 0,
                JitterMs: 0,
                RoundTripMs: 0))
            .ToArray();

        return Task.FromResult<IReadOnlyList<MediaStreamStats>>(stats);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await StopAsync("disposed");
        await StopMicrophoneTestAsync();

        _camera?.Dispose();
        _screen?.Dispose();
        (_audioDevices as IDisposable)?.Dispose();
    }
}
