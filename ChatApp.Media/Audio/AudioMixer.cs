using System.Collections.Concurrent;

namespace ChatApp.Media.Audio;

/// <summary>Snapshot of one source's mixer state.</summary>
public readonly record struct MixerSourceDiagnostics(
    long FeedId,
    Guid UserId,
    int BufferedSamples,
    long Underruns,
    double Gain,
    long SubmittedSamples,
    long ConsumedSamples);

/// <summary>
/// Mixes the decoded audio of every remote participant into a single playback stream.
/// <para>
/// Each source owns a ring buffer that the decode path writes into and the playback pump
/// drains. Buffering per source rather than mixing on arrival is what lets participants
/// whose packets arrive at slightly different times still be summed sample-aligned.
/// </para>
/// <para>
/// Sources are keyed by <b>feed</b>, not participant. One person can publish several
/// audio streams at once — a camera feed and a screen share, or a stale feed left behind
/// by a reconnect — and merging them into one ring makes it fill at twice the rate the
/// pump drains it, pinning the buffer at its cap and adding a fixed delay. Volume is
/// still applied per participant.
/// </para>
/// <para>
/// The ring is intentionally shallow. It absorbs ordinary network jitter, and when a
/// source runs far ahead the oldest audio is discarded — for a live call, dropping is
/// always better than accumulating delay that never recovers.
/// </para>
/// </summary>
public sealed class AudioMixer
{
    private readonly int _channels;
    private readonly int _capacityPerSource;
    private readonly ConcurrentDictionary<long, Source> _sources = new();

    /// <summary>Per-participant gain, retained so it applies to feeds that arrive later.</summary>
    private readonly ConcurrentDictionary<Guid, float> _gains = new();
    private int[] _accumulator = [];

    public AudioMixer(int channels = 2, int bufferedMilliseconds = 120, int sampleRate = 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _channels = channels;
        _capacityPerSource = sampleRate * bufferedMilliseconds / 1000 * channels;
    }

    public int SourceCount => _sources.Count;

    /// <summary>Per-source buffer depth, underrun and throughput counters, for diagnostics.</summary>
    public IReadOnlyList<MixerSourceDiagnostics> GetDiagnostics()
        => _sources
            .Select(kv => new MixerSourceDiagnostics(
                kv.Key,
                kv.Value.UserId,
                kv.Value.Buffered,
                kv.Value.Underruns,
                kv.Value.Gain,
                kv.Value.SubmittedSamples,
                kv.Value.ConsumedSamples))
            .ToArray();

    /// <summary>
    /// Playback gain for a participant. Applies to every feed they publish, including
    /// ones that arrive after the gain was set.
    /// </summary>
    public void SetGain(Guid userId, double gain)
    {
        var clamped = (float)Math.Clamp(gain, 0.0, 4.0);
        _gains[userId] = clamped;

        foreach (var source in _sources.Values)
        {
            if (source.UserId == userId)
                source.Gain = clamped;
        }
    }

    public double GetGain(Guid userId) => _gains.TryGetValue(userId, out var gain) ? gain : 1.0;

    /// <summary>Queues decoded PCM from one feed.</summary>
    public void Submit(long feedId, Guid userId, ReadOnlySpan<short> interleaved)
    {
        if (interleaved.IsEmpty)
            return;

        var source = _sources.GetOrAdd(feedId, _ => new Source(_capacityPerSource, userId)
        {
            Gain = _gains.TryGetValue(userId, out var gain) ? gain : 1.0f
        });

        source.Write(interleaved);
    }

    public void RemoveSource(long feedId) => _sources.TryRemove(feedId, out _);

    /// <summary>Drops every feed belonging to a participant who left.</summary>
    public void RemoveUser(Guid userId)
    {
        foreach (var (feedId, source) in _sources)
        {
            if (source.UserId == userId)
                _sources.TryRemove(feedId, out _);
        }
    }

