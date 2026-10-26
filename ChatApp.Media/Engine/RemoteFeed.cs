using ChatApp.Core.Media;
using ChatApp.Media.Audio;
using ChatApp.Media.Codec;
using ChatApp.Media.Janus;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Engine;

/// <summary>
/// Everything needed to turn one remote Janus feed back into audio and video: its
/// decoders, and the <see cref="IVideoSource"/> the UI binds a renderer to.
/// <para>
/// Decoders are created lazily on the first packet of each kind, because Janus does not
/// tell us up front whether a feed carries audio, video or both, and allocating an
/// unused decoder costs real memory per participant.
/// </para>
/// </summary>
internal sealed class RemoteFeed : IVideoSource, IDisposable
{
    private readonly VideoEncoderPolicy _policy;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private OpusDecoder? _audioDecoder;
    private AudioResampler? _audioResampler;
    private int _lastDecodedChannels;
    private bool _hardwareDecodeDisabled;
    private bool _disposed;

    /// <summary>
    /// The policy new decoders are built from. Once hardware decode has failed on this
    /// feed it stays off for the rest of the call, so a track that reopens — after a
    /// codec switch, or the fallback itself — does not walk back into the same failure.
    /// </summary>
    private VideoEncoderPolicy DecoderPolicy => _hardwareDecodeDisabled
        ? _policy with { AllowHardwareDecode = false }
        : _policy;

    /// <summary>
    /// Video state per track, keyed by SDP mid.
    /// <para>
    /// One Janus feed can carry several video tracks at once - a camera and a screen
    /// share, or simulcast layers. They are independent streams, so sharing one
    /// depacketizer interleaves their fragments and corrupts every access unit; sharing
    /// one decoder then flips resolution from frame to frame.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, VideoTrack> _videoTracks = [];

    private sealed class VideoTrack : IDisposable
    {
        public VideoDecoder? Decoder;
        public int Width;
        public int Height;
        public long PacketsReceived;
        public long BytesReceived;
        public long AccessUnits;
        public long FramesDecoded;

        public void Dispose() => Decoder?.Dispose();
    }

