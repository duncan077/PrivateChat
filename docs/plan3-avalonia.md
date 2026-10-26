# Plan 3 — Finish the Avalonia native client

*Drafted 2026-08-06. Scope: bring the Avalonia client (`ChatApp.Core` / `ChatApp.Media` / `ChatApp.Ui` / `ChatApp.Desktop` / `ChatApp.Android`) to feature parity with the legacy MAUI Blazor client, wire-compatible with the existing ChatServer, with full NativeAOT on desktop and hardware-accelerated video decode.*

## Context

The Avalonia stack already solves the hardest problems: native WebRTC via SIPSorcery (ICE/DTLS/SRTP/RTP/RTCP), an FFmpeg codec ladder (NVENC → QSV → AMF → software encode; software decode), Opus audio with per-feed jitter-buffered mixing, and a well-engineered renderer (`ChatApp.Ui/Controls/VideoView.cs`). Desktop Windows calls, audio, and video work end-to-end today. What remains is UI surface, platform breadth, and a set of engine gaps the code itself documents.

**Decisions locked in:**

- **No VLC/LibVLCSharp.** It is a file/stream playback library; it cannot participate in a WebRTC session, and the current SIPSorcery → FFmpeg → swscale → `WriteableBitmap`/Skia pipeline is the right architecture. Video attachments in chat can reuse the FFmpeg we already ship.
- **Full NativeAOT is a requirement for the desktop heads.** Android stays on profiled Mono AOT (NativeAOT on Android is still experimental in .NET 10); all trim-cleanliness work transfers when that matures.
- **Hardware-accelerated decoding is in scope** (Phase 2), via FFmpeg hwaccel device contexts, not the wrapper decoders.

### How the current pipeline scales (baseline understanding)

- Render cost is proportional to **screen** pixels, not stream pixels: `VideoView` converts on the decode thread to the drawn tile size (quantized 16 px, never upscaling), with a single-slot frame handoff that drops rather than queues. Total conversion/upload work is bounded by monitor resolution regardless of participant count.
- The per-stream cost *was* **software decode at full source resolution**; since 2.1 shipped it is GPU decode plus an NV12 read-back, roughly half the CPU, with software still the automatic fallback per stream. Feeds decode in parallel across cores (one SIPSorcery receive thread each), and a software stream is ~one core max (`FF_THREAD_SLICE` + single-slice conferencing frames).
- Bandwidth *was* the worst of the three: a static 4 Mbps REMB per feed meant N feeds ≈ N × full bitrate with zero adaptation. Since 2.3 shipped, one controller estimates the downlink and shares it out, and the send side adapts to the SFU's reception reports.

Phase 2 attacked all three: hw decode (per-stream CPU), decode gating (waste), congestion control (bandwidth). All three have now shipped.

---

## Phase 1 — Desktop UI parity

The engine and stores already support almost all of this; the work is views/view-models.

1. **Settings dialog.** `CallStore` exposes ~30 persisted settings, device lists, and the voice-gate meter (`GetVoiceGateLevelAsync`); none are surfaced. Include a **server URL field** — `ChatApp.Core/Services/GrpcChannelProvider.cs:18` is hardcoded to `https://chat.duncan.com.ar`. Reference UI: `ChatAppBlazor/Components/Call/CallSettingsDialog.razor`.
2. **Screen-share UI**: toolbar button + target picker. Prerequisite fix: `FFmpegScreenCapture.EnumerateWindowsMonitors()` fabricates "Primary/Secondary monitor" with zero geometry — replace with real enumeration (`EnumDisplayMonitors`), and plan Windows Graphics Capture for per-window share.
3. **Register screen.** `LoginViewModel.GoToRegister()` raises `RegisterRequested`, which nobody subscribes to; there is no `RegisterView`. Build VM + view.
4. **DM UI.** `DirectMessageStore` (fully ported, registered) has no view. Contacts list, thread view, read receipts, unread badges, `OnPrivateCallIncoming` ring overlay + accept/decline into `MediaEngineHost.JoinDirectCallAsync` (engine path exists, unreachable today).
5. **Chat parity**: attachments (upload path exists in `ApiClientService`; picker + paste + drag-drop + inline image/video rendering), @mentions with autocomplete, message deletion, link previews, room create/rename/delete, moderation UI (kick / kick-from-call / move / set-role — all hub methods already wrapped in `ChatClientService`), members panel (`MembersPanelStore` exists, unconsumed).
6. **Notifications**: Windows `AppNotificationBuilder` + tap-to-navigate, per-room notification modes (All / MentionsOnly / Muted — persistence pattern in `ChatRoomStore` already ported), per-contact DM mutes.

