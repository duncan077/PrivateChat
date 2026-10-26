using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// Microphone capture through WASAPI.
/// <para>
/// Captures in the device's native shared-mode format and converts to 16-bit PCM.
/// Requesting a specific format would force WASAPI into exclusive mode or insert the
/// system resampler, both of which add latency; the audio pipeline resamples instead.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private short[] _conversionBuffer = [];
    private bool _disposed;

    public bool IsCapturing { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public event AudioCapturedHandler? SamplesCaptured;
    public event Action<string>? CaptureFailed;

    public WasapiAudioCapture(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsCapturing)
                return Task.CompletedTask;

            var enumerator = new MMDeviceEnumerator();
            try
            {
                _device = ResolveDevice(enumerator, deviceId);

                // 20ms period matches the Opus frame size, so the pipeline usually gets
                // exactly one frame per callback and needs no repacking.
                _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 20);

                SampleRate = _capture.WaveFormat.SampleRate;
                Channels = _capture.WaveFormat.Channels;

                _capture.DataAvailable += HandleDataAvailable;
                _capture.RecordingStopped += HandleRecordingStopped;

                _capture.StartRecording();
                IsCapturing = true;

                _logger.LogInformation(
                    "Microphone capture started on '{Device}' ({SampleRate}Hz {Channels}ch {Format}).",
                    _device.FriendlyName, SampleRate, Channels, _capture.WaveFormat.Encoding);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start microphone capture.");
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
                // The saved device is gone — fall through to the system default rather
                // than failing the call outright.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        var handler = SamplesCaptured;
        if (handler is null)
            return;

        try
        {
            var format = _capture?.WaveFormat;
            if (format is null)
                return;

            var samples = ConvertToPcm16(e.Buffer, e.BytesRecorded, format);
            if (samples.Length > 0)
                handler(samples, format.Channels, format.SampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Microphone capture callback failed.");
        }
    }

    /// <summary>
    /// Converts a WASAPI buffer to interleaved 16-bit PCM. Shared-mode devices almost
    /// always hand back 32-bit float, but 16-bit and 24-bit integer formats also occur.
    /// </summary>
    private ReadOnlySpan<short> ConvertToPcm16(byte[] buffer, int byteCount, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sampleCount = byteCount / sizeof(short);
            EnsureConversionBuffer(sampleCount);
            Buffer.BlockCopy(buffer, 0, _conversionBuffer, 0, byteCount);
            return _conversionBuffer.AsSpan(0, sampleCount);
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var sampleCount = byteCount / sizeof(float);
            EnsureConversionBuffer(sampleCount);

            var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, byteCount));
            for (var i = 0; i < sampleCount; i++)
            {
                // Clamp before scaling: WASAPI float samples can exceed ±1.0 and would
                // otherwise wrap around into loud noise.
                var value = Math.Clamp(floats[i], -1.0f, 1.0f);
                _conversionBuffer[i] = (short)(value * short.MaxValue);
            }

            return _conversionBuffer.AsSpan(0, sampleCount);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            var sampleCount = byteCount / 3;
            EnsureConversionBuffer(sampleCount);

            for (var i = 0; i < sampleCount; i++)
            {
                var offset = i * 3;
                // Little-endian 24-bit; take the top 16 bits.
                _conversionBuffer[i] = (short)(buffer[offset + 1] | (buffer[offset + 2] << 8));
            }

            return _conversionBuffer.AsSpan(0, sampleCount);
        }

        _logger.LogWarning(
            "Unsupported capture format {Encoding} {Bits}-bit; dropping audio.",
            format.Encoding, format.BitsPerSample);

        return ReadOnlySpan<short>.Empty;
    }

    private void EnsureConversionBuffer(int sampleCount)
    {
        if (_conversionBuffer.Length < sampleCount)
            _conversionBuffer = new short[sampleCount];
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
            return;

        _logger.LogError(e.Exception, "Microphone capture stopped unexpectedly.");
        CaptureFailed?.Invoke(e.Exception.Message);
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
        IsCapturing = false;

        if (_capture is not null)
        {
            _capture.DataAvailable -= HandleDataAvailable;
            _capture.RecordingStopped -= HandleRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StopRecording threw during teardown.");
            }

            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
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