    public string Id { get; }
    public Guid UserId { get; }
    public string DisplayName { get; }
    public RemoteStreamKind Kind { get; }
    public long FeedId { get; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>
    /// Audio packets handed over by libdatachannel, and the samples-per-channel the
    /// decoder produced from them. Packet rate x samples-per-packet is the only way to
    /// tell "we are receiving half the packets" from "each packet holds half the audio".
    /// </summary>
    public long AudioPacketsReceived { get; private set; }

    public long AudioSamplesDecoded { get; private set; }

    /// <summary>Video RTP payloads in, access units reassembled, and pictures decoded.</summary>
    public long VideoPacketsReceived => SumTracks(t => t.PacketsReceived);

    public long VideoAccessUnits => SumTracks(t => t.AccessUnits);

    /// <summary>
    /// Encoded video bytes received. The only way to tell a genuinely low-bitrate sender
    /// from a picture that merely looks soft after scaling.
    /// </summary>
    public long VideoBytesReceived => SumTracks(t => t.BytesReceived);

    public long VideoFramesDecoded => SumTracks(t => t.FramesDecoded);

    /// <summary>Frames the decoder could not use, e.g. before the first keyframe.</summary>
    public long VideoPacketsDropped => SumTracks(t => t.AccessUnits - t.FramesDecoded);

    /// <summary>Distinct video tracks on this feed.</summary>
    public int VideoTrackCount { get { lock (_gate) return _videoTracks.Count; } }

    /// <summary>
    /// The path the rendered track is decoding on — <c>h264+d3d11va</c>, <c>h264</c>, and
    /// so on. A CPU spike with several feeds open is ambiguous without it.
    /// </summary>
    public string VideoDecoderName
    {
        get
        {
            lock (_gate)
                return FirstTrack()?.Decoder?.DecoderName ?? "none";
        }
    }

    private long SumTracks(Func<VideoTrack, long> selector)
    {
        lock (_gate)
        {
            long total = 0;
            foreach (var track in _videoTracks.Values)
                total += selector(track);

            return total;
        }
    }

    public event VideoFrameHandler? FrameReady;
    public event Action<int, int>? ResolutionChanged;

    /// <summary>
    /// Raised with decoded PCM so the engine can feed the mixer. Carries the feed id
    /// because one participant can publish several audio streams at once.
    /// </summary>
    public event Action<long, Guid, short[], int>? AudioDecoded;

    /// <summary>
    /// Raised when video arrives but nothing has decoded yet — the engine turns this
    /// into a keyframe request, since a mid-stream joiner sees only P-frames until then.
    /// </summary>
    public event Action<long>? KeyframeNeeded;

    public RemoteFeed(
        long feedId,
        Guid userId,
        string displayName,
        RemoteStreamKind kind,
        VideoEncoderPolicy policy,
        ILogger logger)
    {
        FeedId = feedId;
        UserId = userId;
        DisplayName = displayName;
        Kind = kind;
        _policy = policy;
        _logger = logger;
        Id = feedId.ToString();
    }

    /// <summary>Routes one depacketized frame to the right decoder.</summary>
    public void HandleFrame(
        bool isAudio,
        ReadOnlySpan<byte> payload,
        VideoCodec negotiatedCodec,
        int audioChannels,
        uint rtpTimestamp = 0)
    {
        if (_disposed || payload.IsEmpty)
            return;

        if (isAudio)
        {
            DecodeAudio(payload, audioChannels);
            return;
        }

        DecodeVideo(payload, negotiatedCodec, rtpTimestamp);
    }

    private void DecodeAudio(ReadOnlySpan<byte> payload, int audioChannels)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _audioDecoder ??= OpusDecoder.Create(audioChannels, _logger);
                _audioResampler ??= new AudioResampler(
                    targetSampleRate: OpusEncoder.SampleRate,
                    targetChannels: OpusEncoder.Channels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create an Opus decoder for feed {FeedId}.", FeedId);
                return;
            }

            AudioPacketsReceived++;

            _audioDecoder.Decode(payload, (pcm, channels, sampleRate) =>
            {
                AudioSamplesDecoded += pcm.Length / Math.Max(channels, 1);

                // WebRTC negotiates MONO Opus unless the SDP carries stereo=1, so a
                // browser publisher decodes to one channel. Submitting that straight into
                // the stereo mixer plays it at double speed and starves the pump — the
                // channel count and rate must be honoured, not assumed.
                if (channels != _lastDecodedChannels)
                {
                    _lastDecodedChannels = channels;
                    _logger.LogInformation(
                        "Feed {FeedId} audio: {Channels}ch {SampleRate}Hz -> {TargetChannels}ch {TargetRate}Hz.",
                        FeedId, channels, sampleRate, OpusEncoder.Channels, OpusEncoder.SampleRate);
                }

                var converted = _audioResampler.Convert(pcm, sampleRate, channels);
                if (converted.IsEmpty)
                    return;

                // Copied out of the reusable buffer: the mixer keeps it until the next
                // playback pump.
                AudioDecoded?.Invoke(FeedId, UserId, converted.ToArray(), converted.Length);
            });
        }
    }

    /// <summary>
    /// Set when video arrives while no renderer is subscribed, so the engine can request
    /// a keyframe at the moment one attaches.
    /// </summary>
    private bool _decodeGated;

    private void DecodeVideo(ReadOnlySpan<byte> frame, VideoCodec codec, uint rtpTimestamp)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Decode gating: a feed nobody is rendering — hidden tile, minimised window,
            // participant scrolled out of view — still receives RTP, but spending a core
            // decoding pictures that are thrown away is pure waste. The gate is checked
            // before the decoder ever sees the packet; EmitFrame checking its handler
            // after the decode would save nothing.
            if (FrameReady is null)
            {
                _decodeGated = true;
                return;
            }

            if (_decodeGated)
            {
                // Frames were skipped while gated, so the decoder's reference chain is
                // broken and everything until the next keyframe would be garbage. Ask
                // for one rather than decoding smear.
                _decodeGated = false;
                KeyframeNeeded?.Invoke(FeedId);
            }

            // Keyed by codec: a feed can carry more than one video stream, and each needs
            // its own decoder. SIPSorcery has already reassembled the access unit.
            var key = codec.ToString();

            if (!_videoTracks.TryGetValue(key, out var track))
            {
                track = new VideoTrack();
                _videoTracks[key] = track;

                _logger.LogInformation(
                    "Feed {FeedId}: video stream started ({Codec}, {Count} stream(s) on this feed).",
                    FeedId, codec, _videoTracks.Count);
            }

            try
            {
                track.Decoder ??= VideoDecoder.Create(codec, DecoderPolicy, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create a {Codec} decoder for feed {FeedId}.", codec, FeedId);
                return;
            }

            track.PacketsReceived++;
            track.BytesReceived += frame.Length;
            track.AccessUnits++;

            var decoder = track.Decoder;
            var hadFrame = decoder.HasDecodedFrame;
            var isPrimary = ReferenceEquals(track, FirstTrack());

            decoder.Decode(frame, (in VideoFrame decoded) =>
            {
                track.Width = decoded.Width;
                track.Height = decoded.Height;
                track.FramesDecoded++;

                IsActive = true;

                // Only the primary track reaches the renderer. A second track on the same
                // feed has its own resolution, so emitting both would resize the
                // renderer's bitmap and rebuild its scaler on alternate frames. In
                // practice the app publishes screen shares as a separate Janus feed, so
                // this only guards the degenerate case.
                if (!isPrimary)
                    return;

                if (decoded.Width != Width || decoded.Height != Height)
                {
                    Width = decoded.Width;
                    Height = decoded.Height;
                    ResolutionChanged?.Invoke(decoded.Width, decoded.Height);
                }

                FrameReady?.Invoke(this, in decoded);
            });

            // The GPU gave up mid-stream (device removed, surfaces exhausted, a profile it
            // will not take). Software decode always works, so rebuild on it and ask for a
            // keyframe — the new decoder has no reference frames.
            if (decoder.HardwareFailed)
            {
                DropHardwareDecoder(track);
                return;
            }

            // Still nothing after feeding a frame: we joined mid-GOP and need an IDR.
            if (!hadFrame && !decoder.HasDecodedFrame)
                KeyframeNeeded?.Invoke(FeedId);
        }
    }

    /// <summary>
    /// Hardware decode is abandoned for the whole feed, not just the track that failed:
    /// the cause is the device, and a second track would only rediscover it.
    /// </summary>
    private void DropHardwareDecoder(VideoTrack track)
    {
        _logger.LogWarning(
            "Feed {FeedId}: {Decoder} failed; continuing in software.", FeedId, track.Decoder?.DecoderName);

        _hardwareDecodeDisabled = true;

        track.Decoder?.Dispose();
        track.Decoder = null;

        KeyframeNeeded?.Invoke(FeedId);
    }

    private VideoTrack? FirstTrack()
    {
        foreach (var track in _videoTracks.Values)
            return track;

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            IsActive = false;

            foreach (var track in _videoTracks.Values)
                track.Dispose();

            _videoTracks.Clear();

            _audioDecoder?.Dispose();
            _audioResampler?.Dispose();
            _audioDecoder = null;
            _audioResampler = null;
        }
    }
}