**Exit criteria:** a Windows user can daily-drive the Avalonia client without falling back to the Blazor app for any text-chat or call-control feature.

---

## Phase 2 — Media engine

### 2.1 Hardware-accelerated decode (d3d11va / vaapi / mediacodec) — **shipped 2026-08-06**

FFmpeg **hwaccel device contexts** on the generic `h264`/`hevc`/`av1` decoders — *not* the wrapper decoders (`h264_cuvid` etc., whose internal surface queues are the latency the old code comment rightly feared). The decode loop stayed synchronous, `AV_CODEC_FLAG_LOW_DELAY` stayed set, and with no B-frames there is nothing to reorder.

As built:
- Candidate ladder moved to `VideoEncoderPolicy.GetDecoderCandidates` (Core, unit-tested): `h264+d3d11va` → `h264` on Windows, `+vaapi` on Linux, `*_mediacodec` on Android, `av1+d3d11va` → `libdav1d` → `av1`. VP8 stays software. Every ladder ends in software.
- `av_hwdevice_ctx_create()` per decoder (so per feed), `avcodec_get_hw_config` confirms the decoder/device pair before the device is opened.
- `get_format` as `[UnmanagedCallersOnly]` + function pointer, wanted format carried in `AVCodecContext.opaque`; when the GPU is not offered for a stream it takes the first non-hwaccel format instead → per-stream software fallback, and `DecoderName` becomes `h264 (hardware declined)`.
- Read-back writes straight into one contiguous, tightly-strided NV12 buffer (`av_buffer_create` over `av_malloc`, plane pointers laid out by hand) — `av_hwframe_transfer_data` allocates per-plane if handed a bare frame, which would have cost a repacking pass. Software frames still pack to I420 (or NV12 from mediacodec), because decoders allocate each plane from its own pool.
- **Trap worth remembering** (`Nv12Layout`, with tests): an NV12 chroma row is `2 * ceil(width / 2)` bytes — one byte *wider* than the luma row at odd widths. FFmpeg checks the destination stride with `av_assert0`, which is compiled into release builds and calls `abort()`, so a short stride is a process kill rather than a bad frame. H.264/HEVC 4:2:0 can only signal even widths, but AV1 and VP9 publishers can signal any width.
- Mid-stream failure — a read-back error, non-NV12 surfaces, or 30 consecutive decode errors — sets `HardwareFailed`; `RemoteFeed` drops the decoder, disables hardware for the whole feed, rebuilds in software and requests a keyframe.
- `GpuAccelerationEnabled` already mapped to `AllowHardwareDecode`, so the settings toggle now controls decode too (next launch — the engine reads the policy once).
- **Deferred:** zero-copy GPU rendering (decoded texture → Skia via `ICustomDrawOperation` + SkiaSharp API lease). Only worth pursuing if the NV12 read-backs show up at real call sizes.

Measured on the dev machine, 1080p H.264, 120 frames: software `h264` 3.39 ms CPU/frame, `h264+d3d11va` 1.82 ms CPU/frame — a little under half, with the read-back now the dominant remaining cost. Wall-clock per frame goes the other way (0.5 ms → 1.7 ms) because software decode spreads over slice threads while the hardware path is one submit plus a synchronous read-back; at 30 fps on a per-feed thread that is not close to a budget.

Still to validate: CPU comparison on a real 4–6 participant call, and a 1080p60 screen-share latency soak.

### 2.2 Decode gating (cheap scaling win) — **shipped 2026-08-06**

`RemoteFeed` gates `avcodec_send_packet` on having a `FrameReady` subscriber, and requests a PLI when one re-attaches (the reference chain is broken by then). A hidden tile now creates no decoder at all.

### 2.3 Congestion control — **shipped 2026-08-06**

The static 4 Mbps REMB is gone. One AIMD controller (`BandwidthEstimator`, unit-tested) runs on both legs:

