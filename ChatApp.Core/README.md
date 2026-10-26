# Avalonia rewrite — architecture

Replaces the MAUI Blazor Hybrid client (`ChatAppBlazor`) with a native Avalonia app.
`ChatServer` and `Shared` are unchanged; the new client is wire-compatible with the
existing MagicOnion hub and Janus VideoRoom signalling.

## Projects

| Project | Purpose |
|---|---|
| `ChatApp.Core` | Platform-agnostic services + state stores. No UI, no JS, no MAUI. |
| `ChatApp.Media` | Capture → encode → RTP → Janus, and the reverse. Media engine. |
| `ChatApp.Ui` | Shared Avalonia views and view models. |
| `ChatApp.Desktop` | Windows / Linux head. |
| `ChatApp.Android` | Android head. |

Target platforms are **Windows, Linux and Android**. macOS and iOS are out of scope —
no Apple heads, no VideoToolbox encoders, no `osx-*`/`ios-*` native assets.

`ChatApp.Ui` is deliberately not named `ChatApp.Avalonia`: a namespace segment called
`Avalonia` shadows the framework's own root namespace inside the assembly and breaks
every `using Avalonia.*` in it.

## Transport: SIPSorcery

ICE, DTLS, SRTP, RTP and RTCP come from **SIPSorcery**, in pure C#. It also depacketizes
inbound media and packetizes outbound, so `OnVideoFrameReceived` delivers whole access
units and `SendVideo`/`SendAudio` take encoded frames.

This replaced libdatachannel, which needed a vendored binding fork plus per-RID natives
and silently omitted three things that each cost a debugging cycle:

- **No inbound depacketization.** Its `…PacketizationHandler` entry points are aliases
  for the outgoing packetizers, so raw RTP reached the decoder. An RTP header's first
  byte, `0x90`, is a valid Opus TOC for a 10 ms frame — so it decoded successfully into
  noise at exactly half the expected sample rate.
- **No RTCP receiving session exposed.** With no Receiver Reports or REMB going back, a
  browser publishing 1080p throttled itself to 34 kbps with its limiter reading
  "bandwidth".
- **Nothing reporting the negotiated codec or channel count**, so both had to be parsed
  out of SDP by hand and were wrong whenever the sender chose differently.

SIPSorcery reports all of that through `OnVideoFormatsNegotiated` and
`OnAudioFormatsNegotiated`.

Two API details worth remembering:

- `SendVideo`/`SendAudio` take a frame **duration** in RTP units, not an absolute
  timestamp — `90000/fps` for video, `960` for a 20 ms Opus frame.
- A subscriber must add **recvonly tracks before `setRemoteDescription`**. SIPSorcery
  answers by matching the offer's m-lines against tracks it already has; with none, it
  rejects the whole offer as `NoMatchingMediaType`. The recvonly video track declares
  every codec we can decode, so one subscriber handles an H.264 camera and a VP8 screen
  share.

Still not provided by anything in the stack: **congestion control** (no GCC/TWCC
estimator). Janus's REMB is the practical substitute — read it and retune the encoder.

## Native dependencies

FFmpeg is now the **only** native dependency, which removes most of the cross-compile
work for Android and arm64.

### FFmpeg

`FFmpeg.AutoGen` 8.1.0 ships **bindings only** and binds FFmpeg **8.1**. It needs the
**shared** libraries — a static build will not work, as it contains only `ffmpeg.exe`
and no DLLs. (Note: `winget install Gyan.FFmpeg` produces a static build.)

Fetch them with:

```powershell
pwsh tools/fetch-ffmpeg.ps1
```

That stages ~168 MB into `third_party/ffmpeg/win-x64/`, which is gitignored.
`ChatApp.Media.csproj` copies them to `runtimes/<rid>/native/` in the build output.

`avdevice` and `avfilter` are included because capture goes through FFmpeg:

| Path | Backend | Why |
|---|---|---|
| Microphone | WASAPI (NAudio) | latency, AEC reference signal, stable endpoint IDs |
| System / per-process audio | WASAPI loopback | FFmpeg's `dshow` has no loopback or process capture |
| Camera | FFmpeg `dshow` / `v4l2` | avoids Media Foundation interop, cross-platform |
| Screen | FFmpeg `ddagrab` / `x11grab` | DXGI Desktop Duplication without D3D11 interop |

