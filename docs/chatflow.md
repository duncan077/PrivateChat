# ChatFlow

**Your team's chat, calls and screen sharing — on your own server.**

ChatFlow is a self-hosted chat platform with real-time voice, video and
collaborative screen sharing. One small server binary, native apps for Windows
and Android, and every message stored in a database **you** control. No
subscriptions, no third-party cloud, no data mining.

---

## The problems it solves

**Your conversations live on someone else's computer.** With mainstream chat
platforms, your team's entire history — messages, files, calls metadata — sits
in a vendor's cloud, governed by their terms and their retention rules.
ChatFlow runs on your hardware: a Raspberry-Pi-class ARM box or any Linux
server is enough. The database is yours to back up, migrate, or delete.

**Screen sharing that loses the sound.** Most tools either can't capture
system audio on Windows, or capture everything including your microphone echo.
ChatFlow's Windows client captures true system audio with the share — and can
isolate audio to just the shared application, so the music in your headphones
stays private while the game or video you're presenting is heard.

**"Can you see my cursor? Look at the top right… no, more left."** Pointing
at things over a call is painful. In ChatFlow, anyone in the call can draw
directly on the shared screen — arrows, boxes, freehand ink — and on Windows
the presenter's own desktop shows the same ink, live. Strokes fade away on
their own, or can be pinned until cleaned up.

**Tiny text in a shared screen.** Any participant can zoom into a shared
video or screen — scroll to zoom on desktop, pinch on Android — and pan around
without affecting what anyone else sees.

**Calls that fall apart with more people.** Media is routed through a Janus
SFU rather than peer-to-peer meshes, so group calls scale predictably: rooms
support up to 20 simultaneous video publishers with per-user volume control.

---

## Features

### Messaging
- Chat rooms with descriptions, membership and per-room voice/video toggles
- Direct messages with unread tracking and read receipts
- @mentions with notification modes per room (all / mentions only / muted)
- File and image attachments up to 50 MB, with inline image and video previews
- YouTube link previews
- System notifications for messages, mentions and incoming calls

### Voice & video calls
- Room calls and private 1-to-1 calls with ringing and accept/decline
- Modern codecs — H.264, HEVC and AV1 where the hardware supports them, with
  selectable resolution, frame rate and bitrate
- Push-to-talk with a global hotkey (works while the app is in the background)
- Voice gate (auto-mute below a speech threshold), noise suppression, echo
  cancellation
- Spotlight layout, per-participant volume, hideable streams
- Floating picture-in-picture overlay while you browse other rooms
- Fullscreen viewing with auto-hiding controls — on desktop and Android

### Screen sharing & annotation
- Share a whole monitor or a single window
- **System-audio capture on Windows**, including per-application isolation
- Anyone in the call can **draw on the shared screen** once the presenter
  allows it: pen, highlighter, arrows, boxes and ovals, in six colors
- On Windows, ink renders on the presenter's **real desktop**, so it is
  captured into the stream and even into recordings
- Annotations auto-fade after a few seconds, or can be pinned; the presenter
  can wipe the canvas with one click
- Per-viewer **zoom and pan** on any stream (wheel / pinch), up to 8×

### Administration & moderation
- Role system: Owner, Admin, Moderator, Member — the first registered account
  becomes the server Owner
- Kick from room, kick from call, move users between rooms, rename rooms
- Moderation actions are validated server-side against the actor's role

### Platform
- **Windows** desktop app (WebView2-based, hardware-accelerated)
- **Android** app with ahead-of-time compilation for fast cold starts
- Built-in updater: the app checks your server and offers new versions —
  no store, no forced updates

---

## How it works

```
┌───────────────┐         gRPC / MagicOnion          ┌──────────────────┐
│  ChatFlow app │ ◄────── messages, presence ──────► │  ChatFlow server │
│ (Win/Android) │                                    │  (.NET 10, ARM64│
│               │ ◄────── WebRTC media (SRTP) ─────► │   or x64 Linux)  │
└───────────────┘              via                   └────────┬─────────┘
                        ┌─────────────┐                       │
                        │  Janus SFU  │                MariaDB / MySQL
                        └─────────────┘               (your database)
```

- **Server**: a single .NET 10 binary. Chat, presence and signaling go over
  gRPC (MagicOnion + MessagePack); media is negotiated with a Janus SFU and
  flows encrypted (DTLS-SRTP) between clients and the SFU.
- **Storage**: MariaDB/MySQL via EF Core migrations — schema updates apply
  automatically on startup. Optional Redis cache for hot data.
- **Auth**: username/password with BCrypt hashing and JWT sessions. No email,
  no phone number, no third-party identity provider required.
- **Updates**: the server publishes the latest version and download URL; apps
  poll it and self-update.

## Requirements

| Component | Minimum |
|-----------|---------|
| Server    | Linux (x64 or ARM64), .NET 10 runtime, MariaDB 10.5+/MySQL 8, a Janus instance |
| Windows   | Windows 10 1809+ with WebView2 (preinstalled on Windows 11) |
| Android   | Android 7.0 (API 24) or newer |

## Download

Current release: **1.9.5**

- Windows: [ChatLast1.9.5.zip](https://www.duncan.com.ar/ChatLast1.9.5.zip)
- Android: [ChatLast1.9.5.apk](https://www.duncan.com.ar/ChatLast1.9.5.apk)

---

*ChatFlow is under active development. iOS and macOS builds compile from the
same codebase but are not yet shipped or supported.*