- **Receive side.** One estimator for the whole downlink, not one per feed — every feed arrives over the same link, and per-feed estimators were exactly how N participants came to demand N × 4 Mbps. It measures RTP bytes and packet-weighted loss across all subscribers once a second, then splits the total across the feeds with video (floor 120 kbps each) and sends each its share as REMB. Ceiling raised 4 → 20 Mbps, which is safe now that it adapts.
- **Send side.** Driven by the SFU's reception reports on our publisher (the leg we can actually do something about), applying to `VideoEncoder.SetBitrate` and mirrored to Janus's own publisher cap. The configured bitrate became a **ceiling the controller works under**, not a value applied directly; the encoder now opens at the controller's current target.
- **Opens at 80% of the ceiling**, not at it: the initial estimate is used before anything has been measured, and granting the full cap from the first frame bursts into a link of unknown capacity. One step of growth closes that gap, so a share reaches its configured bitrate about a second in. (An earlier revision opened at a fixed 2500 kbps and took ~20 s to climb — too long to be worth the caution, given the gap is what bounds the overshoot.)
- **Thresholds.** Grow below 2% loss, hold to 5%, cut above it — tighter than the browser's 10%, because browsers pair the loss controller with a delay-gradient one and we have only loss. Cuts back off from the rate that *got through*, not from the estimate, or the controller converges just above capacity and holds there calling steady loss healthy.
- **Capacity memory.** A cut records what the link actually carried. Below 85% of that, growth is fast (×1.5, clamped so a step cannot leap past the measured figure) because it is a return to known-good territory; at or above it, ×1.08, because beyond the measurement nothing is known again.
- **Growth is rationed per report, not per tick.** The loops run every second; RTCP answers every five. Unrationed, the ticks in between compound ×7 before anything can contradict them — a sawtooth with ~20% loss, which is what the first cut of this actually did in simulation. Each fresh report authorises one step's worth of growth and the ticks may deliver it but not exceed it; a cut authorises nothing until the next report says whether it worked.
- **Jitter** is a second, weaker signal: raised jitter withholds growth, and only jitter that is both extreme and still climbing forces a cut. Reports are also consumed once for cuts — acting repeatedly on one bad report collapsed the target far below what the loss justified.

Simulated against modelled links (controller at 1 Hz, reports held between 5 s boundaries the way the real loop sees them): a 12 Mbps share on a fat link is at its ceiling within a second; a 6 Mbps uplink settles at 6.09 Mbps at 1.5% loss with **zero ripple**; a link collapsing 20 → 2 Mbps settles at 2.09 Mbps at 4.3%.

**Known limitation.** Recovering after a dip that has since cleared is slow — ~85 s back to a 12 Mbps ceiling from a 3 Mbps dip — because past the measured capacity growth is ×1.08 per *report*, and `RTCP_MINIMUM_REPORT_PERIOD_MILLISECONDS` is a private const of 5000 inside SIPSorcery. Two ways out, both still open: transport-wide CC (the stretch goal) replaces the jitter proxy with a real delay signal and brings upward probing with it, or a SIPSorcery fork shortens the report period. Not measured on a live call yet.

### 2.4 System / screen-share audio

Port `ChatAppBlazor/Platforms/Windows/WindowsScreenShareAudioFallback.cs` (1,418 lines — WASAPI loopback, per-process isolation via `ApplicationLoopback.dll`): capture → Opus → publish as the second `screen-audio` Janus feed, matching the Blazor client's model. **Dependency:** build this on the post-NAudio audio backend from Phase 3 (per-process loopback activation is COM — it must use the same interop approach), so sequence Phase 3.1 before this item.

### 2.5 Honest stats & applied settings

- `GetStatsAsync` returns all zeros — populate from SIPSorcery RTCP (bitrate, fps, loss, jitter, RTT) and show codec + hw/sw decode path per tile.
- Apply the settings that are silently ignored today: `MicrophoneVolume`, voice gate actually gating (threshold is currently read only to report the meter), `NoiseSuppressionEnabled` (see 2.6), resolution/codec changes without engine restart (`ApplySettingsAsync` only retunes bitrate).

### 2.6 Noise suppression & push-to-talk

- DTLN natively: the Blazor client runs `dtln_rs` as wasm in an AudioWorklet — same model, Rust crate, small FFI binding into the mic pipeline, honoring `NoiseSuppressionEnabled`.
- Global PTT: port `ChatAppBlazor/Platforms/Windows/GlobalKeyboardPttHook.cs` (`WH_KEYBOARD_LL`) nearly 1:1; implement actual PTT gating in the engine (`SetPushToTalkAsync` currently only fires the event).

---

## Phase 3 — NativeAOT enablement (desktop)