`ddagrab` is a *filter*, not a demuxer, so it is driven through the `lavfi` input device
(`ddagrab=output_idx=0:framerate=N,hwdownload,format=bgra`) — hence the `avfilter`
dependency. It emits D3D11 surfaces; `hwdownload` brings them to system memory. A future
zero-copy path could feed those surfaces straight to a hardware encoder.

Camera enumeration uses `avdevice_list_input_sources` rather than parsing
`-list_devices` stderr, which yields dshow's stable `@device_sw_{…}` paths instead of
friendly names — necessary because the selected camera is persisted and two identical
webcams share a friendly name.

The build succeeds without them; `FFmpegLoader.Initialize()` never throws and instead
returns an availability record with an actionable diagnostic, so the chat features still
run and only calls fail, with an explanation. Set `FFmpegLoader.OverridePath` to use
libraries from elsewhere.

> **Licensing.** The default fetch is the **GPL** build, because software H.264/H.265
> encoding needs libx264/libx265, which are GPL-only. Distributing ChatFlow linked
> against it carries GPL obligations. `tools/fetch-ffmpeg.ps1 -Lgpl` fetches an LGPL
> build instead, but it has no x264/x265 — software H.264 encoding disappears and only
> the hardware encoders (NVENC/QSV/AMF/MediaFoundation) work, so machines without a
> supported GPU would be unable to send video.

## Rendering

`VideoView` (in `ChatApp.Ui/Controls`) replaces the `<video>` elements the WebView used to
own. It binds to an `IVideoSource` — a remote feed, the local camera, or a screen share —
and every tile in the call grid is the same control.

Frames arrive on a decode thread and their buffers do not outlive the callback, so each
one is converted to BGRA **on that thread** and handed to the UI thread through a
single-slot swap. If the UI falls behind, the waiting frame is overwritten rather than
queued: for live video the newest frame is the only one worth drawing. Two buffers
circulate in the steady state and neither side blocks on the other for longer than a
pointer swap.

Conversion targets the size the control will actually draw, not the source size. A 1080p
feed in a 480 px tile scales once inside swscale rather than moving four times the pixels
through the bitmap upload and the GPU. Target widths are quantized to 16 px so dragging a
window edge does not rebuild the scaler on every pixel, and the frame is never scaled
*up* — the GPU stretches the smaller bitmap for free.

That work lives behind `IVideoFrameConverter`, implemented by `SwsVideoFrameConverter`, so
`ChatApp.Ui` never references FFmpeg. Heads register the factory; a head without one shows
placeholder tiles, which is the honest outcome since without FFmpeg nothing is decoding
either.

Two behaviours worth knowing:

- A tile falls back to its avatar after **2 s without a frame**. Janus simply stops sending
  when a participant switches their camera off, so without this the last frame of them
  would sit there for the rest of the call.
- Local camera tiles are **mirrored**; screen shares are not, or their text would come out
  backwards. The preview is taken from the capture backend rather than round-tripped
  through the encoder, so it still moves when encoding is failing — which is exactly when
  you want to know the camera itself is fine.

`HideLocalPreview` suppresses the frames but keeps the tile, so you see your avatar in your
own slot rather than losing your place in the grid.

## Platform seams

`ChatApp.Core` owns no platform code. Heads supply implementations of:

- `IAppPreferences` — replaces MAUI `Preferences.Default`
- `ISecureStore` — replaces MAUI `SecureStorage.Default`
- `IGpuInfoProvider` — replaces the inline `#if WINDOWS` WMI query in `CallStore`
- `IMediaEngine` — replaces `WebRtcBridge` + `webrtc-manager.js`

Desktop defaults (`JsonFilePreferences`, `FileSecureStore`) ship in `ChatApp.Core`.

`FileSecureStore` encrypts with DPAPI on Windows. On Linux it falls back to an
owner-only file — that stops other local users, but not anything running as this user.
Swap in libsecret if that matters.

## Settings that changed meaning

The Blazor client turned GPU preferences into Chromium switches for WebView2
(`--enable-accelerated-video-encode`, `PlatformHEVCEncoderSupport`,
`--force-high-performance-gpu`). There is no browser now. The same user-facing settings
are preserved and reinterpreted by `CallStore.BuildVideoEncoderPolicy()` into
`VideoEncoderPolicy`, which picks the FFmpeg encoder chain (NVENC → QSV → AMF →
software, and so on per platform).
