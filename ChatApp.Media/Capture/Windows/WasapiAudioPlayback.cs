using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// WASAPI playback for the mixed remote audio.
/// <para>
/// Fixed at 48 kHz stereo — the Opus decode format — so no resampling happens on the
/// playback path. The internal ring buffer is deliberately small: the jitter buffer
/// upstream already absorbs network variance, and a second large buffer here would only
/// add latency without improving robustness.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    private const int PlaybackSampleRate = 48_000;
    private const int PlaybackChannels = 2;

    /// <summary>
    /// 200 ms of slack. Beyond this the oldest audio is dropped: falling behind is far
    /// better than accumulating unbounded delay that never recovers.
    /// </summary>
    private const int MaxBufferedMilliseconds = 200;

    private readonly ILogger _logger;
    private readonly object _gate = new();

    private WasapiOut? _output;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volume;
    private byte[] _writeScratch = [];
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public int SampleRate => PlaybackSampleRate;
    public int Channels => PlaybackChannels;

    public double Volume
    {
        get => _volume?.Volume ?? 1.0;
        set
        {
            if (_volume is not null)
                _volume.Volume = (float)Math.Clamp(value, 0.0, 2.0);
        }
    }

    public WasapiAudioPlayback(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsPlaying)
                return Task.CompletedTask;

            var enumerator = new MMDeviceEnumerator();
            try
            {
                _device = ResolveDevice(enumerator, deviceId);

                var format = new WaveFormat(PlaybackSampleRate, 16, PlaybackChannels);

                _buffer = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(MaxBufferedMilliseconds * 2),
                    // Silence beats a glitch when the network stalls briefly.
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };

                _volume = new VolumeSampleProvider(_buffer.ToSampleProvider());

                _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                _output.Init(_volume);
                _output.Play();

                IsPlaying = true;

                _logger.LogInformation("Audio playback started on '{Device}'.", _device.FriendlyName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start audio playback.");
                CleanupUnsafe();
                throw;
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active)
                    return device;

                device.Dispose();
            }
            catch
            {
                // Saved speaker is gone; fall back to the system default.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
    }

    public void Write(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.IsEmpty)
            return;

        var buffer = _buffer;
        if (buffer is null || !IsPlaying)
            return;

        // Drop rather than queue when we are already behind: unbounded growth here
        // manifests as steadily increasing delay that never recovers.
        if (buffer.BufferedDuration.TotalMilliseconds > MaxBufferedMilliseconds)
        {
            buffer.ClearBuffer();
            _logger.LogDebug("Playback buffer overrun; dropped backlog to recover latency.");
        }

        var byteCount = interleaved.Length * sizeof(short);
        if (_writeScratch.Length < byteCount)
            _writeScratch = new byte[byteCount];

        MemoryMarshal.AsBytes(interleaved).CopyTo(_writeScratch);
        buffer.AddSamples(_writeScratch, 0, byteCount);
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            CleanupUnsafe();
        }

        return Task.CompletedTask;
    }

    private void CleanupUnsafe()
    {
        IsPlaying = false;

        if (_output is not null)
        {
            try
            {
                _output.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping playback threw during teardown.");
            }

            _output.Dispose();
            _output = null;
        }

        _device?.Dispose();
        _device = null;
        _buffer = null;
        _volume = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CleanupUnsafe();
        }
    }
}