Already AOT-friendly: Avalonia 12 (NativeAOT supported, compiled bindings on), CommunityToolkit.Mvvm source-gen, MagicOnion + MessagePack source-generated clients (`MagicOnionClientGeneration.cs`, `ConfigureMessagePack()` — built to survive IL2CPP), Grpc.Net.Client, managed BouncyCastle/Concentus, FFmpeg.AutoGen `DynamicallyLoadedBindings` (concrete-typed delegate marshalling — verify early).

1. **Audio backend replacement — the one hard blocker.** `NAudio.Wasapi` is classic `[ComImport]` COM, unsupported on NativeAOT. Chosen direction: **miniaudio via plain P/Invoke** — one native library covering WASAPI (incl. loopback), ALSA/PulseAudio, and AAudio, which also delivers the Phase 5 Linux and Android audio backends. The engine already isolates audio behind capture/playback interfaces, so the swap is contained. Alternative if miniaudio disappoints: source-generated COM (`[GeneratedComInterface]`) WASAPI layer, Windows-only. Per-process loopback (2.4) rides on this choice.
2. **Replace WMI.** `System.Management` is unsupported on NativeAOT; `ChatApp.Desktop/WindowsPlatformServices.cs:82` uses it for GPU enumeration. Replace with DXGI via source-generated COM or a registry read (it only feeds encoder-policy naming).
3. **Replace `ViewLocator` reflection** (`Type.GetType` + `Activator.CreateInstance`, flagged `[RequiresUnreferencedCode]`) with an explicit VM → view `switch`.
4. **Source-generate all System.Text.Json usage**: `JsonFilePreferences`, `FileSecureStore`, `ChatRoomStore`, `DirectMessageStore`, `ApiClientService` REST payloads. Template: the Blazor app's `Serialization/AppJsonContext.cs`.
5. **SIPSorcery trim/AOT audit.** No known blocker (no Newtonsoft; deps are managed), but it is unannotated; likeliest surprises in DNS/mDNS (DnsClient, Makaretu.Dns). Audit warnings, add dynamic-dependency roots as needed, runtime smoke test.
6. **Process**: add `<IsAotCompatible>true</IsAotCompatible>` to ChatApp.Core/Media/Ui (enables analyzers), `<PublishAot>true</PublishAot>` to ChatApp.Desktop; fix warnings; CI job: AOT publish → boot → join call smoke test. Keep `AvaloniaUI.DiagnosticsSupport` Debug-only.

Suggested order inside the phase: items 3–4 first (days), item 2 (a day), item 1 as its own work item (1–2 weeks incl. loopback), then the analyzer sweep.

---

## Phase 4 — Screen annotations

1. **Viewer side**: Avalonia canvas control replicating `ChatAppBlazor/wwwroot/js/screen-annotation.js` — the 0..1 frame-normalized coordinate model with letterbox/zoom compensation, 45 ms point batching, tools (pen/highlighter/arrow/rect/ellipse), undo/clearMine, auto-fade — wired to the existing hub methods (`SetScreenAnnotationStateAsync`, `SendScreenAnnotationAsync`, `GetScreenAnnotationSessionsAsync` + private variants; receiver callbacks already land in `ChatClientService`).
2. **Sharer side**: port `AnnotationOverlayWindow.cs` (Win32 layered click-through window, own thread + message pump) and `AnnotationRasterizer.cs` (software BGRA rasterizer) from `ChatAppBlazor/Platforms/Windows/` — raw Win32, ports with minimal change, no AOT concerns (P/Invoke only).
3. Toolbar UI equivalent of `AnnotationToolbar.razor`; re-sync sessions after hub reconnect (logic exists in the Blazor `CallStore.Annotations.cs` as reference).

---

## Phase 5 — Platform breadth

### 5.1 Linux desktop

- Register capture backends on the non-Windows branch (currently only `NullGpuInfoProvider` — `NativeMediaEngine` construction throws). Camera: existing FFmpeg `v4l2` path. Screen: PipeWire/portal (`ashpd`-equivalent via xdg-desktop-portal DBus) with `x11grab` fallback. Audio: miniaudio backend from Phase 3.
- Stage `linux-x64` FFmpeg in `tools/fetch-ffmpeg.ps1` (only `win-x64` staged today); vaapi decode from 2.1.
- Rename the misnamed `WindowsPlatformServices.cs` (it is the shared desktop registrar with a Windows branch).
- Secure storage: current `FileSecureStore` on Linux is owner-only file perms (README flags this); consider libsecret.

### 5.2 Android (its own mini-project)