    public void Clear()
    {
        _sources.Clear();
        _gains.Clear();
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the summed mix and reports whether any
    /// source contributed. Sources with nothing buffered contribute silence rather than
    /// stalling the pump.
    /// </summary>
    public bool MixInto(Span<short> destination)
    {
        destination.Clear();

        if (_sources.IsEmpty)
            return false;

        if (_accumulator.Length < destination.Length)
            _accumulator = new int[destination.Length];

        var accumulator = _accumulator.AsSpan(0, destination.Length);
        accumulator.Clear();

        var contributed = false;

        foreach (var source in _sources.Values)
        {
            if (source.Gain <= 0f)
                continue;

            if (source.MixInto(accumulator))
                contributed = true;
        }

        if (!contributed)
            return false;

        // Sum in 32-bit then clamp. Summing directly in int16 would wrap on overlap and
        // turn two people talking at once into loud crackling.
        for (var i = 0; i < destination.Length; i++)
            destination[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);

        return true;
    }

    /// <summary>
    /// One participant's ring buffer, with priming.
    /// <para>
    /// A source contributes only whole frames. Emitting a partially-filled frame padded
    /// with silence — which is what happens if you just take whatever is buffered —
    /// creates a hard discontinuity on every pump tick, heard as a loud buzz at the pump
    /// rate. Network jitter means 20 ms packets never align perfectly with 20 ms ticks,
    /// so that case is the norm rather than an edge case.
    /// </para>
    /// <para>
    /// Priming builds a small cushion before playback starts and rebuilds it after an
    /// underrun, so a source that is briefly starved goes quiet cleanly instead of
    /// stuttering in and out.
    /// </para>
    /// </summary>
    private sealed class Source(int capacity, Guid userId)
    {
        public Guid UserId { get; } = userId;

        /// <summary>
        /// Frames of cushion held before playback starts. Absorbs the jitter between
        /// packet arrival and the pump's fixed cadence.
        /// </summary>
        private const int PrimeFrames = 3;

        /// <summary>
        /// Consecutive starved ticks before the cushion is rebuilt. Half a second of
        /// nothing means the participant genuinely stopped sending — WebRTC uses
        /// discontinuous transmission, so a silent speaker legitimately sends no packets
        /// — and resuming should re-establish the cushion rather than start on empty.
        /// </summary>
        private const int SustainedUnderrunsBeforeReprime = 25;

        private readonly short[] _buffer = new short[capacity];
        private readonly Lock _gate = new();
        private int _readIndex;
        private int _count;
        private bool _primed;
        private int _consecutiveUnderruns;

        public float Gain = 1.0f;

        /// <summary>Interleaved samples currently buffered. Diagnostics only.</summary>
        public int Buffered
        {
            get { lock (_gate) return _count; }
        }

        /// <summary>Frames dropped because the source had no whole frame ready.</summary>
        public long Underruns { get; private set; }

        /// <summary>
        /// Total interleaved samples written in and read out. Comparing the two is the
        /// only way to distinguish "the network is delivering too little" from "the mixer
        /// is consuming it wrongly" — they look identical from the underrun count alone.
        /// </summary>
        public long SubmittedSamples { get; private set; }

        public long ConsumedSamples { get; private set; }

        public void Write(ReadOnlySpan<short> samples)
        {
            lock (_gate)
            {
                SubmittedSamples += samples.Length;

                // Overrun: this source is ahead of playback. Drop its backlog so it
                // resyncs at the live edge instead of drifting permanently late.
                if (samples.Length >= _buffer.Length)
                {
                    samples = samples[^_buffer.Length..];
                    _readIndex = 0;
                    _count = 0;
                }
                else if (_count + samples.Length > _buffer.Length)
                {
                    var overflow = _count + samples.Length - _buffer.Length;
                    _readIndex = (_readIndex + overflow) % _buffer.Length;
                    _count -= overflow;
                }

                var writeIndex = (_readIndex + _count) % _buffer.Length;
                var firstChunk = Math.Min(samples.Length, _buffer.Length - writeIndex);

                samples[..firstChunk].CopyTo(_buffer.AsSpan(writeIndex));
                if (firstChunk < samples.Length)
                    samples[firstChunk..].CopyTo(_buffer.AsSpan(0));

                _count += samples.Length;
            }
        }

        /// <summary>Adds this source's next samples into the accumulator, applying gain.</summary>
        public bool MixInto(Span<int> accumulator)
        {
            lock (_gate)
            {
                // No early return for an empty buffer: a fully-starved source is the most
                // common underrun, and short-circuiting here would skip both the counter
                // and the un-priming, leaving the source to resume on a single partial
                // frame instead of rebuilding its cushion.
                var frameLength = accumulator.Length;

                // Build a cushion before starting. Packets arrive at the same average rate
                // the pump drains them, so there is no surplus to grow this later — the
                // cushion has to be established while the source is still silent.
                if (!_primed)
                {
                    // Clamped to the ring size: a buffer configured smaller than the
                    // cushion would otherwise never prime and the source would be
                    // permanently silent.
                    var primeTarget = Math.Min(frameLength * PrimeFrames, _buffer.Length);

                    if (_count < primeTarget)
                        return false;

                    _primed = true;
                    _consecutiveUnderruns = 0;
                }

                // Only whole frames. A partial fill would leave the tail of the frame as
                // abrupt silence and buzz at the pump rate.
                if (_count < frameLength)
                {
                    Underruns++;

                    // Stay primed through an isolated miss. Dropping back to unprimed
                    // would force the whole cushion to be rebuilt, costing several silent
                    // ticks for one late packet — that turns ordinary jitter into a
                    // repeating gap pattern, which is audible as a buzz.
                    if (++_consecutiveUnderruns >= SustainedUnderrunsBeforeReprime)
                        _primed = false;

                    return false;
                }

                _consecutiveUnderruns = 0;

                var gain = Gain;

                for (var i = 0; i < frameLength; i++)
                {
                    var sample = _buffer[(_readIndex + i) % _buffer.Length];
                    accumulator[i] += (int)(sample * gain);
                }

                _readIndex = (_readIndex + frameLength) % _buffer.Length;
                _count -= frameLength;
                ConsumedSamples += frameLength;

                return true;
            }
        }
    }
}