- The head is a stock template: **nothing assigns `App.ConfigurePlatformServices`** — no media engine, calls disabled by design. Wire platform registration.
- Manifest: add `CAMERA`, `RECORD_AUDIO`, `POST_NOTIFICATIONS`, foreground-service (media projection) permissions + runtime prompts (today only `INTERNET`).
- Natives: cross-compile FFmpeg for `android-arm64` with `*_mediacodec` decoders + mediacodec encode candidates; extend `fetch-ffmpeg.ps1`.
- Audio: miniaudio/AAudio backend; camera via FFmpeg or Camera2 → NV12 into the existing encoder path.
- AOT/trim: profiled Mono AOT; the `ViewLocator` fix and JSON source-gen from Phase 3 remove the trimming hazards flagged for this head.

### 5.3 FFmpeg licensing (blocker for public distribution)

Decide before shipping binaries: GPL build (has libx264/x265 software fallback, GPL obligations) vs LGPL build (`fetch-ffmpeg.ps1 -Lgpl`, hardware-encode-only — machines without NVENC/QSV/AMF/MF cannot send video). Document the choice; hw decode (2.1) softens the LGPL path's receive-side story but not send-side.

---

## Phase 6 — Ship loop & hygiene

1. **Self-updater** against the existing `GET /api/update?platform=…` (server already serves per-platform URLs; Blazor `UpdateService.cs` is the reference — zip + swap-on-restart script on Windows, browser download on Android).
2. **Hygiene backlog** (small, known):
   - `ShellViewModel.SyncFromStore` clears + re-adds the whole `ObservableCollection` per store change — incremental sync.
   - `.GetAwaiter().GetResult()` inside `NativeMediaEngine.StartAsync` path (lines ~168/176) — make properly async.
   - `ChatClientService` reconnect logging via `Console.WriteLine` — invisible in a WinExe; route through `FileLogger`/`ILogger`.
   - Two `async void` store-event handlers in `MediaEngineHost` (contained, but convert).
   - Stale "libdatachannel" comments (6 sites: `IMediaEngine.cs`, `OpusCodec.cs`, `VideoEncoder.cs` ×2, `NativeMediaEngine.cs`, `RemoteFeed.cs`, `ChatApp.Android.csproj`) — now reference SIPSorcery.
   - `AuthClientFilter` placeholder pass-through — implement or delete.
   - Delete stray SQLite leftovers if truly unused; `artifacts/DataChannelDotnet-ref` is gitignored dead weight, keep or archive deliberately.
3. **Test gaps**: `FFmpegFactAttribute` skips (not fails) when FFmpeg is absent — make CI stage FFmpeg so codec round-trips actually run. (Ladder and decode-gating tests landed with 2.1: `VideoDecoderPolicyTests`, `RemoteFeedTests`.)

---

## Ordering & dependencies

```
Phase 1 (UI parity)  ── done ──────┐
Phase 3.3/3.4 (ViewLocator, STJ) ──┤  independent, start immediately
Phase 2.1/2.2 (hw decode, gating) ─┤  done
                                   │
Phase 3.1 (miniaudio backend) ─────┼─→ Phase 2.4 (loopback share audio)
                                   ├─→ Phase 5.1 (Linux audio)
                                   └─→ Phase 5.2 (Android audio)
Phase 2.3 (congestion control) — independent
Phase 4 (annotations) — after Phase 1 call UI stabilizes
Phase 5.3 (licensing) — decide before any public release
```

## Risks

| Risk | Mitigation |
|---|---|
| Android media (largest unknown) | Scoped as its own mini-project; everything else ships without it |
| SIPSorcery under NativeAOT unproven | Early analyzer sweep + smoke test (Phase 3.6 first week) |
| hwaccel driver variance (esp. vaapi, AV1) | `get_format` software fallback per stream; user-facing toggle |
| miniaudio loopback/per-process capture gaps on Windows | Fallback: `[GeneratedComInterface]` WASAPI layer for the loopback path only |
| FFmpeg GPL vs LGPL | Explicit decision gate before distribution (5.3) |

## Non-goals

- VLC/LibVLCSharp integration.
- Zero-copy GPU rendering (revisit only after 2.1 profiling).
- Janus simulcast (biggest lever for very large rooms, but requires layered publisher encoding — reconsider if room sizes grow beyond ~12 video feeds).
- Server changes: the client stays wire-compatible with ChatServer (MagicOnion 7.10 contract in `Shared/`) and with the Blazor fleet.
