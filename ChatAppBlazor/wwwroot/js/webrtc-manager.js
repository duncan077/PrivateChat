'use strict';

/**
 * WebRTC Manager — First-class Janus SFU integration for Blazor Hybrid.
 *
 * Manages the complete Janus videoroom lifecycle:
 *   - Publisher: local media → Janus → SFU
 *   - Subscribers: one handle per remote feed (two-phase via polling)
 *   - Trickle ICE, keepalive, long-poll event loop
 *
 * Communication with C# via DotNetObjectReference (passed in init).
 */
window.webrtcManager = {
    // ─── State ──────────────────────────────────────────────────────────────────

    localStream: null,
    screenStream: null,
    isMuted: true,
    isCameraOff: true,
    isScreenSharing: false,

    // Janus session (publisher)
    janusSessionId: 0,
    janusHandleId: 0,
    screenAudioPublisherHandleId: 0,
    janusRoomId: 0,
    myPrivateId: 0,

    // Per-feed subscriber handles: feedId → { handleId, pc }
    feeds: {},
    nativeScreenReceivers: {},
    nativeScreenPendingCandidates: {},
    remoteFeedVolumes: {},
    remoteIdentityVolumes: {},
    remoteIdentityVolumeStoragePrefix: 'privatechat:remoteIdentityVolumes:v1:',
    identityVolumePersistDebounceMs: 300,
    _identityVolumePersistTimerId: 0,
    _displayParseCache: null,
    displayParseCacheLimit: 256,
    _remoteAudioRoutingRafId: 0,
    _remoteAudioRoutingTimerId: 0,
    _screenAudioScratch: null,
    _screenAudioScheduled: null,
    screenAudioFallbackMaxLeadSeconds: 0.25,
    hiddenRemoteFeeds: {},
    hiddenRemoteFeedMeta: {},
    streamVolume: 1.0,
    publisherStreamHints: {},
    attachmentVideoVolumeStorageKey: 'privatechat:attachmentVideoVolume:v1',
    attachmentVideoVolumeCached: null,

    // Pending subscriber joins: handleId → { feedId, display }
    // Tracks handles waiting for JSEP offer from Janus via polling loop.
    pendingSubscribes: {},
    pendingSubscribeFeeds: {},
    pendingSubscribeTimeoutMs: 5000,

    // Publisher PeerConnection
    publisherPc: null,
    screenAudioPublisherPc: null,
    publisherConfigured: false,
    screenAudioPublisherConfigured: false,
    screenAudioPublisherPendingCandidates: [],
    pendingJanusBitrateBps: 0,

    // Auth / server
    userId: '',
    userDisplayName: '',
    participantDirectory: {},
    participantAvatarDirectory: {},
    serverBaseUrl: '',
    authToken: '',

    // ICE
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],

    // Polling / keepalive / participant discovery
    polling: false,
    pollAbort: null,
    lastPollActivityAt: 0,
    keepaliveTimer: null,
    participantCheckTimer: null,
    remoteFeedHealthTimer: null,
    remoteFeedHealthBusy: false,
    remoteFeedHealthCursor: 0,
    keepaliveFailures: 0,
    pollFailures: 0,
    pollGeneration: 0,
    pollBackoffMaxMs: 30_000,
    pollFailureLogEscalation: 5,
    maxAudioRecoveryAttempts: 3,
    lastParticipantCheckAtMs: 0,
    lastRemoteFeedHealthRunAtMs: 0,
    lastStreamInfoRefreshAtMs: 0,
    verbosePollEventLogging: false,

    // C# interop reference
    dotNetRef: null,

    // Device / settings
    currentConstraints: {
        micDeviceId: null,
        cameraDeviceId: null,
        resolution: '1280x720',
        fps: 30,
        bitrateKbps: 2000,
        preferredVideoCodec: 'h264',
        h264ProfileOverride: '',
        hideLocalPreview: false,
        screenShareAudioEnabled: true,
        screenShareAutoGainEnabled: true,
        noiseSuppressionEnabled: true,
        voiceGateEnabled: false,
        voiceGateAuto: true,
        voiceGateThreshold: 30
    },
    minPublisherBitrateKbps: 1000,
    maxPublisherBitrateKbps: 20000,
    /// Initial bandwidth estimate handed to libwebrtc via x-google-start-bitrate, in
    /// kbps. Deliberately modest and NOT derived from the cap - see
    /// applyVideoBitrateParametersToSdp for why starting near the cap is self-defeating.
    publisherStartBitrateKbps: 2500,
    /// Whether to ask the encoder for constant bitrate via x-google-cbr.
    ///
    /// Off, because screen content is bursty in exactly the way CBR is bad at. A share
    /// is mostly still, then changes a lot for a few frames. CBR asks the encoder to
    /// spend the same number of bits on every frame regardless: padding out the still
    /// ones, and - the part that costs frames - refusing to spend extra on the busy ones,
    /// so a burst is met by degrading the frame instead of borrowing bits from the idle
    /// seconds either side of it. VBR within the same floor and cap gives the same
    /// average with the headroom placed where the content actually needs it.
    ///
    /// Set back to true to restore the previous behaviour; the SDP log line reports
    /// cbr=0 or cbr=1 so the active setting is visible per negotiation.
    enforcePublisherCbr: false,
    // Applied to outgoing SDP when H264 is present/selected.
    h264ProfileLevelId: '64e029',
    h264ProfileLevelIdFallbacks: ['4de01f', '42e01f'],
    receiverAudioJitterBufferTargetMs: 120,
    receiverVideoJitterBufferTargetMs: 90,
    receiverAudioPlayoutDelayHintS: 0.12,
    receiverVideoPlayoutDelayHintS: 0.10,
    audioContext: null,
    voiceGateAnalyser: null,
    remotePlaybackAudioContext: null,
    remotePlaybackAudioNodes: new WeakMap(),
    remoteSpeakingAnalyzers: {},
    localSpeakingAnalyzer: null,
    speakingMonitorTimer: null,
    speakingMonitorIntervalMs: 250,
    speakingHoldMs: 700,
    speakingRmsThreshold: 0.035,
    activeSpeakingUsers: new Set(),
    gainNode: null,
    musicBotContext: null,
    musicBotDestination: null,
    musicBotElement: null,
    musicBotSource: null,
    musicBotGainNode: null,
    musicBotMixedTrack: null,
    musicBotActive: false,
    musicBotTitle: '',
    musicBotStreamUrl: '',
    musicBotVolume: 0.8,
    micTestStream: null,
    micTestAudioElement: null,
    micTestStreamIdentity: '',

    // Push-to-talk
    pttEnabled: false,
    pttKey: 'Space',
    pttSoundEnabled: true,
    pttBinding: null,
    pttKeyDown: false,
    pttPressedCodes: new Set(),
    _pttKeyDownHandler: null,
    _pttKeyUpHandler: null,
    _pttBlurHandler: null,
    _pttVisibilityHandler: null,
    _hostMessageHandler: null,
    _fullscreenChangeHandler: null,
    _desktopFullscreenRequested: false,

    // Windows WebView2 screen-share audio fallback (native loopback via NAudio)
    screenAudioFallbackActive: false,
    screenAudioFallbackContext: null,
    screenAudioFallbackDestination: null,
    screenAudioFallbackGain: null,
    screenAudioFallbackLimiter: null,
    screenAudioFallbackTrack: null,
    screenAudioFallbackNextTime: 0,
    screenAudioFallbackAutoGain: 1.0,
    screenAudioFallbackTargetRms: 0.11,
    screenAudioFallbackNoiseGateRms: 0.002,
    screenAudioFallbackMinGain: 1.0,
    screenAudioFallbackMaxGain: 4.5,
    screenShareAudioMixContext: null,
    screenShareAudioMixTrack: null,

    // Stream info overlays / stats sampling
    streamInfoIntervalId: null,
    openStreamInfoPanels: {},
    streamStatsCache: {},
    selectedSpotlightStreamKey: null,
    controlOverlayHideTimers: new WeakMap(),
    screenShareSystemAudioSuppressed: false,
    screenShareBitrateWarmupTimers: [],
    publisherVideoPerfGuardIntervalId: null,
    publisherVideoPerfGuardState: null,
    publisherVideoPerfGuardIntervalMs: 2000,
    publisherHealthIntervalId: null,
    publisherHealthState: null,

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /**
     * Find a sender for the given media kind ('audio' or 'video').
     * Handles both normal senders (with a track) and recvonly senders
     * (track is null, must match via transceiver).
     */
    findSenderByKind(pc, kind) {
        // First try: sender with an active track of the right kind
        const senders = pc.getSenders();
        let s = senders.find(x => x.track?.kind === kind);
        if (s) return s;
        // Fallback: find via transceiver (for recvonly where sender.track is null)
        const t = pc.getTransceivers().find(t =>
            t.receiver?.track?.kind === kind || t.mid === (kind === 'audio' ? '0' : '1')
        );
        return t?.sender || null;
    },

    applyVideoTrackContentHint(videoTrack, scope) {
        if (!videoTrack) return;
        try {
            if ('contentHint' in videoTrack) {
                // Prioritize smooth motion by preferring frame rate over spatial detail.
                videoTrack.contentHint = 'motion';
                this.log('media', `${scope}: set video contentHint=motion`);
            }
        } catch { }
    },

    /// Ceiling for a frame-rate limiter that is meant to hit `desiredFps`.
    ///
    /// A limiter clamps at "no more than this", and both the capture adapter and
    /// libwebrtc's frame dropper make that decision against a jittery arrival time.
    /// Pinned exactly at the target, each one rejects the occasional frame that turns up
    /// a fraction early - and there are two of them in series, on the track and on the
    /// encoding, which is how a source delivering a clean 60 came out at 55-57.
    ///
    /// A little headroom lets them clamp what they are actually for - a 144Hz display
    /// feeding a 60fps share - while leaving the target itself alone. The rate is still
    /// governed by `ideal` on the capture constraint, so this does not invite overshoot.
    getFrameRateCeiling(desiredFps) {
        const target = Math.max(1, parseInt(desiredFps, 10) || 30);
        return Math.max(target + 2, Math.ceil(target * 1.1));
    },

    async tuneScreenShareVideoTrackForPerformance(videoTrack, targetFps, scope = 'screenshare', options = null) {
        if (!videoTrack) return;

        const desiredFps = Math.max(1, parseInt(targetFps, 10) || 30);
        const ceilingFps = this.getFrameRateCeiling(desiredFps);
        const minFps = desiredFps >= 30 ? 30 : Math.max(5, Math.floor(desiredFps * 0.75));
        const targetResolution = this.parseResolutionSetting(this.currentConstraints?.resolution, 1920, 1080);
        const enforceResolutionCap = options?.enforceResolutionCap !== false;

        if (typeof videoTrack.applyConstraints === 'function') {
            try {
                const constraints = {
                    frameRate: { min: minFps, ideal: desiredFps, max: ceilingFps }
                };
                if (enforceResolutionCap) {
                    constraints.width = { ideal: targetResolution.width, max: targetResolution.width };
                    constraints.height = { ideal: targetResolution.height, max: targetResolution.height };
                }
                await videoTrack.applyConstraints(constraints);
                this.log(
                    'screenshare',
                    `${scope}: applied video constraints ${enforceResolutionCap ? `${targetResolution.width}x${targetResolution.height} ` : ''}frameRate=${minFps}/${desiredFps}/${ceilingFps}`
                );
            } catch (err) {
                this.log('screenshare', `${scope}: video constraint tuning failed (${err.message})`);
            }
        }

        try {
            const settings = videoTrack.getSettings?.() || {};
            const actualFps = Number(settings.frameRate ?? 0);
            const width = Number(settings.width ?? 0);
            const height = Number(settings.height ?? 0);
            this.log(
                'screenshare',
                `${scope}: captured track settings ${width || 'n/a'}x${height || 'n/a'} @ ${actualFps > 0 ? actualFps.toFixed(1) : 'n/a'} fps`
            );
        } catch { }
    },

    async tuneScreenShareVideoSenderForPerformance(videoSender, targetFps, scope = 'screenshare', options = null) {
        if (!videoSender || typeof videoSender.getParameters !== 'function' || typeof videoSender.setParameters !== 'function') {
            return;
        }

        const desiredFps = Math.max(1, parseInt(targetFps, 10) || 30);
        const maxBitrateKbps = this.clampPublisherBitrateKbps(this.currentConstraints?.bitrateKbps || 2000);
        const minBitrateKbps = Math.min(maxBitrateKbps, this.getEffectiveMinPublisherBitrateKbps());
        const maxBitrateBps = maxBitrateKbps * 1000;
        const minBitrateBps = minBitrateKbps * 1000;
        const requestedScale = Number(options?.scaleResolutionDownBy);
        const minRequiredScale = this.getRequiredScreenShareScaleDownBy(videoSender?.track || null);

        try {
            const params = videoSender.getParameters() || {};
            if (!Array.isArray(params.encodings) || params.encodings.length === 0) {
                params.encodings = [{}];
            }

            params.degradationPreference = 'maintain-framerate';
            // The second of the two limiters - see getFrameRateCeiling. libwebrtc drops
            // frames to stay at or below this, so exactly the target meant dropping on
            // jitter alone.
            params.encodings[0].maxFramerate = this.getFrameRateCeiling(desiredFps);
            params.encodings[0].minBitrate = minBitrateBps;
            params.encodings[0].maxBitrate = maxBitrateBps;
            params.encodings[0].priority = 'high';
            if ('networkPriority' in params.encodings[0]) {
                params.encodings[0].networkPriority = 'high';
            }
            const currentScale = Number(params.encodings[0].scaleResolutionDownBy);
            let nextScale = Number.isFinite(currentScale) && currentScale >= 1 ? currentScale : 1;
            if (Number.isFinite(requestedScale) && requestedScale >= 1) {
                nextScale = requestedScale;
            }
            nextScale = Math.max(minRequiredScale, nextScale);
            params.encodings[0].scaleResolutionDownBy = nextScale;

            await videoSender.setParameters(params);
            this.log(
                'screenshare',
                `${scope}: tuned sender (maintain-framerate, fps=${desiredFps}, bitrate=${minBitrateKbps}/${maxBitrateKbps} kbps, scale=${params.encodings[0].scaleResolutionDownBy || 1})`
            );
        } catch (err) {
            this.log('screenshare', `${scope}: sender performance tuning failed (${err.message})`);
        }
    },

    classifyScreenShareTarget(displaySurface, targetLabel) {
        const surface = (displaySurface || '').toString().trim().toLowerCase();
        const label = (targetLabel || '').toString().trim().toLowerCase();

        // Label hints can be more reliable than displaySurface in some WebView2 builds.
        if (
            label.startsWith('window ') ||
            label.startsWith('window:') ||
            label.startsWith('application ') ||
            label.startsWith('app ') ||
            label.startsWith('tab ') ||
            label.startsWith('tab:')
        ) {
            return 'window';
        }

        if (surface === 'monitor' || surface === 'screen') {
            return 'system';
        }
        if (surface === 'window' || surface === 'application' || surface === 'browser') {
            return 'window';
        }

        if (
            label.startsWith('screen ') ||
            label.startsWith('screen:') ||
            label.startsWith('monitor ') ||
            label.startsWith('monitor:') ||
            label.includes('entire screen')
        ) {
            return 'system';
        }
        if (
            label.startsWith('window ') ||
            label.startsWith('window:') ||
            label.startsWith('application ') ||
            label.startsWith('app ') ||
            label.startsWith('tab ') ||
            label.startsWith('tab:')
        ) {
            return 'window';
        }

        return 'unknown';
    },

    // ─── Screen-share annotation support ──────────────────────────────────────

    /// Tells .NET which desktop surface is being captured so the Windows overlay
    /// can position itself over exactly that monitor or window.
    reportScreenShareTarget(targetLabel, displaySurface) {
        try {
            const track = this.screenStream?.getVideoTracks?.()[0] || null;
            const settings = track?.getSettings?.() || {};
            this.notifyDotNet('OnScreenShareTargetChanged', JSON.stringify({
                targetLabel: (targetLabel || '').toString(),
                displaySurface: (displaySurface || settings.displaySurface || '').toString(),
                frameWidth: parseInt(settings.width, 10) || 0,
                frameHeight: parseInt(settings.height, 10) || 0
            }));
        } catch (err) {
            this.log('screenshare', `Unable to report annotation capture target: ${err.message}`);
        }
    },

    /// Locates the video cell showing a given user's shared screen, which is the
    /// surface annotations are drawn on.
    findAnnotationTargetCell(sharerUserId) {
        const normalized = this.normalizeUserId(sharerUserId);
        if (!normalized) return null;

        const selfId = this.normalizeUserId(this.userId || '');
        if (selfId && normalized === selfId) {
            return this.isScreenSharing ? document.querySelector('#videoGrid .local-cell') : null;
        }

        // IMPORTANT: this client does NOT publish a dedicated screen feed - the
        // share replaces the video track on the sharer's PRIMARY feed. A dedicated
        // 'native-screen' feed only exists for the native (non-Blazor) pipeline.
        // So: prefer a dedicated share feed when one exists, but fall back to the
        // sharer's ordinary cell, which is where the shared screen actually shows
        // for viewers of this app. (Requiring a share-role feed here was the bug
        // that made drawing impossible on every viewer machine.)
        const wantedIdentity = `uid:${normalized}`;
        let fallbackCell = null;
        let fallbackHasVideo = false;

        for (const feedId of Object.keys(this.feeds || {})) {
            const feed = this.feeds[feedId];
            if (!feed) continue;
            if (this.getFeedIdentityKey(feedId) !== wantedIdentity) continue;

            // Screen-audio companion feeds render as hidden cells - never a host.
            if (this.isFeedTaggedScreenAudio(feedId)) continue;

            const cell = document.getElementById('remote-' + feedId);
            if (!cell) continue;

            const role = this.getDisplayRole(feed.display);
            if (role === 'native-screen' || this.getFeedRole(feedId) === 'stream') {
                return cell;
            }

            const hasVideo = !!this.feedHasLiveTrack?.(feed, 'video');
            if (!fallbackCell || (hasVideo && !fallbackHasVideo)) {
                fallbackCell = cell;
                fallbackHasVideo = hasVideo;
            }
        }

        return fallbackCell;
    },

    buildScreenShareTargetLabel(...candidates) {
        const normalizedCandidates = candidates
            .map(x => (x || '').toString().trim())
            .filter(Boolean);

        for (const candidate of normalizedCandidates) {
            if (candidate.toLowerCase().startsWith('window:')) {
                return candidate;
            }
        }

        return normalizedCandidates[0] || '';
    },

    clampPublisherBitrateKbps(value) {
        const parsed = parseInt(value, 10);
        const minBitrateKbps = this.getEffectiveMinPublisherBitrateKbps();
        if (!Number.isFinite(parsed) || parsed <= 0) {
            return Math.max(minBitrateKbps, 2000);
        }
        return Math.max(
            minBitrateKbps,
            Math.min(this.maxPublisherBitrateKbps, parsed)
        );
    },

    getRemoteIdentityVolumeStorageKey() {
        const normalizedUserId = this.normalizeUserId(this.userId || '');
        return `${this.remoteIdentityVolumeStoragePrefix}${normalizedUserId || 'anonymous'}`;
    },

    sanitizeIdentityVolumeMap(raw) {
        if (!raw || typeof raw !== 'object') return {};
        const sanitized = {};
        for (const [identityKey, value] of Object.entries(raw)) {
            if (!identityKey || typeof value !== 'object' || !value) continue;
            const mic = Math.max(0, Math.min(200, parseInt(value.mic, 10) || 100));
            const stream = Math.max(0, Math.min(200, parseInt(value.stream, 10) || mic));
            sanitized[String(identityKey)] = { mic, stream };
        }
        return sanitized;
    },

    loadPersistedRemoteIdentityVolumes() {
        try {
            const storageKey = this.getRemoteIdentityVolumeStorageKey();
            const raw = window.localStorage?.getItem(storageKey);
            if (!raw) return;
            const parsed = JSON.parse(raw);
            const sanitized = this.sanitizeIdentityVolumeMap(parsed);
            this.remoteIdentityVolumes = sanitized;
        } catch {
            // Ignore storage parsing failures.
        }
    },

    savePersistedRemoteIdentityVolumes() {
        try {
            const storageKey = this.getRemoteIdentityVolumeStorageKey();
            const sanitized = this.sanitizeIdentityVolumeMap(this.remoteIdentityVolumes);
            window.localStorage?.setItem(storageKey, JSON.stringify(sanitized));
        } catch {
            // Ignore storage persistence failures.
        }
    },

    /**
     * Trailing-debounced persist. Volume sliders fire `input` at pointer rate, and each write
     * is a full map sanitise + JSON.stringify + synchronous localStorage.setItem.
     */
    schedulePersistedRemoteIdentityVolumes() {
        if (this._identityVolumePersistTimerId) return;
        this._identityVolumePersistTimerId = setTimeout(() => {
            this._identityVolumePersistTimerId = 0;
            this.savePersistedRemoteIdentityVolumes();
        }, this.identityVolumePersistDebounceMs);
    },

    flushPersistedRemoteIdentityVolumes() {
        if (!this._identityVolumePersistTimerId) return;
        clearTimeout(this._identityVolumePersistTimerId);
        this._identityVolumePersistTimerId = 0;
        this.savePersistedRemoteIdentityVolumes();
    },

    getAttachmentVideoSavedVolume() {
        if (typeof this.attachmentVideoVolumeCached === 'number' && Number.isFinite(this.attachmentVideoVolumeCached)) {
            return Math.max(0, Math.min(1, this.attachmentVideoVolumeCached));
        }
        try {
            const raw = window.localStorage?.getItem(this.attachmentVideoVolumeStorageKey);
            const parsed = Number.parseFloat(raw ?? '');
            if (Number.isFinite(parsed)) {
                this.attachmentVideoVolumeCached = Math.max(0, Math.min(1, parsed));
                return this.attachmentVideoVolumeCached;
            }
        } catch {
            // Ignore storage parsing failures.
        }
        this.attachmentVideoVolumeCached = 1.0;
        return 1.0;
    },

    applyAttachmentVideoVolume(videoElement) {
        if (!videoElement) return;
        const savedVolume = this.getAttachmentVideoSavedVolume();
        try {
            videoElement.volume = Math.max(0, Math.min(1, savedVolume));
            videoElement.muted = savedVolume <= 0.0001;
        } catch {
            // Ignore element update failures.
        }
    },

    rememberAttachmentVideoVolume(videoElement) {
        if (!videoElement) return;
        const candidate = videoElement.muted ? 0 : (Number(videoElement.volume) || 0);
        const clamped = Math.max(0, Math.min(1, candidate));
        this.attachmentVideoVolumeCached = clamped;
        try {
            window.localStorage?.setItem(this.attachmentVideoVolumeStorageKey, String(clamped));
        } catch {
            // Ignore storage persistence failures.
        }
    },

    isAndroidWebRtcEnvironment() {
        try {
            const ua = (navigator?.userAgent || '').toString().toLowerCase();
            return ua.includes('android');
        } catch {
            return false;
        }
    },

    enforceAndroidHardwareCodecPreference(reason = 'runtime') {
        if (!this.isAndroidWebRtcEnvironment()) return;

        const support = this.getBrowserVideoCodecSupport();
        if (!support.has('h264')) {
            this.log('codec', `${reason}: Android hardware codec preference skipped (H264 not supported by browser)`);
            return;
        }

        const current = this.normalizeVideoCodec(this.currentConstraints.preferredVideoCodec);
        if (current === 'h264') {
            this.log('codec', `${reason}: Android using H264 (best chance for HW encoder)`);
            return;
        }

        // Do not hard-force codec on Android at runtime; some devices fail renegotiation.
        this.log('codec', `${reason}: keeping preferred codec '${current}' (H264 available but not forced)`);
    },

    getEffectiveMinPublisherBitrateKbps() {
        const configured = Math.max(50, parseInt(this.minPublisherBitrateKbps, 10) || 1000);
        // Android uplinks are often unstable with a strict 1+ Mbps floor.
        if (this.isAndroidWebRtcEnvironment()) {
            return Math.min(configured, 300);
        }
        return configured;
    },

    shouldEnforcePublisherCbr() {
        // CBR + high floor can stall mobile encoders under load.
        if (this.isAndroidWebRtcEnvironment()) {
            return false;
        }
        return this.enforcePublisherCbr;
    },

    parseResolutionSetting(value, fallbackWidth = 1280, fallbackHeight = 720) {
        let width = parseInt(fallbackWidth, 10);
        let height = parseInt(fallbackHeight, 10);
        const raw = (value || '').toString().trim().toLowerCase();
        const match = /^(\d{3,5})x(\d{3,5})$/.exec(raw);
        if (match) {
            width = parseInt(match[1], 10);
            height = parseInt(match[2], 10);
        }

        if (!Number.isFinite(width) || width <= 0) width = parseInt(fallbackWidth, 10) || 1280;
        if (!Number.isFinite(height) || height <= 0) height = parseInt(fallbackHeight, 10) || 720;

        width = Math.max(640, Math.min(3840, width));
        height = Math.max(360, Math.min(2160, height));
        return { width, height };
    },

    buildCameraVideoConstraint() {
        const target = this.parseResolutionSetting(this.currentConstraints?.resolution, 1280, 720);
        const fps = Math.max(1, parseInt(this.currentConstraints?.fps, 10) || 30);
        const constraint = {
            width: { ideal: target.width },
            height: { ideal: target.height },
            // Same reasoning as the screen-share path: a camera asked for exactly its own
            // rate as a hard maximum lands just under it.
            frameRate: { ideal: fps, max: this.getFrameRateCeiling(fps) }
        };
        const cameraDeviceId = (this.currentConstraints?.cameraDeviceId || '').toString().trim();
        if (cameraDeviceId) {
            constraint.deviceId = { exact: cameraDeviceId };
        }
        return constraint;
    },

    stopLocalCameraTracks(reason = 'camera-off') {
        if (!this.localStream) return false;
        const videoTracks = this.localStream.getVideoTracks();
        if (!videoTracks.length) return false;

        for (const track of videoTracks) {
            try { track.enabled = false; } catch { }
            try { this.localStream.removeTrack(track); } catch { }
            try { track.stop(); } catch { }
        }

        if (!this.isScreenSharing) {
            const localVideo = document.getElementById('localVideo');
            if (localVideo) {
                localVideo.srcObject = this.localStream;
            }
            this.updateLocalFeedAvatarFallback();
        }

        this.log('media', `${reason}: disconnected local camera (${videoTracks.length} track(s) stopped)`);
        return true;
    },

    async ensureLocalCameraTrack(reason = 'camera-on') {
        if (!navigator?.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== 'function') {
            throw new Error('getUserMedia unavailable');
        }

        if (!this.localStream) {
            this.localStream = new MediaStream();
        }

        const existing = this.localStream.getVideoTracks().find(t => t && t.readyState === 'live');
        if (existing) {
            existing.enabled = true;
            return existing;
        }

        const primaryConstraint = this.buildCameraVideoConstraint();
        let captureStream = null;
        let lastError = null;

        const attempts = [
            { label: 'preferred', constraints: { audio: false, video: primaryConstraint } },
            { label: 'default', constraints: { audio: false, video: true } }
        ];

        for (const attempt of attempts) {
            try {
                captureStream = await navigator.mediaDevices.getUserMedia(attempt.constraints);
                this.log('media', `${reason}: reacquired camera track (${attempt.label})`);
                break;
            } catch (err) {
                lastError = err;
                this.log('media', `${reason}: camera reacquire failed (${attempt.label}): ${err.message}`);
            }
        }

        const videoTrack = captureStream?.getVideoTracks?.()[0] || null;
        if (!videoTrack) {
            throw new Error(lastError?.message || 'No camera video track available');
        }

        this.applyVideoTrackContentHint(videoTrack, 'camera');
        this.localStream.addTrack(videoTrack);

        const localVideo = document.getElementById('localVideo');
        if (localVideo && !this.isScreenSharing) {
            localVideo.srcObject = this.localStream;
            const playPromise = localVideo.play?.();
            if (playPromise?.catch) {
                playPromise.catch(err => this.log('media', `Local preview play() blocked: ${err.message}`));
            }
        }
        this.updateLocalFeedAvatarFallback();

        return videoTrack;
    },

    getRequiredScreenShareScaleDownBy(videoTrack) {
        if (!videoTrack) return 1;
        const targetResolution = this.parseResolutionSetting(this.currentConstraints?.resolution, 1920, 1080);
        const settings = videoTrack.getSettings?.() || {};
        const sourceWidth = Number(settings.width ?? 0);
        const sourceHeight = Number(settings.height ?? 0);
        if (!Number.isFinite(sourceWidth) || !Number.isFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0) {
            return 1;
        }

        const widthRatio = sourceWidth / Math.max(1, targetResolution.width);
        const heightRatio = sourceHeight / Math.max(1, targetResolution.height);
        const requiredScale = Math.max(1, widthRatio, heightRatio);
        return Math.max(1, Math.min(4, Math.round(requiredScale * 100) / 100));
    },

    clearScreenShareBitrateWarmupTimers() {
        if (!Array.isArray(this.screenShareBitrateWarmupTimers)) {
            this.screenShareBitrateWarmupTimers = [];
            return;
        }
        for (const timerId of this.screenShareBitrateWarmupTimers) {
            try { clearTimeout(timerId); } catch { }
        }
        this.screenShareBitrateWarmupTimers = [];
    },

    async requestPublisherKeyframe(reason = 'manual') {
        if (!this.janusSessionId || !this.janusHandleId || !this.publisherConfigured) {
            return;
        }
        const videocodec = this.getActivePublisherJanusVideoCodec();
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message-nowait`,
                { body: { request: 'configure', keyframe: true, videocodec } }
            );
            this.log('bitrate', `Requested keyframe (${reason}, videocodec=${videocodec})`);
        } catch (err) {
            this.log('bitrate', `Keyframe request failed (${reason}): ${err.message}`);
        }
    },

    scheduleScreenShareBitrateWarmup() {
        this.clearScreenShareBitrateWarmupTimers();
        if (!this.isScreenSharing) return;

        // Re-assert the bitrate as Janus settles, but ask for a keyframe only once.
        //
        // A 1080p keyframe is hundreds of kilobytes. Three of them inside two seconds -
        // on top of the one screenshare-start already requested - hand the pacer a queue
        // it then spends the next second draining, and that is exactly the window in
        // which the share is trying to reach its target frame rate. The extra requests
        // bought nothing: one keyframe is all a newly-attached subscriber needs.
        const steps = [
            { delayMs: 250, keyframe: false },
            { delayMs: 900, keyframe: true },
            { delayMs: 2000, keyframe: false }
        ];

        for (const step of steps) {
            const timerId = setTimeout(() => {
                if (!this.isScreenSharing || !this.publisherPc) return;
                this.applyBitrate(this.currentConstraints?.bitrateKbps || 2000).catch(() => { });
                if (step.keyframe) {
                    this.requestPublisherKeyframe(`screenshare-warmup-${step.delayMs}ms`).catch(() => { });
                }
            }, step.delayMs);
            this.screenShareBitrateWarmupTimers.push(timerId);
        }
    },

    resetPublisherVideoPerformanceGuardState() {
        this.publisherVideoPerfGuardState = {
            scaleResolutionDownBy: 1,
            lowFpsStreak: 0,
            recoveryStreak: 0,
            lastReason: 'none',
            lastFps: 0
        };
    },

    clearPublisherVideoPerformanceGuardSamples() {
        const prefix = 'perfguard:publisher:';
        for (const key of Object.keys(this.streamStatsCache)) {
            if (key.startsWith(prefix)) {
                delete this.streamStatsCache[key];
            }
        }
    },

    startPublisherVideoPerformanceGuard() {
        if (this.publisherVideoPerfGuardIntervalId) return;
        this.resetPublisherVideoPerformanceGuardState();
        this.publisherVideoPerfGuardIntervalId = setInterval(() => {
            this.runPublisherVideoPerformanceGuard().catch(() => { });
        }, this.publisherVideoPerfGuardIntervalMs);
        this.log('screenshare', `Started publisher performance guard (${this.publisherVideoPerfGuardIntervalMs}ms)`);
    },

    stopPublisherVideoPerformanceGuard() {
        if (this.publisherVideoPerfGuardIntervalId) {
            clearInterval(this.publisherVideoPerfGuardIntervalId);
            this.publisherVideoPerfGuardIntervalId = null;
            this.log('screenshare', 'Stopped publisher performance guard');
        }
        this.resetPublisherVideoPerformanceGuardState();
        this.clearPublisherVideoPerformanceGuardSamples();
    },

    async runPublisherVideoPerformanceGuard() {
        if (!this.isAppVisible()) {
            return;
        }

        if (!this.isScreenSharing || !this.publisherPc || !this.publisherConfigured) {
            return;
        }

        const videoSender = this.findSenderByKind(this.publisherPc, 'video');
        if (!videoSender || !videoSender.track || videoSender.track.readyState !== 'live') {
            return;
        }

        const stats = await this.publisherPc.getStats(videoSender);
        const videoReport = this.extractMediaRtpReport(stats, 'outbound', 'video');
        if (!videoReport) return;

        const timestampMs = Number(videoReport.timestamp ?? 0);
        const reportFps = Number(videoReport.framesPerSecond ?? 0);
        const frameCounter = Number(videoReport.framesSent ?? videoReport.framesEncoded ?? videoReport.framesCaptured ?? 0);
        const measuredFps = (Number.isFinite(reportFps) && reportFps > 0)
            ? reportFps
            : this.computeRateSample(`perfguard:publisher:${videoReport.id}`, frameCounter, timestampMs);
        const qualityLimitationReason = (videoReport.qualityLimitationReason || 'none').toString().toLowerCase();

        if (!this.publisherVideoPerfGuardState) {
            this.resetPublisherVideoPerformanceGuardState();
        }
        const state = this.publisherVideoPerfGuardState;

        const targetFps = Math.max(10, parseInt(this.currentConstraints?.fps, 10) || 30);
        const lowFpsThreshold = Math.max(8, targetFps * 0.7);
        const recoveryFpsThreshold = Math.max(12, targetFps * 0.9);
        const isEncoderLimited = qualityLimitationReason === 'cpu' || qualityLimitationReason === 'other';
        const hasLowFps = Number.isFinite(measuredFps) && measuredFps > 0 && measuredFps < lowFpsThreshold;
        const minRequiredScale = this.getRequiredScreenShareScaleDownBy(videoSender.track);

        const senderParams = videoSender.getParameters?.() || {};
        const senderScale = Number(senderParams?.encodings?.[0]?.scaleResolutionDownBy);
        if (Number.isFinite(senderScale) && senderScale >= 1) {
            state.scaleResolutionDownBy = Math.max(minRequiredScale, senderScale);
        }
        state.scaleResolutionDownBy = Math.max(minRequiredScale, Number(state.scaleResolutionDownBy) || 1);

        if (isEncoderLimited && hasLowFps) {
            state.lowFpsStreak += 1;
            state.recoveryStreak = 0;
        } else if (qualityLimitationReason === 'none' && Number.isFinite(measuredFps) && measuredFps >= recoveryFpsThreshold) {
            state.recoveryStreak += 1;
            state.lowFpsStreak = 0;
        } else {
            state.lowFpsStreak = 0;
            // Decay rather than reset. Recovery still wants a run of clean samples, but a
            // single ambiguous one - a momentary bandwidth limit, fps a hair under target -
            // used to throw the whole run away. Since 'bandwidth' counts as ambiguous and is
            // common on a real link, the scale ratcheted down on every dip and in practice
            // never climbed back: each quarter-step needed ten unbroken seconds.
            state.recoveryStreak = Math.max(0, state.recoveryStreak - 1);
        }

        state.lastReason = qualityLimitationReason;
        state.lastFps = measuredFps;

        if (state.lowFpsStreak >= 3) {
            state.lowFpsStreak = 0;
            const nextScale = Math.max(minRequiredScale, Math.min(2.5, Math.round((state.scaleResolutionDownBy + 0.25) * 100) / 100));
            if (nextScale > state.scaleResolutionDownBy + 0.001) {
                state.scaleResolutionDownBy = nextScale;
                await this.tuneScreenShareVideoSenderForPerformance(
                    videoSender,
                    targetFps,
                    'screenshare-perf-guard',
                    { scaleResolutionDownBy: nextScale }
                );
                this.log(
                    'screenshare',
                    `perf-guard: encoder-limited (${qualityLimitationReason}), fps=${measuredFps.toFixed(1)}/${targetFps}; increasing scaleResolutionDownBy to ${nextScale}`
                );
            }
            return;
        }

        // Symmetric with the degrade threshold above. At a 2s cadence this was 10s of
        // unbroken clean samples to win back a quarter-step that 6s of bad ones had cost,
        // so quality drifted downwards over the life of a share.
        if (state.recoveryStreak >= 3 && state.scaleResolutionDownBy > (minRequiredScale + 0.01)) {
            state.recoveryStreak = 0;
            const nextScale = Math.max(minRequiredScale, Math.round((state.scaleResolutionDownBy - 0.25) * 100) / 100);
            if (nextScale < state.scaleResolutionDownBy - 0.001) {
                state.scaleResolutionDownBy = nextScale;
                await this.tuneScreenShareVideoSenderForPerformance(
                    videoSender,
                    targetFps,
                    'screenshare-perf-guard',
                    { scaleResolutionDownBy: nextScale }
                );
                this.log(
                    'screenshare',
                    `perf-guard: recovered (${qualityLimitationReason}), fps=${measuredFps.toFixed(1)}/${targetFps}; reducing scaleResolutionDownBy to ${nextScale}`
                );
            }
        }
    },

    resetPublisherHealthState() {
        this.publisherHealthState = {
            stalledIntervals: 0,
            lastCodecFallbackAtMs: 0
        };
    },

    clearPublisherHealthSamples() {
        const prefix = 'health:publisher:';
        for (const key of Object.keys(this.streamStatsCache)) {
            if (key.startsWith(prefix)) {
                delete this.streamStatsCache[key];
            }
        }
    },

    startPublisherHealthWatchdog() {
        if (this.publisherHealthIntervalId) return;
        this.resetPublisherHealthState();
        if (!this.isAndroidWebRtcEnvironment()) return;

        this.publisherHealthIntervalId = setInterval(() => {
            this.runPublisherHealthWatchdog().catch(() => { });
        }, 3000);
        this.log('publish', 'Started Android publisher health watchdog');
    },

    stopPublisherHealthWatchdog() {
        if (this.publisherHealthIntervalId) {
            clearInterval(this.publisherHealthIntervalId);
            this.publisherHealthIntervalId = null;
            this.log('publish', 'Stopped Android publisher health watchdog');
        }
        this.resetPublisherHealthState();
        this.clearPublisherHealthSamples();
    },

    async runPublisherHealthWatchdog() {
        if (!this.isAppVisible()) return;
        if (!this.isAndroidWebRtcEnvironment()) return;
        if (!this.publisherPc || !this.publisherConfigured) return;

        const localVideoTrack = this.localStream?.getVideoTracks?.()[0] || null;
        if (!localVideoTrack || localVideoTrack.readyState !== 'live' || !localVideoTrack.enabled || this.isCameraOff) {
            if (this.publisherHealthState) {
                this.publisherHealthState.stalledIntervals = 0;
            }
            return;
        }

        const iceState = (this.publisherPc.iceConnectionState || '').toLowerCase();
        if (iceState !== 'connected' && iceState !== 'completed') {
            return;
        }

        const videoSender = this.findSenderByKind(this.publisherPc, 'video');
        if (!videoSender || !videoSender.track || videoSender.track.readyState !== 'live') {
            return;
        }

        const stats = await this.publisherPc.getStats(videoSender);
        const videoReport = this.extractMediaRtpReport(stats, 'outbound', 'video');
        if (!videoReport) return;

        const timestampMs = Number(videoReport.timestamp ?? 0);
        const framesCount = Number(videoReport.framesSent ?? videoReport.framesEncoded ?? videoReport.framesCaptured ?? 0);
        const bytesSent = Number(videoReport.bytesSent ?? 0);
        const reportFps = Number(videoReport.framesPerSecond ?? 0);
        const fps = (Number.isFinite(reportFps) && reportFps > 0)
            ? reportFps
            : this.computeRateSample(`health:publisher:fps:${videoReport.id}`, framesCount, timestampMs);
        const bitrateBps = this.computeBitrateSample(`health:publisher:bitrate:${videoReport.id}`, bytesSent, timestampMs);
        const limitation = (videoReport.qualityLimitationReason || 'none').toString().toLowerCase();

        if (!this.publisherHealthState) {
            this.resetPublisherHealthState();
        }
        const state = this.publisherHealthState;

        const stalled = (fps <= 0.5 || bitrateBps < 20_000) &&
            (limitation === 'none' || limitation === 'cpu' || limitation === 'other');
        if (stalled) {
            state.stalledIntervals += 1;
        } else {
            state.stalledIntervals = 0;
            return;
        }

        if (state.stalledIntervals < 3) {
            return;
        }

        state.stalledIntervals = 0;
        const nowMs = Date.now();
        if (nowMs - (state.lastCodecFallbackAtMs || 0) < 20000) {
            return;
        }
        state.lastCodecFallbackAtMs = nowMs;

        const currentCodec = this.normalizeVideoCodec(this.currentConstraints.preferredVideoCodec);
        const fallbackCodec = currentCodec === 'h264' ? 'vp8' : 'h264';
        this.currentConstraints.preferredVideoCodec = fallbackCodec;

        this.log(
            'codec',
            `android-watchdog: outbound video stalled (fps=${fps.toFixed(1)}, bitrate=${Math.round(bitrateBps / 1000)} kbps, limiter=${limitation}), switching ${currentCodec} -> ${fallbackCodec}`
        );

        this.resetPublisherCodecPreference('android-watchdog-reset');
        await this.renegotiatePublisher('android-watchdog-codec');
        await this.applyBitrate(this.currentConstraints.bitrateKbps);
        await this.requestPublisherKeyframe('android-watchdog-codec');
    },

    escapeRegex(text) {
        return (text || '').toString().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    },

    upsertFmtpParameter(params, key, value) {
        const keyPattern = this.escapeRegex(key);
        const re = new RegExp(`(?:^|;)\\s*${keyPattern}\\s*=\\s*[^;\\s]*\\s*`, 'ig');
        let next = (params || '').replace(re, '');
        next = next.replace(/\s*;\s*/g, ';').replace(/^;+|;+$/g, '').trim();
        return next ? `${next};${key}=${value}` : `${key}=${value}`;
    },

    applyVideoBitrateParametersToSdp(sdp, minBitrateKbps, maxBitrateKbps) {
        if (!sdp) return sdp;

        const minKbps = this.clampPublisherBitrateKbps(minBitrateKbps);
        const maxKbps = Math.max(minKbps, this.clampPublisherBitrateKbps(maxBitrateKbps));
        // x-google-start-bitrate is libwebrtc's INITIAL bandwidth estimate, used before it
        // has measured anything at all. At 90% of the cap, opening a 12 Mbps share meant
        // pushing 10.8 Mbps into a link of unknown capacity from the first frame: the
        // queue builds, packets drop, and the congestion controller answers with a hard
        // multiplicative decrease. That is the shape of "never reaches a steady 30 or 60"
        // and of a share that sags and then takes a long time to climb back.
        //
        // Transport-cc probes upward quickly when the path really is fat, so a modest
        // start costs about a second of ramp and avoids the overshoot completely.
        const startKbps = Math.max(minKbps, Math.min(maxKbps, this.publisherStartBitrateKbps));
        const lines = sdp.split(/\r?\n/);
        const videoPayloads = new Set();
        let inVideoSection = false;

        for (const line of lines) {
            if (!line) continue;
            if (line.startsWith('m=')) {
                inVideoSection = line.startsWith('m=video ');
                continue;
            }
            if (!inVideoSection) continue;

            const rtpMapMatch = /^a=rtpmap:(\d+)\s+([^\/\s]+)/i.exec(line);
            if (!rtpMapMatch) continue;
            const payload = rtpMapMatch[1];
            const codec = (rtpMapMatch[2] || '').toUpperCase();
            if (!payload || !codec) continue;
            if (codec === 'RTX' || codec === 'RED' || codec === 'ULPFEC' || codec === 'FLEXFEC-03') continue;
            videoPayloads.add(payload);
        }

        if (videoPayloads.size === 0) {
            return sdp;
        }

        inVideoSection = false;
        let modifiedCount = 0;
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (!line) continue;
            if (line.startsWith('m=')) {
                inVideoSection = line.startsWith('m=video ');
                continue;
            }
            if (!inVideoSection) continue;

            const fmtpMatch = /^a=fmtp:(\d+)\s+(.+)$/i.exec(line);
            if (!fmtpMatch) continue;

            const payload = fmtpMatch[1];
            if (!videoPayloads.has(payload)) continue;

            let params = (fmtpMatch[2] || '').trim();
            params = this.upsertFmtpParameter(params, 'x-google-min-bitrate', String(minKbps));
            params = this.upsertFmtpParameter(params, 'x-google-start-bitrate', String(startKbps));
            params = this.upsertFmtpParameter(params, 'x-google-max-bitrate', String(maxKbps));
            if (this.shouldEnforcePublisherCbr()) {
                params = this.upsertFmtpParameter(params, 'x-google-cbr', '1');
            }

            lines[i] = `a=fmtp:${payload} ${params}`;
            modifiedCount++;
        }

        if (modifiedCount > 0) {
            this.log('bitrate', `Applied SDP bitrate params to ${modifiedCount} video fmtp line(s) (min/start/max=${minKbps}/${startKbps}/${maxKbps} kbps, cbr=${this.shouldEnforcePublisherCbr() ? 1 : 0})`);
        }

        return lines.join('\r\n');
    },

    buildPublisherOfferWithBitrateParameters(offer) {
        if (!offer || !offer.sdp) return offer;
        const clampedMax = this.clampPublisherBitrateKbps(this.currentConstraints?.bitrateKbps || 2000);
        const patchedSdp = this.applyVideoBitrateParametersToSdp(
            offer.sdp,
            this.getEffectiveMinPublisherBitrateKbps(),
            clampedMax
        );

        if (patchedSdp === offer.sdp) return offer;
        // WebRTC offer objects can expose `type` via accessors that are not enumerable.
        // Preserve it explicitly so Janus always receives a valid JSEP type.
        const offerType = (offer.type || 'offer').toString();
        return {
            ...offer,
            type: offerType,
            sdp: patchedSdp
        };
    },

    ensureRemotePlaybackAudioContext() {
        try {
            if (!this.remotePlaybackAudioContext) {
                this.remotePlaybackAudioContext = new AudioContext();
            }
            if (this.remotePlaybackAudioContext.state === 'suspended') {
                this.remotePlaybackAudioContext.resume().catch(() => { });
            }
            return this.remotePlaybackAudioContext;
        } catch {
            return null;
        }
    },

    getMediaStreamIdentity(stream) {
        if (!stream) return '';
        const id = (stream.id || '').toString();
        if (id) return id;
        const audioIds = (stream.getAudioTracks?.() || []).map(t => t?.id || '').filter(Boolean).join(',');
        const videoIds = (stream.getVideoTracks?.() || []).map(t => t?.id || '').filter(Boolean).join(',');
        return `${audioIds}|${videoIds}`;
    },

    isRemoteFeedUsingStreamGain(videoElement) {
        const nodes = this.remotePlaybackAudioNodes.get(videoElement);
        return !!nodes && nodes.sourceKind === 'stream';
    },

    setRemoteFeedElementGain(videoElement, gainLinear) {
        if (!videoElement) return false;
        const context = this.ensureRemotePlaybackAudioContext();
        if (!context) return false;
        if (context.state !== 'running') {
            return false;
        }

        try {
            const stream = videoElement.srcObject;
            const hasStreamAudio = !!stream && typeof stream.getAudioTracks === 'function' && stream.getAudioTracks().length > 0;
            if (!hasStreamAudio) {
                this.detachRemoteFeedElementGain(videoElement);
                return false;
            }

            const streamIdentity = hasStreamAudio ? this.getMediaStreamIdentity(stream) : '';
            let nodes = this.remotePlaybackAudioNodes.get(videoElement);
            if (nodes && nodes.sourceKind !== 'stream') {
                try { nodes.sourceNode.disconnect(); } catch { }
                try { nodes.gainNode.disconnect(); } catch { }
                this.remotePlaybackAudioNodes.delete(videoElement);
                nodes = null;
            }
            const needsRecreateForStreamChange =
                !!nodes &&
                nodes.sourceKind === 'stream' &&
                streamIdentity &&
                nodes.streamIdentity !== streamIdentity;

            if (needsRecreateForStreamChange) {
                try { nodes.sourceNode.disconnect(); } catch { }
                try { nodes.gainNode.disconnect(); } catch { }
                this.remotePlaybackAudioNodes.delete(videoElement);
                nodes = null;
            }

            if (!nodes) {
                let sourceNode = null;
                try {
                    sourceNode = context.createMediaStreamSource(stream);
                } catch {
                    return false;
                }

                const gainNode = context.createGain();
                sourceNode.connect(gainNode);
                gainNode.connect(context.destination);
                nodes = { sourceNode, gainNode, sourceKind: 'stream', streamIdentity };
                this.remotePlaybackAudioNodes.set(videoElement, nodes);
            }

            nodes.gainNode.gain.value = Math.max(0, Math.min(2, gainLinear));
            return true;
        } catch {
            return false;
        }
    },

    detachRemoteFeedElementGain(videoElement) {
        if (!videoElement) return;
        const nodes = this.remotePlaybackAudioNodes.get(videoElement);
        if (!nodes) return;
        try { nodes.sourceNode.disconnect(); } catch { }
        try { nodes.gainNode.disconnect(); } catch { }
        this.remotePlaybackAudioNodes.delete(videoElement);
    },

    resolveFeedUserId(feedId) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return '';

        const feedDisplay = this.feeds[normalizedFeedId]?.display || this.hiddenRemoteFeedMeta[normalizedFeedId]?.display || '';
        const parsed = this.parsePublisherDisplay((feedDisplay || '').toString());
        return this.normalizeUserId(parsed.userId);
    },

    clearDrawerSpeakingState() {
        const rows = document.querySelectorAll('[data-room-drag-user]');
        for (const row of rows) {
            if (!row) continue;
            row.classList.remove('room-user-speaking');
            const avatar = row.querySelector('.chat-type-avatar');
            if (avatar) {
                avatar.classList.remove('chat-type-avatar-speaking');
            }
        }
    },

    /**
     * One document scan producing userId -> [{row, avatar}]. The speaking monitor used to run
     * this unindexed attribute-selector walk once per speaker per tick, over a document that
     * includes the whole chat scrollback, plus a nested query per matched row.
     */
    buildDrawerUserRowIndex() {
        const index = new Map();
        const rows = document.querySelectorAll('[data-room-drag-user]');
        for (const row of rows) {
            if (!row) continue;
            const rowUserId = this.normalizeUserId(row.getAttribute('data-room-drag-user') || '');
            if (!rowUserId) continue;

            const entry = { row, avatar: row.querySelector('.chat-type-avatar') };
            const existing = index.get(rowUserId);
            if (existing) {
                existing.push(entry);
            } else {
                index.set(rowUserId, [entry]);
            }
        }
        return index;
    },

    setDrawerUserSpeakingState(userId, speaking, rowIndex = null) {
        const normalizedUserId = this.normalizeUserId(userId);
        if (!normalizedUserId) return;

        const index = rowIndex || this.buildDrawerUserRowIndex();
        const entries = index.get(normalizedUserId);
        if (!entries) return;

        for (const entry of entries) {
            entry.row.classList.toggle('room-user-speaking', !!speaking);
            if (entry.avatar) {
                entry.avatar.classList.toggle('chat-type-avatar-speaking', !!speaking);
            }
        }
    },

    stopSpeakingMonitor() {
        if (this.speakingMonitorTimer) {
            clearInterval(this.speakingMonitorTimer);
            this.speakingMonitorTimer = null;
        }
    },

    sampleSpeakingLevel(entry) {
        if (!entry?.analyzerNode || !entry?.dataArray) return 0;
        try {
            entry.analyzerNode.getByteTimeDomainData(entry.dataArray);
        } catch {
            return 0;
        }

        let sumSquares = 0;
        for (let i = 0; i < entry.dataArray.length; i += 2) {
            const sample = (entry.dataArray[i] - 128) / 128;
            sumSquares += sample * sample;
        }
        const sampleCount = Math.max(1, Math.ceil(entry.dataArray.length / 2));
        return Math.sqrt(sumSquares / sampleCount);
    },

    updateSpeakingIndicatorsFromAnalyzers() {
        const analyzerEntries = Object.values(this.remoteSpeakingAnalyzers || {});
        const localEntry = this.localSpeakingAnalyzer;
        if (analyzerEntries.length === 0 && !localEntry) {
            if (this.activeSpeakingUsers.size > 0) {
                this.clearDrawerSpeakingState();
                this.activeSpeakingUsers.clear();
            }
            this.stopSpeakingMonitor();
            return;
        }

        const now = Date.now();
        const nextActive = new Set();

        for (const entry of analyzerEntries) {
            if (!entry.userId) continue;

            const rms = this.sampleSpeakingLevel(entry);

            if (rms >= this.speakingRmsThreshold) {
                entry.lastVoiceAt = now;
            }

            if ((now - (entry.lastVoiceAt || 0)) <= this.speakingHoldMs) {
                nextActive.add(entry.userId);
            }
        }

        if (localEntry?.userId) {
            const localRms = this.sampleSpeakingLevel(localEntry);
            if (localRms >= this.speakingRmsThreshold) {
                localEntry.lastVoiceAt = now;
            }
            if ((now - (localEntry.lastVoiceAt || 0)) <= this.speakingHoldMs) {
                nextActive.add(localEntry.userId);
            }
        }

        // Build the row index once per tick, and only when there is state to apply — a silent
        // room now costs zero DOM queries instead of one walk per tracked user.
        const rowIndex = (nextActive.size > 0 || this.activeSpeakingUsers.size > 0)
            ? this.buildDrawerUserRowIndex()
            : null;

        for (const userId of this.activeSpeakingUsers) {
            if (!nextActive.has(userId)) {
                this.setDrawerUserSpeakingState(userId, false, rowIndex);
            }
        }

        // Reapply speaking classes on every tick so drawer rerenders don't lose glow state.
        for (const userId of nextActive) {
            this.setDrawerUserSpeakingState(userId, true, rowIndex);
        }

        this.activeSpeakingUsers = nextActive;
    },

    ensureSpeakingMonitor() {
        if (this.speakingMonitorTimer) return;
        this.speakingMonitorTimer = setInterval(() => {
            this.updateSpeakingIndicatorsFromAnalyzers();
        }, Math.max(120, parseInt(this.speakingMonitorIntervalMs, 10) || 250));
    },

    removeLocalSpeakingAnalyzer() {
        const existing = this.localSpeakingAnalyzer;
        if (!existing) return;
        try { existing.sourceNode?.disconnect?.(); } catch { }
        try { existing.analyzerNode?.disconnect?.(); } catch { }
        try { existing.monitorGainNode?.disconnect?.(); } catch { }
        this.localSpeakingAnalyzer = null;
    },

    ensureLocalSpeakingAnalyzer() {
        const normalizedSelfUserId = this.normalizeUserId(this.userId || '');
        if (!normalizedSelfUserId) {
            this.removeLocalSpeakingAnalyzer();
            return;
        }

        const localAudioTrack = this.localStream?.getAudioTracks?.().find(t => t && t.readyState === 'live') || null;
        if (!localAudioTrack) {
            this.removeLocalSpeakingAnalyzer();
            return;
        }

        const context = this.ensureRemotePlaybackAudioContext();
        if (!context) {
            return;
        }

        const localAudioStream = new MediaStream([localAudioTrack]);
        const streamIdentity = this.getMediaStreamIdentity(localAudioStream);
        if (this.localSpeakingAnalyzer && this.localSpeakingAnalyzer.streamIdentity === streamIdentity) {
            this.localSpeakingAnalyzer.userId = normalizedSelfUserId;
            return;
        }

        this.removeLocalSpeakingAnalyzer();
        try {
            const sourceNode = context.createMediaStreamSource(localAudioStream);
            const analyzerNode = context.createAnalyser();
            analyzerNode.fftSize = 512;
            analyzerNode.smoothingTimeConstant = 0.72;

            const monitorGainNode = context.createGain();
            monitorGainNode.gain.value = 0;

            sourceNode.connect(analyzerNode);
            analyzerNode.connect(monitorGainNode);
            monitorGainNode.connect(context.destination);

            this.localSpeakingAnalyzer = {
                sourceNode,
                analyzerNode,
                monitorGainNode,
                dataArray: new Uint8Array(analyzerNode.fftSize),
                streamIdentity,
                userId: normalizedSelfUserId,
                lastVoiceAt: 0
            };
            this.ensureSpeakingMonitor();
        } catch {
            // Ignore local analyzer creation failures.
        }
    },

    removeRemoteSpeakingAnalyzer(feedId) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;

        const existing = this.remoteSpeakingAnalyzers[normalizedFeedId];
        if (!existing) return;

        try { existing.sourceNode?.disconnect?.(); } catch { }
        try { existing.analyzerNode?.disconnect?.(); } catch { }
        try { existing.monitorGainNode?.disconnect?.(); } catch { }
        delete this.remoteSpeakingAnalyzers[normalizedFeedId];
    },

    ensureRemoteSpeakingAnalyzer(feedId, stream) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;
        if (this.isFeedTaggedScreenAudio(normalizedFeedId) || this.getFeedRole(normalizedFeedId) === 'stream') {
            this.removeRemoteSpeakingAnalyzer(normalizedFeedId);
            return;
        }

        const audioTracks = stream?.getAudioTracks?.().filter(t => t && t.readyState === 'live') || [];
        if (audioTracks.length === 0) {
            this.removeRemoteSpeakingAnalyzer(normalizedFeedId);
            return;
        }

        const context = this.ensureRemotePlaybackAudioContext();
        if (!context) {
            return;
        }

        const userId = this.resolveFeedUserId(normalizedFeedId);
        if (!userId) {
            return;
        }

        const streamIdentity = this.getMediaStreamIdentity(stream);
        const existing = this.remoteSpeakingAnalyzers[normalizedFeedId];
        if (existing && existing.streamIdentity === streamIdentity && existing.userId === userId) {
            return;
        }

        this.removeRemoteSpeakingAnalyzer(normalizedFeedId);

        try {
            const sourceNode = context.createMediaStreamSource(stream);
            const analyzerNode = context.createAnalyser();
            analyzerNode.fftSize = 512;
            analyzerNode.smoothingTimeConstant = 0.72;

            const monitorGainNode = context.createGain();
            monitorGainNode.gain.value = 0;

            sourceNode.connect(analyzerNode);
            analyzerNode.connect(monitorGainNode);
            monitorGainNode.connect(context.destination);

            this.remoteSpeakingAnalyzers[normalizedFeedId] = {
                sourceNode,
                analyzerNode,
                monitorGainNode,
                dataArray: new Uint8Array(analyzerNode.fftSize),
                streamIdentity,
                userId,
                lastVoiceAt: 0
            };
            this.ensureSpeakingMonitor();
        } catch {
            // Ignore analyzer creation failures.
        }
    },

    resetRemoteSpeakingState() {
        this.stopSpeakingMonitor();
        this.removeLocalSpeakingAnalyzer();
        for (const feedId of Object.keys(this.remoteSpeakingAnalyzers || {})) {
            this.removeRemoteSpeakingAnalyzer(feedId);
        }
        this.remoteSpeakingAnalyzers = {};
        this.clearDrawerSpeakingState();
        this.activeSpeakingUsers.clear();
    },

    normalizeRemotePlaybackStream(stream, feedId, preferredTrack = null) {
        if (!stream) return stream;

        const dedupeKind = (kind) => {
            const tracks = kind === 'audio' ? stream.getAudioTracks() : stream.getVideoTracks();
            if (!Array.isArray(tracks) || tracks.length === 0) return;

            for (const track of tracks) {
                if (track.readyState === 'ended') {
                    try { stream.removeTrack(track); } catch { }
                }
            }

            const liveTracks = (kind === 'audio' ? stream.getAudioTracks() : stream.getVideoTracks())
                .filter(t => t.readyState !== 'ended');
            if (liveTracks.length <= 1) return;

            let keepTrack = null;
            if (preferredTrack && preferredTrack.kind === kind) {
                keepTrack = liveTracks.find(t => t.id === preferredTrack.id) || null;
            }
            if (!keepTrack) {
                keepTrack = liveTracks[liveTracks.length - 1];
            }

            let removed = 0;
            for (const track of liveTracks) {
                if (!keepTrack || track.id === keepTrack.id) continue;
                try {
                    stream.removeTrack(track);
                    removed++;
                } catch { }
            }

            if (removed > 0) {
                this.log('subscribe', `Feed ${feedId}: removed ${removed} duplicate ${kind} track(s) from playback stream`);
            }
        };

        dedupeKind('audio');
        dedupeKind('video');
        return stream;
    },

    mergeRemotePlaybackStreams(existingStream, incomingStream, feedId, preferredTrack = null) {
        const normalizedIncoming = this.normalizeRemotePlaybackStream(incomingStream, feedId, preferredTrack);
        if (!existingStream) return normalizedIncoming;
        if (!normalizedIncoming || normalizedIncoming === existingStream) {
            return this.normalizeRemotePlaybackStream(existingStream, feedId, preferredTrack);
        }

        const existingTracks = existingStream.getTracks?.() || [];
        const existingTrackIds = new Set(existingTracks.map(t => t?.id).filter(Boolean));

        const incomingTracks = normalizedIncoming.getTracks?.() || [];
        for (const track of incomingTracks) {
            if (!track || track.readyState === 'ended') continue;
            if (existingTrackIds.has(track.id)) continue;
            try {
                existingStream.addTrack(track);
                existingTrackIds.add(track.id);
            } catch { }
        }

        if (preferredTrack && preferredTrack.readyState !== 'ended' && !existingTrackIds.has(preferredTrack.id)) {
            try {
                existingStream.addTrack(preferredTrack);
            } catch { }
        }

        return this.normalizeRemotePlaybackStream(existingStream, feedId, preferredTrack);
    },

    normalizeVideoCodec(codec) {
        const normalized = (codec || 'h264').toString().trim().toLowerCase();
        if (normalized === 'auto') return 'h264';
        // hvc1 is the same codec under its container fourcc; the settings layer accepts
        // it, so fold it in rather than silently demoting to h264.
        if (normalized === 'hvc1') return 'h265';
        // av1 belongs here: leaving it out meant a selected AV1 was rewritten to h264
        // before resolveJanusVideoCodec ever saw it, so the setting could never take
        // effect no matter what the device supported.
        return ['h264', 'vp8', 'vp9', 'h265', 'av1'].includes(normalized) ? normalized : 'h264';
    },

    normalizeH264ProfileOverrideValue(rawOverride) {
        const normalized = (rawOverride || '').toString().trim().toLowerCase();
        if (!normalized) return '';

        const parts = normalized.split(':', 2);
        const profileLevelId = (parts[0] || '').trim();
        if (!/^[0-9a-f]{6}$/.test(profileLevelId)) {
            return '';
        }

        const packetizationModeRaw = (parts[1] || '').trim();
        if (!packetizationModeRaw) {
            return profileLevelId;
        }

        const packetizationMode = parseInt(packetizationModeRaw, 10);
        if (!Number.isFinite(packetizationMode) || packetizationMode < 0) {
            return profileLevelId;
        }

        return `${profileLevelId}:${packetizationMode}`;
    },

    extractFmtpValue(fmtp, key) {
        if (!fmtp || !key) return '';
        const regex = new RegExp(`(?:^|;)\\s*${key}=([^;\\s]+)\\s*(?:;|$)`, 'i');
        const match = regex.exec(fmtp.toString());
        return (match?.[1] || '').trim();
    },

    parseH264CodecDescriptor(fmtp) {
        const profileLevelId = this.extractFmtpValue(fmtp, 'profile-level-id').toLowerCase();
        const packetizationRaw = this.extractFmtpValue(fmtp, 'packetization-mode');
        const packetizationMode = Number.isFinite(parseInt(packetizationRaw, 10))
            ? parseInt(packetizationRaw, 10)
            : 0;

        return {
            profileLevelId: /^[0-9a-f]{6}$/.test(profileLevelId) ? profileLevelId : '',
            packetizationMode
        };
    },

    parseH264OverrideValue(rawOverride) {
        const normalized = this.normalizeH264ProfileOverrideValue(rawOverride);
        if (!normalized) {
            return { profileLevelId: '', packetizationMode: null };
        }

        const parts = normalized.split(':', 2);
        const profileLevelId = (parts[0] || '').trim();
        if (!/^[0-9a-f]{6}$/.test(profileLevelId)) {
            return { profileLevelId: '', packetizationMode: null };
        }

        const packetizationRaw = (parts[1] || '').trim();
        if (!packetizationRaw) {
            return { profileLevelId, packetizationMode: null };
        }

        const packetizationMode = parseInt(packetizationRaw, 10);
        if (!Number.isFinite(packetizationMode) || packetizationMode < 0) {
            return { profileLevelId, packetizationMode: null };
        }

        return { profileLevelId, packetizationMode };
    },

    getH264ProfileRank(profileLevelId) {
        if (!/^[0-9a-f]{6}$/i.test(profileLevelId || '')) {
            return 0;
        }

        const normalized = profileLevelId.toLowerCase();
        const profileIdc = parseInt(normalized.slice(0, 2), 16);
        const profileIop = parseInt(normalized.slice(2, 4), 16);
        switch (profileIdc) {
            case 0xf4: return 800; // High 4:4:4 Predictive
            case 0x7a: return 700; // High 4:2:2
            case 0x6e: return 600; // High 10
            case 0x64: return 500; // High
            case 0x4d: return 400; // Main
            case 0x58: return 300; // Extended
            case 0x42:
                return (profileIop & 0x40) !== 0 ? 200 : 250; // Constrained Baseline / Baseline
            default:
                return 100;
        }
    },

    getH264CodecPreferenceScore(profileLevelId, packetizationMode) {
        const normalized = (profileLevelId || '').toString().trim().toLowerCase();
        const levelIdc = /^[0-9a-f]{6}$/.test(normalized)
            ? parseInt(normalized.slice(4, 6), 16)
            : 0;
        const profileRank = this.getH264ProfileRank(normalized);
        const pmBonus = packetizationMode === 1 ? 20 : 0;
        return profileRank + levelIdc + pmBonus;
    },

    getH264CodecCapabilities() {
        try {
            const senderCaps = window.RTCRtpSender?.getCapabilities?.('video');
            const codecs = senderCaps?.codecs || [];
            const unique = new Map();

            for (const codec of codecs) {
                const mime = (codec?.mimeType || '').toString().toLowerCase();
                if (mime !== 'video/h264') {
                    continue;
                }

                const fmtp = (codec?.sdpFmtpLine || '').toString();
                const details = this.parseH264CodecDescriptor(fmtp);
                if (!details.profileLevelId) {
                    continue;
                }

                const key = `${details.profileLevelId}:${details.packetizationMode}`;
                if (unique.has(key)) {
                    continue;
                }

                unique.set(key, {
                    profileLevelId: details.profileLevelId,
                    packetizationMode: details.packetizationMode,
                    sdpFmtpLine: fmtp
                });
            }

            const result = [...unique.values()];
            result.sort((a, b) =>
                this.getH264CodecPreferenceScore(b.profileLevelId, b.packetizationMode)
                - this.getH264CodecPreferenceScore(a.profileLevelId, a.packetizationMode));

            return result;
        } catch {
            return [];
        }
    },

    getDefaultH264ProfileOverride() {
        const caps = this.getH264CodecCapabilities();
        if (!Array.isArray(caps) || caps.length === 0) {
            return '';
        }

        const best = caps[0];
        if (!best?.profileLevelId) {
            return '';
        }

        return `${best.profileLevelId}:${best.packetizationMode ?? 0}`;
    },

    resolveEffectiveH264Override() {
        const explicit = this.parseH264OverrideValue(this.currentConstraints?.h264ProfileOverride);
        if (explicit.profileLevelId) {
            return explicit;
        }

        const auto = this.parseH264OverrideValue(this.getDefaultH264ProfileOverride());
        if (auto.profileLevelId) {
            return auto;
        }

        return { profileLevelId: '', packetizationMode: null };
    },

    getBrowserVideoCodecSupport() {
        const support = new Set();
        const caps = window.RTCRtpSender?.getCapabilities?.('video');
        const codecs = caps?.codecs || [];
        for (const codec of codecs) {
            const mime = (codec?.mimeType || '').toString().toLowerCase();
            if (mime === 'video/h265' || mime === 'video/hevc') support.add('h265');
            if (mime === 'video/h264') support.add('h264');
            if (mime === 'video/vp8') support.add('vp8');
            if (mime === 'video/vp9') support.add('vp9');
            // Chromium reports AV1 as video/AV1; older builds used video/AV01. Missing
            // this was why AV1 never appeared in settings even on hardware that encodes
            // it - every other layer (normalizeVideoCodec, the C# filter,
            // mapSdpVideoCodecToJanus) already knew about av1, only the probe did not.
            if (mime === 'video/av1' || mime === 'video/av01') support.add('av1');
        }
        return support;
    },

    getSupportedVideoCodecs() {
        const support = this.getBrowserVideoCodecSupport();
        const ordered = ['h264', 'h265', 'av1', 'vp9', 'vp8'];
        const supported = ordered.filter(codec => support.has(codec));
        if (supported.length > 0) {
            return supported;
        }
        // Conservative fallback so settings still render if capabilities are unavailable.
        return ['h264', 'vp8'];
    },

    resolveJanusVideoCodec(preferredCodec) {
        const preferred = this.normalizeVideoCodec(preferredCodec);
        const support = this.getBrowserVideoCodecSupport();

        const pick = (codec) => support.has(codec) ? codec : null;

        if (preferred === 'h265') {
            return pick('h265') || pick('h264') || pick('vp8') || pick('vp9') || 'h264';
        }
        if (preferred === 'h264') {
            return pick('h264') || pick('vp8') || pick('vp9') || pick('h265') || 'h264';
        }
        if (preferred === 'vp9') {
            return pick('vp9') || pick('h264') || pick('vp8') || pick('h265') || 'h264';
        }
        if (preferred === 'vp8') {
            return pick('vp8') || pick('h264') || pick('vp9') || pick('h265') || 'h264';
        }
        if (preferred === 'av1') {
            // Without this branch a selected AV1 fell through to the h264 return below,
            // so even a device that advertised AV1 silently published h264.
            return pick('av1') || pick('h264') || pick('vp9') || pick('vp8') || 'h264';
        }
        return 'h264';
    },

    getJanusRequestedVideoCodec() {
        // Janus request-side codec must match browser capability, otherwise Janus may disable video mids.
        return this.resolveJanusVideoCodec(this.currentConstraints.preferredVideoCodec);
    },

    mapSdpVideoCodecToJanus(codec) {
        const normalized = (codec || '').toString().trim().toUpperCase();
        if (normalized === 'H264') return 'h264';
        if (normalized === 'VP8') return 'vp8';
        if (normalized === 'VP9') return 'vp9';
        if (normalized === 'H265' || normalized === 'HEVC') return 'h265';
        if (normalized === 'AV1') return 'av1';
        return '';
    },

    getJanusVideoCodecForSdp(sdp, fallbackCodec = '') {
        const codecFromSdp = this.getCodecFromSdp(sdp || '', 'video');
        const janusCodec = this.mapSdpVideoCodecToJanus(codecFromSdp);
        if (janusCodec) {
            return janusCodec;
        }

        if (fallbackCodec) {
            return this.resolveJanusVideoCodec(fallbackCodec);
        }

        return this.getJanusRequestedVideoCodec();
    },

    getActivePublisherJanusVideoCodec() {
        const sdp = this.publisherPc?.localDescription?.sdp || '';
        return this.getJanusVideoCodecForSdp(sdp, this.currentConstraints.preferredVideoCodec);
    },

    tuneReceiverPlayout(receiver, kind, scope) {
        if (!receiver) return;

        const mediaKind = (kind || receiver.track?.kind || 'video').toLowerCase();
        const jitterTarget = mediaKind === 'audio'
            ? this.receiverAudioJitterBufferTargetMs
            : this.receiverVideoJitterBufferTargetMs;
        const playoutDelay = mediaKind === 'audio'
            ? this.receiverAudioPlayoutDelayHintS
            : this.receiverVideoPlayoutDelayHintS;

        try {
            if ('jitterBufferTarget' in receiver && Number.isFinite(jitterTarget) && jitterTarget >= 0) {
                receiver.jitterBufferTarget = jitterTarget;
            }
        } catch (err) {
            this.log('subscribe', `${scope}: failed to set jitterBufferTarget for ${mediaKind}: ${err.message}`);
        }

        try {
            if ('playoutDelayHint' in receiver && Number.isFinite(playoutDelay) && playoutDelay >= 0) {
                receiver.playoutDelayHint = playoutDelay;
            }
        } catch (err) {
            this.log('subscribe', `${scope}: failed to set playoutDelayHint for ${mediaKind}: ${err.message}`);
        }
    },

    tunePeerReceiversPlayout(pc, scope) {
        if (!pc) return;
        const receivers = pc.getReceivers?.() || [];
        for (const receiver of receivers) {
            this.tuneReceiverPlayout(receiver, receiver?.track?.kind || '', scope);
        }
        this.enforceVideoReceiverPlayoutDelay(pc, scope);
    },

    enforceVideoReceiverPlayoutDelay(peerConnection, scope) {
        if (!peerConnection || typeof peerConnection.getReceivers !== 'function') return;
        const receiver = peerConnection.getReceivers().find(r => r?.track?.kind === 'video');
        if (!receiver) return;
        try {
            if ('playoutDelayHint' in receiver) {
                receiver.playoutDelayHint = 0.1;
            }
        } catch (err) {
            this.log('subscribe', `${scope}: failed to force video playoutDelayHint=0.1: ${err.message}`);
        }
    },

    getAudioMlineInfoFromSdp(sdp) {
        const mids = new Set();
        if (!sdp) return { mids };
        const lines = sdp.split(/\r?\n/);
        let currentKind = '';
        for (const line of lines) {
            if (!line) continue;
            if (line.startsWith('m=')) {
                currentKind = line.startsWith('m=audio ') ? 'audio' : (line.startsWith('m=video ') ? 'video' : '');
                continue;
            }
            if (currentKind !== 'audio') continue;
            const midMatch = /^a=mid:([^\s]+)/i.exec(line);
            if (midMatch?.[1]) {
                mids.add(midMatch[1].trim());
            }
        }
        return { mids };
    },

    enforceSubscriberVideoOnlyTransceivers(pc, remoteSdp, scope) {
        if (!pc || typeof pc.getTransceivers !== 'function') return;
        const audioInfo = this.getAudioMlineInfoFromSdp(remoteSdp);
        const transceivers = pc.getTransceivers() || [];
        let changed = 0;
        for (let i = 0; i < transceivers.length; i++) {
            const transceiver = transceivers[i];
            if (!transceiver) continue;
            const receiverKind = (transceiver.receiver?.track?.kind || '').toString().toLowerCase();
            const mid = (transceiver.mid || '').toString().trim();
            const isAudio = receiverKind === 'audio' || (mid && audioInfo.mids.has(mid));
            if (!isAudio) continue;
            try {
                if (transceiver.direction !== 'inactive') {
                    transceiver.direction = 'inactive';
                    changed++;
                }
            } catch (err) {
                this.log('subscribe', `${scope}: failed to set audio transceiver inactive (mid=${mid || 'n/a'}, idx=${i}): ${err.message}`);
            }
        }
        this.log('subscribe', `${scope}: video-only transceiver gate changed=${changed}/${transceivers.length}, audioMids=[${Array.from(audioInfo.mids).join(',')}]`);
    },

    tryResumeRemoteVideo(vid, feedId, reason) {
        if (!vid) return;

        const now = Date.now();
        const lastResumeAt = parseInt(vid.dataset.lastResumeAt || '0', 10) || 0;
        if (now - lastResumeAt < 1500) return;
        vid.dataset.lastResumeAt = String(now);

        const playPromise = vid.play?.();
        if (playPromise?.catch) {
            playPromise.catch(err =>
                this.log('subscribe', `Remote play() retry failed for feed ${feedId} (${reason}): ${err.message}`));
        }
    },

    configureRemoteVideoElement(vid, feedId) {
        if (!vid) return;
        vid.autoplay = true;
        vid.playsInline = true;
        vid.setAttribute('playsinline', 'true');
        if ('disablePictureInPicture' in vid) {
            vid.disablePictureInPicture = true;
        }
        vid.defaultPlaybackRate = 1.0;
        vid.playbackRate = 1.0;

        if (vid.dataset.feedConfigured === '1') return;

        vid.addEventListener('waiting', () => this.tryResumeRemoteVideo(vid, feedId, 'waiting'));
        vid.addEventListener('stalled', () => this.tryResumeRemoteVideo(vid, feedId, 'stalled'));
        vid.addEventListener('suspend', () => this.tryResumeRemoteVideo(vid, feedId, 'suspend'));

        vid.dataset.feedConfigured = '1';
    },

    cleanupStalePendingSubscribes() {
        const now = Date.now();
        for (const [handleId, pending] of Object.entries(this.pendingSubscribes)) {
            if (!pending || !pending.createdAt) continue;
            if ((now - pending.createdAt) < this.pendingSubscribeTimeoutMs) continue;
            this.log('subscribe', `Pending subscribe timed out for feed ${pending.feedId} (handle ${handleId}), clearing`);
            delete this.pendingSubscribes[handleId];
        }
    },

    schedulePendingSubscribeNudge(subHandleId, delayMs, reason) {
        const pending = this.pendingSubscribes[subHandleId];
        if (!pending) return;

        const delay = Math.max(0, parseInt(delayMs, 10) || 0);
        const now = Date.now();
        const nextAllowedAt = pending.nextNudgeAt || 0;
        if (nextAllowedAt > now) return;
        pending.nextNudgeAt = now + delay + 300;

        setTimeout(() => {
            const current = this.pendingSubscribes[subHandleId];
            if (!current) return;
            this.requestSubscriberOffer(subHandleId, current.feedId, reason).catch(() => { });
        }, delay);
    },

    normalizeSubscribeStreams(streamsHint, fallbackFeedId) {
        const out = [];
        const dedupe = new Set();
        const feedNum = Number(fallbackFeedId);
        if (Array.isArray(streamsHint)) {
            for (const s of streamsHint) {
                if (!s || typeof s !== 'object') continue;
                if (s.disabled === true) continue;
                if (s.active === false) continue;
                if (s.send === false) continue;
                const normalizedFeed = Number(s.feed ?? s.feed_id ?? fallbackFeedId);
                const feed = Number.isFinite(normalizedFeed) ? normalizedFeed : feedNum;
                if (!Number.isFinite(feed)) continue;

                const entry = { feed };
                const mid = (s.feed_mid ?? s.feedMid ?? s.publisher_mid ?? s.publisherMid ?? s.mid ?? '')
                    .toString()
                    .trim();
                if (mid) entry.mid = mid;
                const type = (s.type ?? '').toString().trim().toLowerCase();
                if (type === 'audio' || type === 'video') {
                    entry.type = type;
                }
                const key = `${entry.feed}:${entry.mid || ''}:${entry.type || ''}`;
                if (dedupe.has(key)) continue;
                dedupe.add(key);
                out.push(entry);
            }
        }

        if (out.length > 0) return out;
        if (Number.isFinite(feedNum)) return [{ feed: feedNum }];
        return [];
    },

    isAudioSubscribeStream(streamInfo) {
        if (!streamInfo || typeof streamInfo !== 'object') return false;
        const type = (streamInfo.type || '').toString().trim().toLowerCase();
        if (type === 'audio') return true;

        const mid = (streamInfo.mid || '').toString().trim().toLowerCase();
        if (!mid) return false;
        if (mid.startsWith('a')) return true;
        if (mid === 'audio') return true;
        return false;
    },

    filterVideoOnlySubscribeStreams(streams) {
        if (!Array.isArray(streams)) return [];
        return streams.filter(s => !this.isAudioSubscribeStream(s));
    },

    selectVideoPreferredSubscribeStreams(streams, fallbackFeedId) {
        const normalized = this.normalizeSubscribeStreams(streams, fallbackFeedId);
        if (normalized.length === 0) {
            const feedNum = Number(fallbackFeedId);
            return Number.isFinite(feedNum) ? [{ feed: feedNum }] : [];
        }

        const explicitVideo = normalized.filter(streamInfo => {
            if (!streamInfo || typeof streamInfo !== 'object') return false;
            const type = (streamInfo.type || '').toString().trim().toLowerCase();
            if (type === 'video') return true;
            const mid = (streamInfo.mid || '').toString().trim().toLowerCase();
            return mid === 'video' || mid.startsWith('v');
        });
        if (explicitVideo.length > 0) {
            return explicitVideo;
        }

        const nonAudio = normalized.filter(streamInfo => !this.isAudioSubscribeStream(streamInfo));
        if (nonAudio.length === 1) {
            return nonAudio;
        }
        if (nonAudio.length > 1) {
            const withMid = nonAudio.filter(streamInfo => ((streamInfo?.mid || '').toString().trim().length > 0));
            if (withMid.length === 1) {
                return withMid;
            }
            return [nonAudio[0]];
        }

        // Janus occasionally omits or mislabels stream type metadata. For native-screen
        // feeds we still force a single stream request to avoid drifting into audio-only.
        return [normalized[0]];
    },

    findPendingSubscribeByFeed(feedId) {
        const target = Number(feedId);
        if (!Number.isFinite(target)) return null;
        for (const [handleId, pending] of Object.entries(this.pendingSubscribes)) {
            if (!pending) continue;
            if (Number(pending.feedId) === target) {
                return { handleId, pending };
            }
        }
        return null;
    },

    updatePublisherStreamHint(feedId, streams) {
        const key = String(feedId);
        const existing = this.publisherStreamHints[key];

        // Nothing to learn from: listparticipants does not report streams at all, so
        // writing a hint here invented "this publisher has neither audio nor video"
        // out of an absent field. That false negative is permanent and silently
        // disables repair, because needVideoRefresh is gated on hint.hasVideo - a feed
        // discovered by the participant sweep could never be told apart from one whose
        // publisher genuinely sends nothing. Absent has to stay absent so the recovery
        // paths can distinguish "unknown" from "known to have none".
        if (!Array.isArray(streams) && !existing) {
            return;
        }

        const previous = existing || {};
        const normalized = this.normalizeSubscribeStreams(streams, feedId);
        let hasVideo = !!previous.hasVideo;
        let hasAudio = !!previous.hasAudio;
        if (Array.isArray(streams)) {
            hasVideo = streams.some(s =>
                s
                && s.type === 'video'
                && s.disabled !== true
                && s.active !== false
                && s.send !== false);
            hasAudio = streams.some(s =>
                s
                && s.type === 'audio'
                && s.disabled !== true
                && s.active !== false
                && s.send !== false);
        }
        this.publisherStreamHints[key] = {
            streams: normalized,
            raw: Array.isArray(streams) ? streams : [],
            hasVideo,
            hasAudio
        };
    },

    hasIncomingVideoTrack(pc) {
        if (!pc || typeof pc.getReceivers !== 'function') return false;
        return pc.getReceivers().some(r => r?.track?.kind === 'video' && r.track.readyState === 'live');
    },

    hasIncomingAudioTrack(pc) {
        if (!pc || typeof pc.getReceivers !== 'function') return false;
        return pc.getReceivers().some(r => r?.track?.kind === 'audio' && r.track.readyState === 'live');
    },

    scheduleExistingSubscriberRefresh(feedId, delayMs, reason, options = null) {
        const feed = this.feeds[feedId];
        if (!feed?.handleId) return;
        if (feed.preferVideoOnly === true && options?.requireVideo !== true) {
            return;
        }

        const delay = Math.max(0, parseInt(delayMs, 10) || 0);
        const now = Date.now();
        if ((feed.nextRefreshAt || 0) > now) return;
        feed.nextRefreshAt = now + delay + 400;

        setTimeout(() => {
            // Logged, not swallowed: this is the recovery path for a feed that is already
            // missing media, so a failure here is the difference between "audio came back"
            // and "that person is silent for the rest of the call".
            this.requestExistingSubscriberRefresh(feedId, reason, options)
                .catch(err => this.logError(
                    'subscribe',
                    `Subscriber refresh for feed ${feedId} (${reason}) failed: ${err?.message || err}`));
        }, delay);
    },

    async requestExistingSubscriberRefresh(feedId, reason, options = null) {
        const feed = this.feeds[feedId];
        if (!feed?.handleId) return;
        if (!feed.pc) return;
        const preferVideoOnly = feed.preferVideoOnly === true;
        const requireAudio = options?.requireAudio === true;
        const requireVideo = options?.requireVideo === true;
        const force = options?.force === true;
        const shouldCheckAudio = requireAudio || (!requireAudio && !requireVideo);
        const shouldCheckVideo = requireVideo || (!requireAudio && !requireVideo);

        const missingAudio = shouldCheckAudio && !this.hasIncomingAudioTrack(feed.pc);
        const missingVideo = shouldCheckVideo && !this.hasIncomingVideoTrack(feed.pc);
        if (!missingAudio && !missingVideo) return;
        if (preferVideoOnly && missingAudio && !missingVideo) return;

        const hint = this.publisherStreamHints[String(feedId)];
        if (!force) {
            if (missingVideo && !hint?.hasVideo) return;
            if (missingAudio && hint && hint.hasAudio === false) return;
        }

        // hint can be absent entirely, and the guards above say so themselves (hint?.,
        // hint &&). A publisher discovered by the listparticipants sweep rather than by a
        // publishers event never gets one, because Janus does not report streams there -
        // which is precisely when callers pass force:true and skip those guards. So this
        // line threw, before the log below, into a caller that swallowed it: audio
        // recovery silently never ran for exactly the feeds that needed it, leaving
        // nothing in the log to find. No hint means "subscribe to whatever they
        // publish", which is what normalizeSubscribeStreams returns for undefined.
        let streams = this.normalizeSubscribeStreams(hint?.streams, feedId);
        if (preferVideoOnly) {
            streams = this.selectVideoPreferredSubscribeStreams(streams, feedId);
        }
        this.log(
            'subscribe',
            `Requesting subscriber refresh for feed ${feedId} (handle ${feed.handleId}), reason=${reason}, missingAudio=${missingAudio}, missingVideo=${missingVideo}`
        );

        const result = await this.apiPost(
            `/api/janus/session/${this.janusSessionId}/handle/${feed.handleId}/message-nowait`,
            {
                body: {
                    request: 'subscribe',
                    streams,
                    offer_audio: !preferVideoOnly,
                    offer_video: true,
                    offer_data: false
                }
            }
        );

        if (result?.jsep) {
            this.log('subscribe', `Subscriber refresh returned immediate JSEP for feed ${feedId}`);
            await this.renegotiateExistingSubscriber(feed.handleId, result.jsep);
        }
    },

    normalizeUserId(userId) {
        return (userId || '').toString().trim().toLowerCase();
    },

    getParticipantDisplayName(userId) {
        const key = this.normalizeUserId(userId);
        if (!key) return '';
        return this.participantDirectory[key] || '';
    },

    getParticipantAvatarUrl(userId) {
        const key = this.normalizeUserId(userId);
        if (!key) return '';
        return this.participantAvatarDirectory[key] || '';
    },

    /**
     * Memoized on the raw display string — a feed's display is immutable for its lifetime, and
     * this sits under getFeedRole/getFeedIdentityKey, which run per feed per sibling per pass.
     *
     * The returned object is SHARED between callers. Treat it as read-only.
     */
    parsePublisherDisplay(display) {
        if (!display || typeof display !== 'string') {
            return { userId: '', displayName: '', role: '' };
        }

        const cache = this._displayParseCache || (this._displayParseCache = new Map());
        const cached = cache.get(display);
        if (cached) return cached;

        const parsed = this.parsePublisherDisplayUncached(display);
        // Bounded: displays churn as publishers come and go over a long call.
        if (cache.size >= this.displayParseCacheLimit) cache.clear();
        cache.set(display, parsed);
        return parsed;
    },

    parsePublisherDisplayUncached(display) {
        const fallback = { userId: '', displayName: '', role: '' };
        if (!display || typeof display !== 'string') return fallback;

        const trimmed = display.trim();
        if (!trimmed) return fallback;

        if (trimmed.startsWith('{') && trimmed.endsWith('}')) {
            try {
                const parsed = JSON.parse(trimmed);
                return {
                    userId: (parsed.userId || '').toString(),
                    displayName: (parsed.displayName || parsed.name || '').toString(),
                    role: (parsed.role || '').toString().trim().toLowerCase()
                };
            } catch {
                // Fall through to legacy parsing.
            }
        }

        // Legacy mode: plain display string was either a GUID or a raw label.
        if (/^[0-9a-fA-F-]{20,}$/.test(trimmed)) {
            return { userId: trimmed, displayName: '', role: '' };
        }

        return { userId: '', displayName: trimmed, role: '' };
    },

    getDisplayIdentityKey(display) {
        const parsed = this.parsePublisherDisplay(display);
        if (parsed.userId) {
            const normalizedUser = this.normalizeUserId(parsed.userId);
            if (normalizedUser) return `uid:${normalizedUser}`;
        }
        if (parsed.displayName) {
            const normalizedDisplay = parsed.displayName.trim().toLowerCase();
            if (normalizedDisplay) return `name:${normalizedDisplay}`;
        }
        const normalizedRaw = (display || '').toString().trim().toLowerCase();
        return normalizedRaw ? `raw:${normalizedRaw}` : '';
    },

    getDisplayRole(display) {
        const parsed = this.parsePublisherDisplay(display);
        return (parsed.role || '').toString().trim().toLowerCase();
    },

    collapsePublisherCandidates(publishers) {
        if (!Array.isArray(publishers) || publishers.length <= 1) {
            return Array.isArray(publishers) ? publishers : [];
        }

        const deduped = [];
        const seenNativeScreen = new Set();
        for (let i = publishers.length - 1; i >= 0; i--) {
            const pub = publishers[i];
            if (!pub) continue;

            const role = this.getDisplayRole(pub.display);
            const identityKey = this.getDisplayIdentityKey(pub.display);
            if (role === 'native-screen' && identityKey) {
                const dedupeKey = `${role}:${identityKey}`;
                if (seenNativeScreen.has(dedupeKey)) continue;
                seenNativeScreen.add(dedupeKey);
            }

            deduped.push(pub);
        }

        deduped.reverse();
        return deduped;
    },

    replaceDuplicateNativeScreenFeeds(targetFeedId, display) {
        if (this.getDisplayRole(display) !== 'native-screen') return;
        const identityKey = this.getDisplayIdentityKey(display);
        if (!identityKey) return;

        const targetKey = String(targetFeedId);

        for (const existingFeedId of Object.keys(this.feeds || {})) {
            const existingKey = String(existingFeedId);
            if (existingKey === targetKey) continue;
            const existingFeed = this.feeds[existingKey];
            if (!existingFeed) continue;
            if (this.getDisplayRole(existingFeed.display) !== 'native-screen') continue;
            if (this.getFeedIdentityKey(existingKey) !== identityKey) continue;

            this.log('subscribe', `Replacing duplicate native-screen feed ${existingKey} with ${targetKey} for ${identityKey}`);
            this.removeFeed(existingKey);
        }

        for (const pendingFeedKey of Object.keys(this.pendingSubscribeFeeds || {})) {
            const pendingKey = String(pendingFeedKey);
            if (pendingKey === targetKey) continue;
            const pending = this.pendingSubscribeFeeds[pendingKey];
            if (!pending) continue;
            if (this.getDisplayRole(pending.display) !== 'native-screen') continue;
            if (this.getDisplayIdentityKey(pending.display) !== identityKey) continue;

            delete this.pendingSubscribeFeeds[pendingKey];
            this.log('subscribe', `Dropped duplicate pending native-screen subscribe feed ${pendingKey} in favor of ${targetKey}`);
        }

        for (const [handleId, pending] of Object.entries(this.pendingSubscribes || {})) {
            if (!pending) continue;
            if (String(pending.feedId) === targetKey) continue;
            if (this.getDisplayRole(pending.display) !== 'native-screen') continue;
            if (this.getDisplayIdentityKey(pending.display) !== identityKey) continue;

            delete this.pendingSubscribes[handleId];
            this.log('subscribe', `Dropped duplicate pending native-screen handle ${handleId} (feed ${pending.feedId}) in favor of ${targetKey}`);
        }
    },

    buildPublisherDisplay(role = '') {
        const userId = (this.userId || '').toString();
        const displayName = (this.userDisplayName || userId || 'Unknown').toString();
        const normalizedRole = (role || '').toString().trim().toLowerCase();
        if (normalizedRole) {
            return JSON.stringify({ userId, displayName, role: normalizedRole });
        }
        return JSON.stringify({ userId, displayName });
    },

    isSelfPublisherDisplay(display) {
        const parsed = this.parsePublisherDisplay(display);
        if (parsed.userId && this.userId && this.normalizeUserId(parsed.userId) === this.normalizeUserId(this.userId)) {
            const role = (parsed.role || '').toString().trim().toLowerCase();
            if (role === 'native-screen') {
                // Allow self-subscribe for native Windows screen publisher preview.
                return false;
            }
            return true;
        }
        return display === this.userId;
    },

    isSelfNativeScreenDisplay(display) {
        const parsed = this.parsePublisherDisplay(display);
        if (!parsed.userId || !this.userId) return false;
        return this.normalizeUserId(parsed.userId) === this.normalizeUserId(this.userId)
            && (parsed.role || '').toString().trim().toLowerCase() === 'native-screen';
    },

    isNativeScreenDisplay(display) {
        const parsed = this.parsePublisherDisplay(display);
        return (parsed.role || '').toString().trim().toLowerCase() === 'native-screen';
    },

    maybeNotifyRemoteParticipant(display) {
        const parsed = this.parsePublisherDisplay(display);
        if (!parsed.userId) return;
        if (this.userId && parsed.userId === this.userId) return;
        this.notifyDotNet('OnRemoteParticipantSeen', parsed.userId);
    },

    updateParticipantDirectory(directory) {
        const next = {};
        if (directory && typeof directory === 'object') {
            for (const [userId, displayName] of Object.entries(directory)) {
                const key = this.normalizeUserId(userId);
                const value = (displayName || '').toString().trim();
                if (!key || !value) continue;
                next[key] = value;
            }
        }
        this.participantDirectory = next;
        this.refreshRemoteFeedLabels();
        this.refreshRemoteFeedAvatarFallbacks();
        this.updateLocalFeedAvatarFallback();
    },

    updateParticipantAvatarDirectory(directory) {
        const next = {};
        if (directory && typeof directory === 'object') {
            for (const [userId, avatarUrl] of Object.entries(directory)) {
                const key = this.normalizeUserId(userId);
                const value = (avatarUrl || '').toString().trim();
                if (!key || !value) continue;
                next[key] = value;
            }
        }

        this.participantAvatarDirectory = next;
        this.refreshRemoteFeedAvatarFallbacks();
        this.updateLocalFeedAvatarFallback();
    },

    applyParticipantProfiles(participantDirectory, participantAvatarDirectory) {
        this.updateParticipantDirectory(participantDirectory || {});
        this.updateParticipantAvatarDirectory(participantAvatarDirectory || {});
    },

    refreshRemoteFeedLabels() {
        const cells = document.querySelectorAll('.video-cell[id^="remote-"]');
        cells.forEach(cell => {
            const label = cell.querySelector('.video-label');
            if (!label) return;
            const display = cell.dataset.display || '';
            const feedId = cell.dataset.feedId || (cell.id || '').replace('remote-', '');
            label.textContent = this.getRemoteFeedDisplayName(feedId, display);
        });
    },

    refreshRemoteFeedAvatarFallbacks() {
        const cells = document.querySelectorAll('.video-cell[id^="remote-"]');
        cells.forEach(cell => {
            const feedId = cell.dataset.feedId || (cell.id || '').replace('remote-', '');
            const display = cell.dataset.display || '';
            this.ensureRemoteFeedAvatarFallback(cell, feedId, display);
        });
    },

    clearCellOverlayHideTimer(cell) {
        if (!cell) return;
        const timer = this.controlOverlayHideTimers.get(cell);
        if (timer) {
            clearTimeout(timer);
            this.controlOverlayHideTimers.delete(cell);
        }
    },

    showCellOverlayControls(cell, scheduleAutoHide = true) {
        if (!cell) return;
        cell.classList.add('controls-visible');
        if (scheduleAutoHide) {
            this.scheduleCellOverlayHide(cell);
        }
    },

    hideCellOverlayControls(cell) {
        if (!cell) return;
        this.clearCellOverlayHideTimer(cell);
        cell.classList.remove('controls-visible');
    },

    scheduleCellOverlayHide(cell, delayMs = null) {
        if (!cell) return;
        this.clearCellOverlayHideTimer(cell);
        // Fullscreen turns the cell into the whole screen, where a lingering
        // toolbar is far more intrusive - hide it faster there.
        const isFullscreenCell = document.fullscreenElement === cell;
        const fallbackDelay = isFullscreenCell ? 3000 : 5000;
        const hideDelay = Math.max(400, parseInt(delayMs, 10) || fallbackDelay);
        const timer = setTimeout(() => {
            this.controlOverlayHideTimers.delete(cell);
            if (!cell.isConnected) return;
            const activeElement = document.activeElement;
            if (activeElement && cell.contains(activeElement)) {
                // Keyboard focus pins the overlay (someone tabbing through the
                // controls must not have them vanish mid-navigation)...
                let keyboardFocus = false;
                try { keyboardFocus = activeElement.matches(':focus-visible'); } catch { }
                if (keyboardFocus) return;

                // ...but a mouse/touch click parks focus on the button it hit, and
                // in fullscreen that residual focus must not hold the overlay open.
                if (document.fullscreenElement === cell) {
                    try { activeElement.blur(); } catch { }
                } else {
                    return;
                }
            }
            cell.classList.remove('controls-visible');
        }, hideDelay);
        this.controlOverlayHideTimers.set(cell, timer);
    },

    // ─── Per-cell video zoom (wheel / pinch / drag-pan) ────────────────────────
    //
    // The transform is published as a CSS custom property on the cell and applied
    // by CSS to BOTH the <video> and the annotation canvas, so drawn ink stays
    // registered with the frame while zoomed. State lives per cell element; a
    // removed cell takes its state with it.

    cellZoomStates: new WeakMap(),

    getCellZoom(cell) {
        return this.cellZoomStates.get(cell) || { s: 1, tx: 0, ty: 0 };
    },

    setCellZoom(cell, s, tx, ty) {
        if (!cell) return;
        s = Math.min(8, Math.max(1, s));

        // Clamp the pan so the (scaled) video always covers the cell - no gaps.
        const rect = cell.getBoundingClientRect();
        tx = Math.min(0, Math.max(rect.width * (1 - s), tx));
        ty = Math.min(0, Math.max(rect.height * (1 - s), ty));

        if (s <= 1.001) {
            this.cellZoomStates.delete(cell);
            cell.classList.remove('video-zoomed');
            cell.style.removeProperty('--video-zoom');
            this.updateZoomBadge(cell, 1);
            return;
        }

        this.cellZoomStates.set(cell, { s, tx, ty });
        cell.classList.add('video-zoomed');
        cell.style.setProperty('--video-zoom', `translate(${tx}px, ${ty}px) scale(${s})`);
        this.updateZoomBadge(cell, s);
    },

    /// Zooms keeping the content under (clientX, clientY) stationary.
    zoomCellAt(cell, clientX, clientY, factor) {
        const rect = cell.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;

        const { s, tx, ty } = this.getCellZoom(cell);
        const ns = Math.min(8, Math.max(1, s * factor));
        const cx = clientX - rect.left;
        const cy = clientY - rect.top;
        const contentX = (cx - tx) / s;
        const contentY = (cy - ty) / s;
        this.setCellZoom(cell, ns, cx - (contentX * ns), cy - (contentY * ns));
    },

    updateZoomBadge(cell, scale) {
        let badge = cell.querySelector('.video-zoom-badge');
        if (scale <= 1.001) {
            badge?.remove();
            return;
        }

        if (!badge) {
            badge = document.createElement('button');
            badge.type = 'button';
            badge.className = 'video-zoom-badge';
            badge.title = 'Reset zoom';
            badge.addEventListener('click', (ev) => {
                ev.stopPropagation();
                this.setCellZoom(cell, 1, 0, 0);
            });
            cell.appendChild(badge);
        }

        badge.textContent = `${scale.toFixed(1)}×`;
    },

    isZoomExemptTarget(target) {
        return !!(target.closest?.('.video-controls')
            || target.closest?.('.video-info-panel')
            || target.closest?.('.remote-volume-panel')
            || target.closest?.('.video-zoom-badge')
            || target.closest?.('.annotation-toolbar-host')
            || target.closest?.('.annotation-toolbar'));
    },

    bindCellZoomInteraction(cell) {
        if (!cell || cell.dataset.zoomBound === '1') return;
        cell.dataset.zoomBound = '1';

        // Wheel: Ctrl+wheel anywhere (the desktop zoom convention); plain wheel
        // only in fullscreen, where there is nothing else the wheel could scroll.
        cell.addEventListener('wheel', (ev) => {
            const isFullscreen = document.fullscreenElement === cell;
            if (!ev.ctrlKey && !isFullscreen) return;
            if (this.isZoomExemptTarget(ev.target)) return;

            ev.preventDefault();
            const factor = Math.exp(-ev.deltaY * 0.0015);
            this.zoomCellAt(cell, ev.clientX, ev.clientY, factor);
        }, { passive: false });

        // Pointer gestures: two pointers pinch, one pointer pans once zoomed in.
        // While annotation drawing is active the canvas captures its pointerdowns
        // and stops propagation, so drawing naturally wins over pan - wheel zoom
        // still works because wheel events keep bubbling.
        const activePointers = new Map();
        let pinchOrigin = null;
        let panOrigin = null;
        let gestureMoved = false;

        cell.addEventListener('pointerdown', (ev) => {
            if (this.isZoomExemptTarget(ev.target)) return;

            activePointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
            const zoom = this.getCellZoom(cell);

            if (activePointers.size === 2) {
                const [a, b] = [...activePointers.values()];
                pinchOrigin = {
                    dist: Math.hypot(a.x - b.x, a.y - b.y),
                    s: zoom.s, tx: zoom.tx, ty: zoom.ty
                };
                panOrigin = null;
            } else if (activePointers.size === 1 && zoom.s > 1) {
                panOrigin = { x: ev.clientX, y: ev.clientY, tx: zoom.tx, ty: zoom.ty };
                gestureMoved = false;
                try { cell.setPointerCapture(ev.pointerId); } catch { }
            }
        });

        cell.addEventListener('pointermove', (ev) => {
            if (!activePointers.has(ev.pointerId)) return;
            activePointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });

            if (activePointers.size === 2 && pinchOrigin?.dist > 0) {
                const [a, b] = [...activePointers.values()];
                const dist = Math.hypot(a.x - b.x, a.y - b.y);
                if (dist <= 0) return;

                const rect = cell.getBoundingClientRect();
                const centerX = ((a.x + b.x) / 2) - rect.left;
                const centerY = ((a.y + b.y) / 2) - rect.top;
                const ns = Math.min(8, Math.max(1, pinchOrigin.s * (dist / pinchOrigin.dist)));
                const contentX = (centerX - pinchOrigin.tx) / pinchOrigin.s;
                const contentY = (centerY - pinchOrigin.ty) / pinchOrigin.s;
                this.setCellZoom(cell, ns, centerX - (contentX * ns), centerY - (contentY * ns));
                gestureMoved = true;
                ev.preventDefault();
            } else if (panOrigin) {
                const dx = ev.clientX - panOrigin.x;
                const dy = ev.clientY - panOrigin.y;
                if (Math.abs(dx) + Math.abs(dy) > 4) gestureMoved = true;
                const zoom = this.getCellZoom(cell);
                this.setCellZoom(cell, zoom.s, panOrigin.tx + dx, panOrigin.ty + dy);
                ev.preventDefault();
            }
        });

        const endPointer = (ev) => {
            activePointers.delete(ev.pointerId);
            if (activePointers.size < 2) pinchOrigin = null;
            if (activePointers.size === 0) panOrigin = null;

            if (gestureMoved) {
                gestureMoved = false;
                // The click that follows a drag/pinch must not toggle spotlight.
                cell.dataset.suppressTileClick = '1';
                setTimeout(() => cell.removeAttribute('data-suppress-tile-click'), 150);
            }
        };
        cell.addEventListener('pointerup', endPointer);
        cell.addEventListener('pointercancel', endPointer);
    },

    bindCellOverlayAutoHide(cell) {
        if (!cell || cell.dataset.overlayAutoHideBound === '1') return;

        // Every interactive cell also gets the zoom gestures; both binders guard
        // against double-binding themselves.
        this.bindCellZoomInteraction(cell);

        // A stroke in progress must never summon the overlay. The annotation canvas
        // stops its own pointer events, but a reveal can still be reached by a move
        // that began outside it - and the toolbar fading in under the user's finger
        // halfway through a line is exactly what they are trying to avoid.
        const reveal = () => {
            if (window.screenAnnotation?.isDrawing?.()) return;
            this.showCellOverlayControls(cell, true);
        };
        const hideNow = () => this.hideCellOverlayControls(cell);

        cell.addEventListener('mouseenter', reveal);
        cell.addEventListener('mousemove', reveal);
        cell.addEventListener('pointermove', reveal);
        cell.addEventListener('pointerdown', reveal);
        cell.addEventListener('touchstart', reveal, { passive: true });
        cell.addEventListener('focusin', () => this.showCellOverlayControls(cell, false));
        cell.addEventListener('focusout', () => this.scheduleCellOverlayHide(cell, 1500));
        cell.addEventListener('mouseleave', hideNow);

        cell.dataset.overlayAutoHideBound = '1';
        this.showCellOverlayControls(cell, true);
    },

    bindSpotlightInteraction(cell, streamKey, allowTileClick = true) {
        if (!cell) return;
        cell.dataset.streamKey = streamKey;
        cell.dataset.allowTileSpotlight = allowTileClick ? '1' : '0';
        if (cell.dataset.spotlightBound === '1') return;

        cell.addEventListener('click', (ev) => {
            if (cell.dataset.allowTileSpotlight !== '1') {
                return;
            }
            // A pan/pinch gesture ends in a click; that click is not a request to
            // change the spotlight.
            if (cell.dataset.suppressTileClick === '1') {
                return;
            }
            if (ev.target.closest('.video-controls')
                || ev.target.closest('.video-info-panel')
                || ev.target.closest('.annotation-toolbar-host')) {
                return;
            }
            this.toggleSpotlight(streamKey);
        });
        cell.dataset.spotlightBound = '1';
    },

    ensureSpotlightButton(controls, streamKey) {
        if (!controls) return;

        let btn = controls.querySelector('.video-spotlight-btn');
        if (!btn) {
            btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'video-control-btn video-spotlight-btn';
            btn.addEventListener('click', (ev) => {
                ev.stopPropagation();
                this.toggleSpotlight(streamKey);
            });
            controls.prepend(btn);
        }
        btn.dataset.streamKey = streamKey;
    },

    toggleSpotlight(streamKey) {
        if (this.selectedSpotlightStreamKey === streamKey) {
            this.selectedSpotlightStreamKey = null;
        } else {
            this.selectedSpotlightStreamKey = streamKey;
        }
        this.applySpotlightLayout();
    },

    clearSpotlight() {
        this.selectedSpotlightStreamKey = null;
        this.applySpotlightLayout();
    },

    applySpotlightLayout() {
        const videoGrid = document.getElementById('videoGrid');
        if (!videoGrid) return;

        const cells = Array.from(videoGrid.querySelectorAll('.video-cell'));
        let mainCell = null;
        if (this.selectedSpotlightStreamKey) {
            mainCell = cells.find(c => c.dataset.streamKey === this.selectedSpotlightStreamKey) || null;
            if (!mainCell) {
                this.selectedSpotlightStreamKey = null;
            }
        }

        videoGrid.classList.toggle('spotlight-mode', !!mainCell);
        if (mainCell) {
            // Keep spotlight rows explicit so grid-row spans can cover all side tiles.
            // With implicit rows, "1 / -1" only spans the first explicit row.
            const sideCount = Math.max(1, cells.length - 1);
            videoGrid.style.gridTemplateRows = `repeat(${sideCount}, minmax(0, 1fr))`;
        } else {
            videoGrid.style.removeProperty('grid-template-rows');
        }

        cells.forEach(cell => {
            const streamKey = cell.dataset.streamKey || '';
            const isMain = !!mainCell && cell === mainCell;
            cell.classList.toggle('spotlight-main', isMain);
            cell.classList.toggle('spotlight-side', !!mainCell && !isMain);
            if (isMain) {
                const sideCount = Math.max(1, cells.length - 1);
                cell.style.gridRow = `1 / span ${sideCount}`;
            } else {
                cell.style.removeProperty('grid-row');
            }

            const btn = cell.querySelector('.video-spotlight-btn');
            if (btn) {
                btn.textContent = isMain ? 'Grid' : 'Center';
                btn.classList.toggle('active', isMain);
                if (!mainCell && streamKey === 'local') {
                    btn.textContent = 'Center';
                    btn.classList.remove('active');
                }
            }
        });
    },

    getDisplayInitial(displayName, fallback = '?') {
        const normalized = (displayName || '').toString().trim();
        if (!normalized) return fallback;
        return normalized[0].toUpperCase();
    },

    localHasVisibleVideoTrack() {
        if (this.currentConstraints?.hideLocalPreview === true) {
            return false;
        }

        const activeStream = this.isScreenSharing && this.screenStream ? this.screenStream : this.localStream;
        if (!activeStream) return false;

        const hasLiveVideo = activeStream.getVideoTracks().some(track =>
            track && track.readyState === 'live' && track.enabled !== false
        );

        if (!hasLiveVideo) return false;
        if (!this.isScreenSharing && this.isCameraOff) return false;
        return true;
    },

    applyLocalPreviewVisibility() {
        const cell = document.querySelector('#videoGrid .local-cell');
        const hidden = this.currentConstraints?.hideLocalPreview === true;
        if (cell) {
            cell.classList.toggle('local-preview-hidden', hidden);
        }
    },

    setLocalPreviewHidden(hidden) {
        const nextHidden = !!hidden;
        if ((this.currentConstraints?.hideLocalPreview === true) === nextHidden) {
            this.updateLocalFeedAvatarFallback();
            return;
        }

        this.currentConstraints.hideLocalPreview = nextHidden;
        this.updateLocalFeedAvatarFallback();
        this.notifyDotNet('OnHideLocalPreviewChanged', nextHidden);
    },

    toggleLocalPreviewHidden() {
        this.setLocalPreviewHidden(!(this.currentConstraints?.hideLocalPreview === true));
    },

    ensureLocalFeedAvatarFallback() {
        const cell = document.querySelector('#videoGrid .local-cell');
        if (!cell) return null;

        let fallback = cell.querySelector('.video-avatar-fallback.local-avatar-fallback');
        if (!fallback) {
            fallback = document.createElement('div');
            fallback.className = 'video-avatar-fallback local-avatar-fallback';

            const avatarImage = document.createElement('img');
            avatarImage.className = 'video-avatar-image';
            avatarImage.alt = 'Local profile picture';
            avatarImage.decoding = 'async';
            avatarImage.loading = 'lazy';

            const avatarInitial = document.createElement('span');
            avatarInitial.className = 'video-avatar-initial';

            fallback.append(avatarImage, avatarInitial);
            cell.appendChild(fallback);
        }

        const avatarImage = fallback.querySelector('.video-avatar-image');
        const avatarInitial = fallback.querySelector('.video-avatar-initial');
        const avatarUrl = this.getParticipantAvatarUrl(this.userId || '');
        const displayName = (this.userDisplayName || this.getParticipantDisplayName(this.userId || '') || 'You').toString();

        if (avatarImage) {
            if (avatarUrl) {
                avatarImage.src = avatarUrl;
                avatarImage.hidden = false;
            } else {
                avatarImage.hidden = true;
                avatarImage.removeAttribute('src');
            }
        }

        if (avatarInitial) {
            avatarInitial.textContent = this.getDisplayInitial(displayName, 'Y');
            avatarInitial.hidden = !!avatarUrl;
        }

        return fallback;
    },

    updateLocalFeedAvatarFallback() {
        const cell = document.querySelector('#videoGrid .local-cell');
        if (!cell) return;

        const fallback = this.ensureLocalFeedAvatarFallback();
        if (!fallback) return;
        const controls = cell.querySelector('.video-controls');
        this.ensureLocalFeedVisibilityControl(controls);

        this.applyLocalPreviewVisibility();
        const hasVideo = this.localHasVisibleVideoTrack();
        fallback.classList.toggle('visible', !hasVideo);
        cell.classList.toggle('video-cell-no-video', !hasVideo);
    },

    ensureRemoteFeedAvatarFallback(cell, feedId, display) {
        if (!cell) return null;

        let fallback = cell.querySelector('.video-avatar-fallback.remote-avatar-fallback');
        if (!fallback) {
            fallback = document.createElement('div');
            fallback.className = 'video-avatar-fallback remote-avatar-fallback';

            const avatarImage = document.createElement('img');
            avatarImage.className = 'video-avatar-image';
            avatarImage.alt = 'Participant profile picture';
            avatarImage.decoding = 'async';
            avatarImage.loading = 'lazy';

            const avatarInitial = document.createElement('span');
            avatarInitial.className = 'video-avatar-initial';

            fallback.append(avatarImage, avatarInitial);
            cell.appendChild(fallback);
        }

        const parsed = this.parsePublisherDisplay((display || '').toString());
        const userId = this.normalizeUserId(parsed.userId || '');
        const displayName = this.getRemoteFeedDisplayName(feedId, display);
        const avatarUrl = userId ? this.getParticipantAvatarUrl(userId) : '';

        const avatarImage = fallback.querySelector('.video-avatar-image');
        const avatarInitial = fallback.querySelector('.video-avatar-initial');

        if (avatarImage) {
            if (avatarUrl) {
                avatarImage.src = avatarUrl;
                avatarImage.hidden = false;
            } else {
                avatarImage.hidden = true;
                avatarImage.removeAttribute('src');
            }
        }

        if (avatarInitial) {
            avatarInitial.textContent = this.getDisplayInitial(displayName, '?');
            avatarInitial.hidden = !!avatarUrl;
        }

        const normalizedFeedId = this.normalizeFeedId(feedId);
        const feed = normalizedFeedId ? this.feeds[normalizedFeedId] : null;
        const hasVideo = this.feedHasLiveTrack(feed, 'video');
        fallback.classList.toggle('visible', !hasVideo);
        cell.classList.toggle('video-cell-no-video', !hasVideo);
        return fallback;
    },

    ensureLocalFeedCellControls() {
        const cell = document.querySelector('#videoGrid .local-cell');
        if (!cell) return;

        // Local stream should stay in the side list by default; only the explicit
        // Center button can promote it to spotlight.
        this.bindSpotlightInteraction(cell, 'local', false);
        this.bindCellOverlayAutoHide(cell);

        let controls = cell.querySelector('.video-controls');
        if (!controls) {
            controls = document.createElement('div');
            controls.className = 'video-controls';
            cell.appendChild(controls);
        }
        this.ensureSpotlightButton(controls, 'local');
        this.ensureLocalFeedVisibilityControl(controls);

        let infoBtn = controls.querySelector('.video-info-btn');
        if (!infoBtn) {
            infoBtn = document.createElement('button');
            infoBtn.type = 'button';
            infoBtn.className = 'video-control-btn video-info-btn';
            infoBtn.textContent = 'Info';
            infoBtn.addEventListener('click', (ev) => {
                ev.stopPropagation();
                this.toggleStreamInfoPanel('local');
            });
            controls.prepend(infoBtn);
        }

        if (!cell.querySelector('.video-info-panel')) {
            const infoPanel = this.createStreamInfoPanel('local');
            cell.appendChild(infoPanel);
        }

        this.updateLocalFeedAvatarFallback();
        this.applySpotlightLayout();
    },

    ensureLocalFeedVisibilityControl(controls) {
        if (!controls) return;

        let button = controls.querySelector('.video-visibility-btn.local-visibility-btn');
        if (!button) {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'video-control-btn video-visibility-btn local-visibility-btn';
            button.addEventListener('click', (ev) => {
                ev.stopPropagation();
                this.toggleLocalPreviewHidden();
            });
            controls.insertBefore(button, controls.firstChild || null);
        }

        const hidden = this.currentConstraints?.hideLocalPreview === true;
        button.textContent = hidden ? 'Show' : 'Hide';
        button.classList.toggle('active', hidden);
    },

    createStreamInfoPanel(streamKey) {
        const panel = document.createElement('div');
        panel.className = 'video-info-panel';
        panel.dataset.streamKey = streamKey;
        panel.innerHTML = [
            '<div class="video-info-row"><span>Bitrate</span><strong data-field="bitrate">n/a</strong></div>',
            '<div class="video-info-row"><span>FPS</span><strong data-field="fps">n/a</strong></div>',
            '<div class="video-info-row"><span>Limiter</span><strong data-field="limiter">n/a</strong></div>',
            '<div class="video-info-row"><span>Resolution</span><strong data-field="resolution">n/a</strong></div>',
            '<div class="video-info-row"><span>Codec</span><strong data-field="codec">n/a</strong></div>',
            '<div class="video-info-row"><span>H264 Profile</span><strong data-field="h264Profile">n/a</strong></div>',
            '<div class="video-info-row"><span>Frames Rx/Dropped</span><strong data-field="framesDropped">n/a</strong></div>',
            '<div class="video-info-row"><span>Encoder</span><strong data-field="encoderImpl">n/a</strong></div>',
            '<div class="video-info-row"><span>Decoder</span><strong data-field="decoderImpl">n/a</strong></div>',
            '<div class="video-info-row"><span>GPU Enc/Dec</span><strong data-field="gpuAccel">n/a</strong></div>',
            '<div class="video-info-row"><span>Transport</span><strong data-field="transport">n/a</strong></div>',
            '<div class="video-info-row"><span>Updated</span><strong data-field="updated">-</strong></div>'
        ].join('');
        return panel;
    },

    getStreamCell(streamKey) {
        if (streamKey === 'local') {
            return document.querySelector('#videoGrid .local-cell');
        }
        if (streamKey.startsWith('remote-')) {
            const feedId = streamKey.substring('remote-'.length);
            return document.getElementById('remote-' + feedId);
        }
        return null;
    },

    toggleStreamInfoPanel(streamKey) {
        const cell = this.getStreamCell(streamKey);
        if (!cell) return;
        const panel = cell.querySelector('.video-info-panel');
        if (!panel) return;

        const isOpen = panel.classList.toggle('open');
        const btn = cell.querySelector('.video-info-btn');
        if (btn) btn.classList.toggle('active', isOpen);

        if (isOpen) {
            this.openStreamInfoPanels[streamKey] = true;
            this.updateStreamInfoPanel(streamKey).catch(() => { });
            this.ensureStreamInfoLoop();
        } else {
            delete this.openStreamInfoPanels[streamKey];
            this.stopStreamInfoLoopIfIdle();
        }
    },

    ensureStreamInfoLoop() {
        if (this.streamInfoIntervalId) return;
        this.streamInfoIntervalId = setInterval(() => {
            this.refreshOpenStreamInfoPanels().catch(() => { });
        }, 1000);
    },

    stopStreamInfoLoopIfIdle() {
        if (Object.keys(this.openStreamInfoPanels).length > 0) return;
        if (!this.streamInfoIntervalId) return;
        clearInterval(this.streamInfoIntervalId);
        this.streamInfoIntervalId = null;
    },

    resetStreamInfoState() {
        if (this.streamInfoIntervalId) {
            clearInterval(this.streamInfoIntervalId);
            this.streamInfoIntervalId = null;
        }
        this.openStreamInfoPanels = {};
        this.streamStatsCache = {};
    },

    clearStreamStatsCache(streamKey) {
        const prefix = `${streamKey}:`;
        for (const key of Object.keys(this.streamStatsCache)) {
            if (key.startsWith(prefix)) {
                delete this.streamStatsCache[key];
            }
        }
    },

    async refreshOpenStreamInfoPanels() {
        const nowMs = Date.now();
        const minRefreshIntervalMs = this.isAppVisible() ? 1500 : 5000;
        if ((nowMs - (this.lastStreamInfoRefreshAtMs || 0)) < minRefreshIntervalMs) {
            return;
        }
        this.lastStreamInfoRefreshAtMs = nowMs;

        const keys = Object.keys(this.openStreamInfoPanels);
        for (const streamKey of keys) {
            const cell = this.getStreamCell(streamKey);
            if (!cell) {
                delete this.openStreamInfoPanels[streamKey];
                this.clearStreamStatsCache(streamKey);
                continue;
            }
            await this.updateStreamInfoPanel(streamKey);
        }
        this.stopStreamInfoLoopIfIdle();
    },

    async updateStreamInfoPanel(streamKey) {
        const cell = this.getStreamCell(streamKey);
        if (!cell) return;

        const panel = cell.querySelector('.video-info-panel');
        if (!panel) return;

        const stats = await this.getStreamInfoSnapshot(streamKey);
        const bitrateEl = panel.querySelector('[data-field="bitrate"]');
        const fpsEl = panel.querySelector('[data-field="fps"]');
        const limiterEl = panel.querySelector('[data-field="limiter"]');
        const resolutionEl = panel.querySelector('[data-field="resolution"]');
        const codecEl = panel.querySelector('[data-field="codec"]');
        const h264ProfileEl = panel.querySelector('[data-field="h264Profile"]');
        const framesDroppedEl = panel.querySelector('[data-field="framesDropped"]');
        const encoderImplEl = panel.querySelector('[data-field="encoderImpl"]');
        const decoderImplEl = panel.querySelector('[data-field="decoderImpl"]');
        const gpuAccelEl = panel.querySelector('[data-field="gpuAccel"]');
        const transportEl = panel.querySelector('[data-field="transport"]');
        const updatedEl = panel.querySelector('[data-field="updated"]');

        if (bitrateEl) {
            bitrateEl.textContent = `${this.formatBitrate(stats.videoBitrateBps)} / ${this.formatBitrate(stats.audioBitrateBps)}`;
        }
        if (fpsEl) {
            fpsEl.textContent = this.formatFps(stats.fps);
        }
        if (limiterEl) {
            limiterEl.textContent = stats.qualityLimitationReason || 'n/a';
        }
        if (resolutionEl) {
            resolutionEl.textContent = stats.resolution || 'n/a';
        }
        if (codecEl) {
            codecEl.textContent = stats.codec || 'n/a';
        }
        if (h264ProfileEl) {
            h264ProfileEl.textContent = stats.h264Profile || 'n/a';
        }
        if (framesDroppedEl) {
            framesDroppedEl.textContent = stats.framesReceivedDropped || 'n/a';
        }
        if (encoderImplEl) {
            encoderImplEl.textContent = stats.encoderImplementation || 'n/a';
        }
        if (decoderImplEl) {
            decoderImplEl.textContent = stats.decoderImplementation || 'n/a';
        }
        if (gpuAccelEl) {
            gpuAccelEl.textContent = stats.gpuEncodeDecode || 'n/a';
        }
        if (transportEl) {
            transportEl.textContent = stats.transport || 'n/a';
        }
        if (updatedEl) {
            updatedEl.textContent = stats.updatedAt || '-';
        }
    },

    formatBitrate(bitsPerSecond) {
        if (!Number.isFinite(bitsPerSecond) || bitsPerSecond <= 0) return 'n/a';
        if (bitsPerSecond >= 1_000_000) return `${(bitsPerSecond / 1_000_000).toFixed(2)} Mbps`;
        return `${(bitsPerSecond / 1_000).toFixed(0)} kbps`;
    },

    formatFps(fpsValue) {
        if (!Number.isFinite(fpsValue) || fpsValue <= 0) return 'n/a';
        return `${fpsValue.toFixed(1)} fps`;
    },

    computeBitrateSample(sampleKey, bytes, timestampMs) {
        if (!Number.isFinite(bytes) || !Number.isFinite(timestampMs)) return 0;
        const prev = this.streamStatsCache[sampleKey];
        this.streamStatsCache[sampleKey] = { bytes, timestampMs };

        if (!prev || timestampMs <= prev.timestampMs || bytes < prev.bytes) return 0;

        const deltaBytes = bytes - prev.bytes;
        const deltaMs = timestampMs - prev.timestampMs;
        return (deltaBytes * 8 * 1000) / deltaMs;
    },

    computeRateSample(sampleKey, count, timestampMs) {
        if (!Number.isFinite(count) || !Number.isFinite(timestampMs)) return 0;
        const prev = this.streamStatsCache[sampleKey];
        this.streamStatsCache[sampleKey] = { count, timestampMs };

        if (!prev || timestampMs <= prev.timestampMs || count < prev.count) return 0;

        const deltaCount = count - prev.count;
        const deltaMs = timestampMs - prev.timestampMs;
        if (deltaMs <= 0) return 0;
        return (deltaCount * 1000) / deltaMs;
    },

    extractMediaRtpReport(stats, direction, kind) {
        const targetType = direction === 'outbound' ? 'outbound-rtp' : 'inbound-rtp';
        let chosen = null;
        let chosenBytes = -1;

        stats.forEach(report => {
            if (report.type !== targetType || report.isRemote) return;
            const mediaKind = (report.kind || report.mediaType || '').toString().toLowerCase();
            if (mediaKind !== kind) return;

            const bytes = direction === 'outbound'
                ? Number(report.bytesSent ?? 0)
                : Number(report.bytesReceived ?? 0);

            if (bytes > chosenBytes) {
                chosen = report;
                chosenBytes = bytes;
            }
        });

        return chosen;
    },

    getCodecFromStatsReport(stats, mediaReport) {
        if (!mediaReport) return 'n/a';
        const codecId = mediaReport.codecId;
        if (!codecId) return 'n/a';
        const codecReport = stats.get(codecId);
        if (!codecReport) return 'n/a';

        const mimeType = (codecReport.mimeType || '').toString();
        if (!mimeType.includes('/')) return mimeType ? mimeType.toUpperCase() : 'n/a';
        return mimeType.split('/')[1].toUpperCase();
    },

    getCodecReportFromStats(stats, mediaReport) {
        if (!mediaReport?.codecId) return null;
        return stats.get(mediaReport.codecId) || null;
    },

    isLikelyHardwareCodecImplementation(implementationName) {
        const value = (implementationName || '').toString().trim().toLowerCase();
        if (!value || value === 'unknown' || value === 'n/a') return null;

        const softwareImplementations = [
            'libvpx',
            'libaom',
            'openh264',
            'ffmpeg',
            'fallback',
            'software'
        ];

        return !softwareImplementations.some(sw => value.includes(sw));
    },

    formatHardwareAccelerationFlag(value) {
        if (value === true) return 'HW';
        if (value === false) return 'SW';
        return 'n/a';
    },

    getVideoEncoderInfoFromStats(stats) {
        let encoderName = 'n/a';
        let encoderHardware = null;
        let bestBytes = -1;

        stats.forEach(report => {
            if (report.type !== 'outbound-rtp' || report.isRemote) return;
            const mediaKind = (report.kind || report.mediaType || '').toString().toLowerCase();
            if (mediaKind !== 'video') return;

            const impl = (report.encoderImplementation || '').toString().trim();
            if (!impl) return;

            const bytes = Number(report.bytesSent ?? 0);
            if (bytes >= bestBytes) {
                bestBytes = bytes;
                encoderName = impl;
                encoderHardware = this.isLikelyHardwareCodecImplementation(impl);
            }
        });

        return { encoder: encoderName, hardware: encoderHardware };
    },

    getVideoDecoderInfoFromStats(stats) {
        let decoderName = 'n/a';
        let decoderHardware = null;
        let bestBytes = -1;

        stats.forEach(report => {
            if (report.type !== 'inbound-rtp' || report.isRemote) return;
            const mediaKind = (report.kind || report.mediaType || '').toString().toLowerCase();
            if (mediaKind !== 'video') return;

            const impl = (report.decoderImplementation || '').toString().trim();
            if (!impl) return;

            const bytes = Number(report.bytesReceived ?? 0);
            if (bytes >= bestBytes) {
                bestBytes = bytes;
                decoderName = impl;
                decoderHardware = this.isLikelyHardwareCodecImplementation(impl);
            }
        });

        return { decoder: decoderName, hardware: decoderHardware };
    },

    async checkVideoEncoder(peerConnection) {
        if (!peerConnection || typeof peerConnection.getStats !== 'function') {
            return { encoder: 'n/a', hardware: null };
        }

        const stats = await peerConnection.getStats();
        const encoderInfo = this.getVideoEncoderInfoFromStats(stats);
        this.log('stats', `[WebRTC Stats] Active Video Encoder: ${encoderInfo.encoder}`);
        this.log('stats', `[WebRTC Stats] Hardware Accelerated (Encoder): ${encoderInfo.hardware}`);
        return encoderInfo;
    },

    async checkVideoDecoder(peerConnection) {
        if (!peerConnection || typeof peerConnection.getStats !== 'function') {
            return { decoder: 'n/a', hardware: null };
        }

        const stats = await peerConnection.getStats();
        const decoderInfo = this.getVideoDecoderInfoFromStats(stats);
        this.log('stats', `[WebRTC Stats] Active Video Decoder: ${decoderInfo.decoder}`);
        this.log('stats', `[WebRTC Stats] Hardware Accelerated (Decoder): ${decoderInfo.hardware}`);
        return decoderInfo;
    },

    getH264ProfileFromStatsReport(stats, mediaReport) {
        const codecReport = this.getCodecReportFromStats(stats, mediaReport);
        if (!codecReport) return 'n/a';

        const mimeType = (codecReport.mimeType || '').toString().toLowerCase();
        if (mimeType !== 'video/h264') return 'n/a';

        const fmtp = (codecReport.sdpFmtpLine || '').toString();
        const profileLevelMatch = /(?:^|;)\s*profile-level-id=([0-9a-fA-F]{6})\s*(?:;|$)/i.exec(fmtp);
        const profileLevelId = (profileLevelMatch?.[1] || '').toLowerCase();
        if (!profileLevelId) return 'H264';

        const profileIdc = parseInt(profileLevelId.slice(0, 2), 16);
        const profileIop = parseInt(profileLevelId.slice(2, 4), 16);
        const profileName = (() => {
            switch (profileIdc) {
                case 0x42:
                    return (profileIop & 0x40) !== 0 ? 'Constrained Baseline' : 'Baseline';
                case 0x4d:
                    return 'Main';
                case 0x58:
                    return 'Extended';
                case 0x64:
                    return 'High';
                case 0x6e:
                    return 'High 10';
                case 0x7a:
                    return 'High 4:2:2';
                case 0xf4:
                    return 'High 4:4:4';
                default:
                    return `Profile ${profileIdc}`;
            }
        })();

        return `${profileName} (${profileLevelId})`;
    },

    getResolutionForStream(streamKey, videoReport) {
        if (videoReport && Number(videoReport.frameWidth) > 0 && Number(videoReport.frameHeight) > 0) {
            return `${videoReport.frameWidth}x${videoReport.frameHeight}`;
        }

        const cell = this.getStreamCell(streamKey);
        const videoEl = cell?.querySelector('video');
        if (videoEl && videoEl.videoWidth > 0 && videoEl.videoHeight > 0) {
            return `${videoEl.videoWidth}x${videoEl.videoHeight}`;
        }

        if (streamKey === 'local' && this.localStream) {
            const videoTrack = this.localStream.getVideoTracks()[0];
            const settings = videoTrack?.getSettings?.() || {};
            if (Number(settings.width) > 0 && Number(settings.height) > 0) {
                return `${settings.width}x${settings.height}`;
            }
        }

        return 'n/a';
    },

    async getStreamInfoSnapshot(streamKey) {
        const snapshot = {
            videoBitrateBps: 0,
            audioBitrateBps: 0,
            resolution: 'n/a',
            codec: 'n/a',
            h264Profile: 'n/a',
            framesReceivedDropped: 'n/a',
            fps: 0,
            qualityLimitationReason: 'n/a',
            encoderImplementation: 'n/a',
            decoderImplementation: 'n/a',
            gpuEncodeDecode: 'n/a',
            transport: 'n/a',
            updatedAt: new Date().toLocaleTimeString()
        };

        let pc = null;
        let direction = 'inbound';

        if (streamKey === 'local') {
            pc = this.publisherPc;
            direction = 'outbound';
        } else if (streamKey.startsWith('remote-')) {
            const feedId = streamKey.substring('remote-'.length);
            pc = this.feeds[feedId]?.pc || null;
            direction = 'inbound';
        }

        if (!pc || typeof pc.getStats !== 'function') {
            return snapshot;
        }

        try {
            const stats = await pc.getStats();
            const encoderInfo = this.getVideoEncoderInfoFromStats(stats);
            const decoderInfo = this.getVideoDecoderInfoFromStats(stats);
            snapshot.encoderImplementation = encoderInfo.encoder || 'n/a';
            snapshot.decoderImplementation = decoderInfo.decoder || 'n/a';
            snapshot.gpuEncodeDecode = `${this.formatHardwareAccelerationFlag(encoderInfo.hardware)} / ${this.formatHardwareAccelerationFlag(decoderInfo.hardware)}`;

            const videoReport = this.extractMediaRtpReport(stats, direction, 'video');
            const audioReport = this.extractMediaRtpReport(stats, direction, 'audio');

            if (videoReport) {
                const bytes = direction === 'outbound'
                    ? Number(videoReport.bytesSent ?? 0)
                    : Number(videoReport.bytesReceived ?? 0);
                const timestamp = Number(videoReport.timestamp ?? 0);
                snapshot.videoBitrateBps = this.computeBitrateSample(
                    `${streamKey}:video:${videoReport.id}`,
                    bytes,
                    timestamp
                );
                snapshot.codec = this.getCodecFromStatsReport(stats, videoReport);
                snapshot.h264Profile = this.getH264ProfileFromStatsReport(stats, videoReport);
                snapshot.qualityLimitationReason = (videoReport.qualityLimitationReason || 'none').toString().toLowerCase();

                const reportFps = Number(videoReport.framesPerSecond ?? 0);
                if (Number.isFinite(reportFps) && reportFps > 0) {
                    snapshot.fps = reportFps;
                } else {
                    const frameCounter = direction === 'outbound'
                        ? Number(videoReport.framesSent ?? videoReport.framesEncoded ?? videoReport.framesCaptured ?? 0)
                        : Number(videoReport.framesDecoded ?? videoReport.framesReceived ?? 0);
                    snapshot.fps = this.computeRateSample(
                        `${streamKey}:fps:${videoReport.id}`,
                        frameCounter,
                        timestamp
                    );
                }

                if (direction === 'inbound') {
                    const framesReceivedRaw = Number(videoReport.framesReceived);
                    const framesDroppedRaw = Number(videoReport.framesDropped ?? videoReport.framesDiscarded ?? 0);
                    const hasFramesReceived = Number.isFinite(framesReceivedRaw);
                    const hasFramesDropped = Number.isFinite(framesDroppedRaw);
                    if (hasFramesReceived || hasFramesDropped) {
                        const framesReceived = hasFramesReceived ? Math.max(0, Math.trunc(framesReceivedRaw)) : 'n/a';
                        const framesDropped = hasFramesDropped ? Math.max(0, Math.trunc(framesDroppedRaw)) : 0;
                        snapshot.framesReceivedDropped = `${framesReceived} / ${framesDropped}`;
                    }
                }
            }

            if (audioReport) {
                const bytes = direction === 'outbound'
                    ? Number(audioReport.bytesSent ?? 0)
                    : Number(audioReport.bytesReceived ?? 0);
                snapshot.audioBitrateBps = this.computeBitrateSample(
                    `${streamKey}:audio:${audioReport.id}`,
                    bytes,
                    Number(audioReport.timestamp ?? 0)
                );
            }

            snapshot.resolution = this.getResolutionForStream(streamKey, videoReport);
            snapshot.transport = this.getTransportInfoFromStats(stats);
        } catch (err) {
            this.log('stats', `getStats failed for ${streamKey}: ${err.message}`);
        }

        return snapshot;
    },

    getTransportInfoFromStats(stats) {
        // Find the active (nominated/succeeded) candidate pair
        let activePair = null;
        stats.forEach(report => {
            if (report.type === 'candidate-pair' && (report.nominated || report.state === 'succeeded')) {
                activePair = report;
            }
        });
        if (!activePair) return 'n/a';

        const localCandidate = stats.get(activePair.localCandidateId);
        const remoteCandidate = stats.get(activePair.remoteCandidateId);
        const localType = localCandidate?.candidateType || '?';
        const remoteType = remoteCandidate?.candidateType || '?';
        const protocol = (localCandidate?.protocol || activePair.transportId || '').toUpperCase();

        const isTurn = localType === 'relay' || remoteType === 'relay';
        const label = isTurn ? 'TURN (relay)' : localType === 'host' && remoteType === 'host' ? 'Direct (host)' : `${localType}/${remoteType}`;
        return protocol ? `${label} [${protocol}]` : label;
    },

    getCodecFromSdp(sdp, kind) {
        if (!sdp || !kind) return 'n/a';
        const lines = sdp.split(/\r?\n/);
        let inSection = false;
        let payloads = [];
        const rtpMap = new Map();

        for (const line of lines) {
            if (!line) continue;
            if (line.startsWith('m=')) {
                inSection = line.startsWith(`m=${kind} `);
                payloads = inSection ? line.split(' ').slice(3).filter(Boolean) : [];
                continue;
            }
            if (!inSection) continue;

            if (line.startsWith('a=rtpmap:')) {
                const match = /^a=rtpmap:(\d+)\s+([^\/\s]+)/i.exec(line);
                if (match) {
                    rtpMap.set(match[1], match[2].toUpperCase());
                }
            }
        }

        for (const payload of payloads) {
            const codec = rtpMap.get(payload);
            if (!codec) continue;
            if (codec === 'RTX' || codec === 'RED' || codec === 'ULPFEC' || codec === 'FLEXFEC-03') {
                continue;
            }
            return codec;
        }

        return payloads.length > 0 ? 'unknown' : 'none';
    },

    logCodecSummary(pc, scope) {
        if (!pc) return;
        const localSdp = pc.localDescription?.sdp || '';
        const remoteSdp = pc.remoteDescription?.sdp || '';
        const localAudio = this.getCodecFromSdp(localSdp, 'audio');
        const localVideo = this.getCodecFromSdp(localSdp, 'video');
        const remoteAudio = this.getCodecFromSdp(remoteSdp, 'audio');
        const remoteVideo = this.getCodecFromSdp(remoteSdp, 'video');
        this.log('codec', `${scope}: local[audio=${localAudio},video=${localVideo}] remote[audio=${remoteAudio},video=${remoteVideo}]`);
    },

    applyH264ProfileLevelIdToSdp(sdp, profileLevelId) {
        if (!sdp || !profileLevelId) return sdp;

        const normalizedProfile = profileLevelId.toString().trim().toLowerCase();
        if (!/^[0-9a-f]{6}$/.test(normalizedProfile)) {
            return sdp;
        }

        const lines = sdp.split(/\r?\n/);
        const h264Payloads = new Set();

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const rtpMapMatch = /^a=rtpmap:(\d+)\s+H264\/\d+/i.exec(line);
            if (!rtpMapMatch) continue;

            const payload = rtpMapMatch[1];
            h264Payloads.add(payload);
        }

        if (h264Payloads.size === 0) return sdp;

        const payloadsWithFmtp = new Set();
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const fmtpMatch = /^a=fmtp:(\d+)\s+(.+)$/i.exec(line);
            if (!fmtpMatch) continue;

            const payload = fmtpMatch[1];
            if (!h264Payloads.has(payload)) continue;

            payloadsWithFmtp.add(payload);
            let params = (fmtpMatch[2] || '').trim();
            if (!params) {
                lines[i] = `a=fmtp:${payload} profile-level-id=${normalizedProfile}`;
                continue;
            }

            if (/(?:^|;)\s*profile-level-id=/i.test(params)) {
                params = params.replace(
                    /((?:^|;)\s*profile-level-id=)[0-9a-fA-F]{6}/i,
                    `$1${normalizedProfile}`);
            } else {
                params = `${params};profile-level-id=${normalizedProfile}`;
            }

            lines[i] = `a=fmtp:${payload} ${params}`;
        }

        // Do not inject new fmtp lines for payloads that never advertised fmtp.
        // Injecting parameters blindly can invalidate recv capabilities on some platforms.
        if (payloadsWithFmtp.size === 0) {
            return sdp;
        }

        return lines.join('\r\n');
    },

    isH264ProfileLevelIdSupported(profileLevelId) {
        const desired = (profileLevelId || '').toString().trim().toLowerCase();
        if (!/^[0-9a-f]{6}$/.test(desired)) return false;

        const hasProfile = (codecs) => {
            if (!Array.isArray(codecs)) return false;
            return codecs.some(codec => {
                const mimeType = (codec?.mimeType || '').toString().toLowerCase();
                if (mimeType !== 'video/h264') return false;
                const fmtp = (codec?.sdpFmtpLine || '').toString().toLowerCase();
                const match = /(?:^|;)\s*profile-level-id=([0-9a-f]{6})\s*(?:;|$)/i.exec(fmtp);
                return !!match && match[1].toLowerCase() === desired;
            });
        };

        try {
            const senderCodecs = window.RTCRtpSender?.getCapabilities?.('video')?.codecs || [];
            const receiverCodecs = window.RTCRtpReceiver?.getCapabilities?.('video')?.codecs || [];
            return hasProfile(senderCodecs) && hasProfile(receiverCodecs);
        } catch {
            return false;
        }
    },

    getPreferredH264ProfileLevelIdCandidates() {
        const rawCandidates = [
            this.h264ProfileLevelId,
            ...(Array.isArray(this.h264ProfileLevelIdFallbacks) ? this.h264ProfileLevelIdFallbacks : [])
        ];
        const seen = new Set();
        const candidates = [];
        for (const raw of rawCandidates) {
            const normalized = (raw || '').toString().trim().toLowerCase();
            if (!/^[0-9a-f]{6}$/.test(normalized)) continue;
            if (seen.has(normalized)) continue;
            seen.add(normalized);
            candidates.push(normalized);
        }
        return candidates;
    },

    buildPreferredH264ProfileOffers(offer, scope) {
        // H264 profile-level-id overrides are disabled to avoid breaking
        // hardware encoder negotiation on some Chromium/WebView2 builds.
        return [];
    },

    async setLocalDescriptionWithProfileFallback(pc, rawOffer, patchedOffers, scope) {
        const attempts = [];
        if (Array.isArray(patchedOffers)) {
            attempts.push(...patchedOffers);
        } else if (patchedOffers && patchedOffers.sdp) {
            attempts.push(patchedOffers);
        }
        attempts.push(rawOffer);

        let lastError = null;
        for (const offerToTry of attempts) {
            try {
                await pc.setLocalDescription(offerToTry);
                return offerToTry;
            } catch (err) {
                lastError = err;
                const attemptedProfile = offerToTry?.__forcedH264ProfileLevelId || '';
                if (!attemptedProfile) {
                    throw err;
                }
                this.log(
                    'codec',
                    `${scope}: forced H264 profile-level-id=${attemptedProfile} rejected by setLocalDescription (${err.message}), trying next fallback`);
            }
        }

        throw lastError || new Error(`${scope}: Failed to set local description`);
    },

    getH264PreferenceWeightForCodec(codec, effectivePreference) {
        if (!codec) {
            return 0;
        }

        const fmtp = (codec?.sdpFmtpLine || '').toString();
        const details = this.parseH264CodecDescriptor(fmtp);
        if (!details.profileLevelId) {
            return 0;
        }

        let bonus = 0;
        if (effectivePreference?.profileLevelId && details.profileLevelId === effectivePreference.profileLevelId) {
            bonus += 10_000;
            if (effectivePreference.packetizationMode !== null
                && details.packetizationMode === effectivePreference.packetizationMode) {
                bonus += 2_000;
            }
        }

        return bonus + this.getH264CodecPreferenceScore(details.profileLevelId, details.packetizationMode);
    },

    orderH264CodecsByPreference(codecs, effectivePreference) {
        if (!Array.isArray(codecs) || codecs.length <= 1) {
            return Array.isArray(codecs) ? [...codecs] : [];
        }

        return [...codecs].sort((a, b) =>
            this.getH264PreferenceWeightForCodec(b, effectivePreference)
            - this.getH264PreferenceWeightForCodec(a, effectivePreference));
    },

    findPublisherVideoTransceiver() {
        if (!this.publisherPc) return null;
        return this.publisherPc.getTransceivers().find(t =>
            t.sender &&
            (t.sender.track?.kind === 'video' || t.receiver?.track?.kind === 'video' || t.mid === '1')
        ) || null;
    },

    applyPublisherCodecPreference(scope) {
        if (!this.publisherPc) return false;

        const preferred = this.normalizeVideoCodec(this.currentConstraints.preferredVideoCodec);

        try {
            const transceiver = this.findPublisherVideoTransceiver();

            if (!transceiver) {
                this.log('codec', `${scope}: no video transceiver found to apply codec preference (${preferred})`);
                return false;
            }

            if (typeof transceiver.setCodecPreferences !== 'function' || !window.RTCRtpSender?.getCapabilities) {
                this.log('codec', `${scope}: setCodecPreferences not supported, cannot force ${preferred}`);
                return false;
            }

            const caps = window.RTCRtpSender.getCapabilities('video');
            const codecs = caps?.codecs || [];
            if (codecs.length === 0) {
                this.log('codec', `${scope}: browser returned no video codec capabilities`);
                return false;
            }

            const mime = (c) => (c?.mimeType || '').toLowerCase();
            const isAuxCodec = (c) => {
                const m = mime(c);
                return m === 'video/rtx' || m === 'video/red' || m === 'video/ulpfec' || m === 'video/flexfec-03';
            };
            const isMime = (c, values) => values.has(mime(c));
            const getPayloadType = (c) => {
                const pt = c?.payloadType ?? c?.preferredPayloadType;
                return (typeof pt === 'number' || typeof pt === 'string') ? String(pt) : '';
            };
            const parseApt = (fmtp) => {
                if (!fmtp) return '';
                const match = /(?:^|;)\s*apt=(\d+)\s*(?:;|$)/i.exec(fmtp);
                return match ? match[1] : '';
            };

            const preferredMimes = preferred === 'h265'
                ? new Set(['video/h265', 'video/hevc'])
                : new Set([`video/${preferred}`]);
            const h264Mimes = new Set(['video/h264']);
            const vp8Mimes = new Set(['video/vp8']);
            const primaryCodecs = codecs.filter(c => !isAuxCodec(c));

            let preferredCodecs = primaryCodecs.filter(c => isMime(c, preferredMimes));
            let fallbackCodecs = [];
            const effectiveH264Preference = this.resolveEffectiveH264Override();

            // H265 preferred: fallback to H264 only.
            if (preferred === 'h265') {
                fallbackCodecs = primaryCodecs.filter(c => isMime(c, h264Mimes) && !preferredCodecs.includes(c));
                fallbackCodecs = this.orderH264CodecsByPreference(fallbackCodecs, effectiveH264Preference);
            }

            // H264 preferred: fallback to VP8 only.
            if (preferred === 'h264') {
                preferredCodecs = this.orderH264CodecsByPreference(preferredCodecs, effectiveH264Preference);
                fallbackCodecs = primaryCodecs.filter(c => isMime(c, vp8Mimes) && !preferredCodecs.includes(c));
                if (effectiveH264Preference.profileLevelId) {
                    const mode = effectiveH264Preference.packetizationMode ?? 0;
                    this.log(
                        'codec',
                        `${scope}: prioritizing H264 profile ${effectiveH264Preference.profileLevelId} pm=${mode}${this.currentConstraints?.h264ProfileOverride ? ' (explicit)' : ' (auto default)'}`
                    );
                }
            }

            if (preferredCodecs.length === 0) {
                if (preferred === 'h265' && fallbackCodecs.length > 0) {
                    preferredCodecs = fallbackCodecs;
                    fallbackCodecs = [];
                    this.log('codec', `${scope}: H265 unavailable, falling back to H264`);
                } else if (preferred === 'h264' && fallbackCodecs.length > 0) {
                    preferredCodecs = fallbackCodecs;
                    fallbackCodecs = [];
                    this.log('codec', `${scope}: H264 unavailable, falling back to VP8`);
                } else {
                    const available = [...new Set(codecs.map(c => c.mimeType).filter(Boolean))].join(', ');
                    this.log('codec', `${scope}: preferred codec ${preferred.toUpperCase()} not available. Browser supports: ${available}`);
                    return false;
                }
            }

            let reordered;
            if (preferred === 'h265' || preferred === 'h264') {
                const selectedPrimary = [...preferredCodecs, ...fallbackCodecs];
                const remainingPrimary = primaryCodecs.filter(c => !selectedPrimary.includes(c));
                const orderedPrimary = [...selectedPrimary, ...remainingPrimary];
                const selectedPts = new Set(orderedPrimary.map(getPayloadType).filter(Boolean));
                const auxCodecs = codecs.filter(c => {
                    if (!isAuxCodec(c)) return false;
                    const m = mime(c);
                    if (m === 'video/red' || m === 'video/ulpfec' || m === 'video/flexfec-03') return true;
                    const apt = parseApt(c.sdpFmtpLine || '');
                    return apt ? selectedPts.has(apt) : false;
                });
                reordered = [...orderedPrimary, ...auxCodecs];
                if (preferred === 'h265') {
                    this.log('codec', `${scope}: codec order prioritized to H265 (H264 fallback retained, other codecs kept for recv compatibility)`);
                } else {
                    this.log('codec', `${scope}: codec order prioritized to H264 (VP8 fallback retained, other codecs kept for recv compatibility)`);
                }
            } else {
                const available = [...new Set(codecs.map(c => c.mimeType).filter(Boolean))].join(', ');
                reordered = [
                    ...preferredCodecs,
                    ...codecs.filter(c => !preferredCodecs.includes(c))
                ];
                this.log('codec', `${scope}: preferred video codec set to ${preferred.toUpperCase()} (available: ${available})`);
            }

            transceiver.setCodecPreferences(reordered);
            return true;
        } catch (err) {
            this.log('codec', `${scope}: failed to apply codec preference (${preferred}): ${err.message}`);
            return false;
        }
    },

    resetPublisherCodecPreference(scope) {
        if (!this.publisherPc) return false;
        try {
            const transceiver = this.findPublisherVideoTransceiver();
            if (!transceiver) return false;
            if (typeof transceiver.setCodecPreferences !== 'function' || !window.RTCRtpSender?.getCapabilities) {
                return false;
            }

            const codecs = window.RTCRtpSender.getCapabilities('video')?.codecs || [];
            if (!Array.isArray(codecs) || codecs.length === 0) return false;

            transceiver.setCodecPreferences(codecs);
            this.log('codec', `${scope}: codec order reset to browser default`);
            return true;
        } catch (err) {
            this.log('codec', `${scope}: failed to reset codec order (${err.message})`);
            return false;
        }
    },

    async createPublisherOfferWithCodecRecovery(pc, scope) {
        this.applyPublisherCodecPreference(scope);
        try {
            const rawOffer = await pc.createOffer();
            const patchedOffers = this.buildPreferredH264ProfileOffers(rawOffer, `${scope}-offer`);
            const rawOfferWithBitrate = this.buildPublisherOfferWithBitrateParameters(rawOffer);
            const patchedOffersWithBitrate = (patchedOffers || []).map(o => this.buildPublisherOfferWithBitrateParameters(o));
            return await this.setLocalDescriptionWithProfileFallback(pc, rawOfferWithBitrate, patchedOffersWithBitrate, `${scope}-offer`);
        } catch (err) {
            this.log('codec', `${scope}: offer failed with preferred codec order (${err.message})`);
            const resetApplied = this.resetPublisherCodecPreference(`${scope}-fallback`);
            if (!resetApplied) {
                throw err;
            }

            this.log('codec', `${scope}: retrying offer with browser default codec order`);
            const rawOffer = await pc.createOffer();
            const patchedOffers = this.buildPreferredH264ProfileOffers(rawOffer, `${scope}-offer-fallback`);
            const rawOfferWithBitrate = this.buildPublisherOfferWithBitrateParameters(rawOffer);
            const patchedOffersWithBitrate = (patchedOffers || []).map(o => this.buildPublisherOfferWithBitrateParameters(o));
            return await this.setLocalDescriptionWithProfileFallback(pc, rawOfferWithBitrate, patchedOffersWithBitrate, `${scope}-offer-fallback`);
        }
    },

    async updateParticipantNames(participantDirectory) {
        this.applyParticipantProfiles(participantDirectory || {}, this.participantAvatarDirectory || {});
    },

    async updateParticipantProfiles(participantDirectory, participantAvatarDirectory) {
        this.applyParticipantProfiles(
            participantDirectory || {},
            participantAvatarDirectory || {}
        );
    },

    async apiPost(path, body) {
        const resp = await fetch(this.serverBaseUrl + path, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': 'Bearer ' + this.authToken
            },
            body: JSON.stringify(body)
        });
        if (!resp.ok) {
            const text = await resp.text();
            throw new Error(`POST ${path} → ${resp.status}: ${text}`);
        }
        // Some endpoints (keepalive, trickle) return 200 with empty body
        const text = await resp.text();
        if (!text) return null;
        return JSON.parse(text);
    },

    async apiGet(path, signal) {
        const opts = {
            headers: { 'Authorization': 'Bearer ' + this.authToken }
        };
        if (signal) opts.signal = signal;
        const resp = await fetch(this.serverBaseUrl + path, opts);
        if (!resp.ok) throw new Error(`GET ${path} → ${resp.status}`);
        const text = await resp.text();
        if (!text) return null;
        return JSON.parse(text);
    },

    notifyDotNet(method, ...args) {
        if (this.dotNetRef) {
            this.dotNetRef.invokeMethodAsync(method, ...args).catch(() => { });
        }
    },

    notifyHostPttConfig() {
        try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
                window.chrome.webview.postMessage({
                    type: 'ptt-config',
                    enabled: !!this.pttEnabled,
                    key: this.pttKey || 'Space'
                });
            }
        } catch { }
    },

    notifyHostScreenAudioFallback(action, details) {
        try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
                window.chrome.webview.postMessage({
                    type: 'screen-audio-fallback',
                    action,
                    ...(details || {})
                });
            }
        } catch { }
    },

    notifyHostDesktopFullscreen(action) {
        try {
            const host = window.chrome?.webview;
            if (!host || typeof host.postMessage !== 'function') {
                return false;
            }

            host.postMessage({
                    type: 'desktop-fullscreen',
                    action: action || 'toggle'
            });
            return true;
        } catch { }
        return false;
    },

    installHostMessageListener() {
        if (!window.chrome || !window.chrome.webview || !window.chrome.webview.addEventListener) {
            return;
        }
        if (this._hostMessageHandler) {
            return;
        }

        this._hostMessageHandler = (event) => {
            this.handleHostMessage(event?.data);
        };
        window.chrome.webview.addEventListener('message', this._hostMessageHandler);
    },

    removeHostMessageListener() {
        if (!this._hostMessageHandler) {
            return;
        }
        try {
            window.chrome?.webview?.removeEventListener?.('message', this._hostMessageHandler);
        } catch { }
        this._hostMessageHandler = null;
    },

    ensureFullscreenSyncListener() {
        if (this._fullscreenChangeHandler) {
            return;
        }

        this._fullscreenChangeHandler = () => {
            if (!document.fullscreenElement && this._desktopFullscreenRequested) {
                this.notifyHostDesktopFullscreen('exit');
                this._desktopFullscreenRequested = false;
            }

            // Entering fullscreen on a video cell: the click that launched it left
            // focus on the fullscreen button, which would pin the controls overlay
            // open. Drop that residual focus and start the auto-hide cycle.
            const fullscreenCell = document.fullscreenElement;
            if (fullscreenCell && fullscreenCell.classList?.contains('video-cell')) {
                const active = document.activeElement;
                if (active && fullscreenCell.contains(active)) {
                    let keyboardFocus = false;
                    try { keyboardFocus = active.matches(':focus-visible'); } catch { }
                    if (!keyboardFocus) {
                        try { active.blur(); } catch { }
                    }
                }
                // Element fullscreen renders only the cell's subtree, so anything
                // outside it - including the annotation toolbar - is invisible.
                // Physically move the toolbar host into the cell for the duration.
                const toolbarHost = document.getElementById('annotationToolbarHost');
                if (toolbarHost && !fullscreenCell.contains(toolbarHost)) {
                    fullscreenCell.appendChild(toolbarHost);
                }
                this.showCellOverlayControls(fullscreenCell, true);
            } else {
                this.restoreAnnotationToolbarHost();
            }
        };
        document.addEventListener('fullscreenchange', this._fullscreenChangeHandler);
    },

    /// Returns the annotation toolbar host to its Blazor anchor after it was
    /// reparented into a fullscreen cell. Must run before remote cells are torn
    /// down, or the host would be deleted along with the cell it sits in.
    restoreAnnotationToolbarHost() {
        const toolbarHost = document.getElementById('annotationToolbarHost');
        const anchor = document.getElementById('annotationToolbarAnchor');
        if (!toolbarHost || !anchor || toolbarHost.previousElementSibling === anchor) {
            return;
        }
        try { anchor.insertAdjacentElement('afterend', toolbarHost); } catch { }
    },

    removeFullscreenSyncListener() {
        if (!this._fullscreenChangeHandler) {
            return;
        }
        try {
            document.removeEventListener('fullscreenchange', this._fullscreenChangeHandler);
        } catch { }
        this._fullscreenChangeHandler = null;
    },

    handleHostMessage(data) {
        if (!data) {
            return;
        }

        if (typeof data === 'string') {
            const prefix = '__webrtcHost:';
            if (!data.startsWith(prefix)) {
                return;
            }

            const json = data.substring(prefix.length);
            try {
                data = JSON.parse(json);
            } catch {
                return;
            }
        }

        if (typeof data !== 'object') {
            return;
        }

        if (data.type === 'screen-audio-fallback-state') {
            const state = data.state || '';
            if (state === 'started') {
                const mode = data.mode || 'system';
                const processId = data.processId ? ` pid=${data.processId}` : '';
                const windowTitle = data.windowTitle ? ` window="${data.windowTitle}"` : '';
                this.log('screenshare', `Windows audio fallback capture started (mode=${mode}${processId}${windowTitle})`);
                return;
            }
            if (state === 'stopped') {
                this.log('screenshare', 'Windows audio fallback capture stopped');
                return;
            }
            if (state === 'error') {
                const message = data.message || 'Unknown Windows audio fallback error';
                this.log('screenshare', `Windows audio fallback error: ${message}`);
                this.notifyDotNet('OnError', 'Screen share audio fallback failed: ' + message);
            }
            return;
        }

        if (data.type === 'screen-audio-fallback-chunk') {
            this.ingestWindowsScreenAudioChunk(data);
        }
    },

    sleep(ms) {
        return new Promise(r => setTimeout(r, ms));
    },

    isAppVisible() {
        try {
            return typeof document === 'undefined' || !document.hidden;
        } catch {
            return true;
        }
    },

    log(area, msg, ...extra) {
        console.log(`[WebRTC:${area}]`, msg, ...extra);
    },

    logError(area, msg, ...extra) {
        console.error(`[WebRTC:${area}]`, msg, ...extra);
        this.notifyDotNet('OnError', `${area}: ${msg}`);
    },

    // ─── Trickle ICE batching ───────────────────────────────────────────────────
    //
    // Every gathered candidate used to be its own authenticated POST. On a Windows host with
    // VPN/Hyper-V adapters that is 15-30 requests per PeerConnection during gathering, and a
    // call opens one PC per remote feed. Candidates are buffered per handle and flushed as a
    // single array; end-of-candidates is never delayed, because holding it back stalls ICE.

    _trickleBatches: null,
    trickleBatchWindowMs: 50,

    getTrickleBatch(handleId) {
        const key = String(handleId || '');
        if (!key) return null;

        const batches = this._trickleBatches || (this._trickleBatches = new Map());
        let batch = batches.get(key);
        if (!batch) {
            batch = { key, candidates: [], timerId: 0, chain: Promise.resolve() };
            batches.set(key, batch);
        }
        return batch;
    },

    queueTrickleCandidate(handleId, candidate) {
        const batch = this.getTrickleBatch(handleId);
        if (!batch) return;

        if (candidate) {
            batch.candidates.push(candidate);
            if (!batch.timerId) {
                batch.timerId = setTimeout(() => this.flushTrickleBatch(batch.key), this.trickleBatchWindowMs);
            }
            return;
        }

        // End-of-candidates: drain what is buffered, then send the terminator. Both go on the
        // per-handle chain so neither can overtake a batch that is already in flight.
        this.flushTrickleBatch(batch.key);
        this.enqueueTricklePost(batch, null);
    },

    flushTrickleBatch(handleId) {
        const key = String(handleId || '');
        const batch = this._trickleBatches?.get(key);
        if (!batch) return;

        if (batch.timerId) {
            clearTimeout(batch.timerId);
            batch.timerId = 0;
        }

        if (batch.candidates.length === 0) return;

        // A lone candidate still posts in the original single-object shape.
        const payload = batch.candidates.length === 1 ? batch.candidates[0] : batch.candidates;
        batch.candidates = [];
        this.enqueueTricklePost(batch, payload);
    },

    enqueueTricklePost(batch, payload) {
        batch.chain = batch.chain
            .then(() => this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${batch.key}/trickle`,
                payload))
            .catch(err => this.log('trickle', `ICE trickle failed for handle ${batch.key}: ${err.message}`));
    },

    clearTrickleBatch(handleId) {
        const key = String(handleId || '');
        const batch = this._trickleBatches?.get(key);
        if (!batch) return;
        if (batch.timerId) clearTimeout(batch.timerId);
        this._trickleBatches.delete(key);
    },

    clearAllTrickleBatches() {
        if (!this._trickleBatches) return;
        for (const batch of this._trickleBatches.values()) {
            if (batch.timerId) clearTimeout(batch.timerId);
        }
        this._trickleBatches.clear();
    },

    async sendJanusBitrateConfigure(bitrateBps) {
        if (!this.janusSessionId || !this.janusHandleId || !bitrateBps || bitrateBps <= 0) {
            return;
        }
        const videocodec = this.getActivePublisherJanusVideoCodec();
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message-nowait`,
                { body: { request: 'configure', bitrate: bitrateBps, videocodec } }
            );
            this.log('bitrate', `Sent Janus configure bitrate=${bitrateBps} bps (videocodec=${videocodec})`);
        } catch (err) {
            this.log('bitrate', `Janus configure failed: ${err.message}`);
        }
    },

    /// True once there is a publisher PeerConnection that Janus has answered, i.e. once
    /// a local track can actually reach anyone. IsInCall goes true as soon as the hub
    /// join returns, which is well before this.
    isPublisherReadyForMedia() {
        return !!this.publisherPc && this.publisherConfigured === true;
    },

    notifyPublisherReadyChanged() {
        this.notifyDotNet('OnPublisherReadyChanged', this.isPublisherReadyForMedia());
    },

    async markPublisherConfigured(source) {
        if (this.publisherConfigured) return;
        this.publisherConfigured = true;
        this.log('publish', `Publisher configured (${source})`);
        this.notifyPublisherReadyChanged();
        this.startPublisherHealthWatchdog();

        if (this.pendingJanusBitrateBps > 0) {
            const pending = this.pendingJanusBitrateBps;
            this.pendingJanusBitrateBps = 0;
            await this.sendJanusBitrateConfigure(pending);
        }
    },

    // ─── Init ───────────────────────────────────────────────────────────────────

    async init(config) {
        this.janusSessionId = config.janusSessionId;
        this.janusHandleId = config.janusHandleId;
        this.janusRoomId = config.janusRoomId;
        this.serverBaseUrl = config.serverBaseUrl;
        this.authToken = config.authToken;
        this.userId = config.userId;
        this.userDisplayName = config.userDisplayName || config.userId || '';
        this.applyParticipantProfiles(
            config.participantDirectory || {},
            config.participantAvatarDirectory || {}
        );
        this.dotNetRef = config.dotNetRef;
        this.installHostMessageListener();
        this.ensureFullscreenSyncListener();
        this.loadPersistedRemoteIdentityVolumes();

        if (config.iceServers) {
            try { this.iceServers = JSON.parse(config.iceServers); } catch { }
        }

        this.currentConstraints.micDeviceId = config.micDeviceId || null;
        this.currentConstraints.cameraDeviceId = config.cameraDeviceId || null;
        this.currentConstraints.resolution = config.resolution || this.currentConstraints.resolution || '1280x720';
        this.currentConstraints.fps = parseInt(config.fps, 10) || this.currentConstraints.fps || 30;
        this.currentConstraints.bitrateKbps = this.clampPublisherBitrateKbps(
            config.bitrateKbps ?? this.currentConstraints.bitrateKbps ?? 2000
        );
        this.currentConstraints.preferredVideoCodec = this.normalizeVideoCodec(
            config.preferredVideoCodec || this.currentConstraints.preferredVideoCodec || 'h264'
        );
        this.currentConstraints.h264ProfileOverride = this.normalizeH264ProfileOverrideValue(
            config.h264ProfileOverride
        );
        this.currentConstraints.hideLocalPreview = config.hideLocalPreview === true;
        this.currentConstraints.screenShareAudioEnabled = config.screenShareAudioEnabled !== false;
        this.currentConstraints.screenShareAutoGainEnabled = config.screenShareAutoGainEnabled !== false;
        this.currentConstraints.noiseSuppressionEnabled = config.noiseSuppressionEnabled !== false;
        this.currentConstraints.voiceGateEnabled = config.voiceGateEnabled === true;
        this.currentConstraints.voiceGateAuto = config.voiceGateAuto !== false;
        this.currentConstraints.voiceGateThreshold = parseInt(config.voiceGateThreshold, 10) || 30;

        const effectiveH264 = this.resolveEffectiveH264Override();
        if (this.currentConstraints.preferredVideoCodec === 'h264' && effectiveH264.profileLevelId) {
            const mode = effectiveH264.packetizationMode ?? 0;
            this.log(
                'codec',
                `init: H264 profile preference ${effectiveH264.profileLevelId} pm=${mode}${this.currentConstraints.h264ProfileOverride ? ' (explicit)' : ' (auto default)'}`
            );
        }

        this.isMuted = true;
        this.isCameraOff = true;
        this.isScreenSharing = false;
        this.screenAudioPublisherHandleId = 0;
        this.screenAudioPublisherPc = null;
        this.screenAudioPublisherConfigured = false;
        this.screenAudioPublisherPendingCandidates = [];
        this.pendingSubscribes = {};
        this.pendingSubscribeFeeds = {};
        this.publisherStreamHints = {};
        this.publisherConfigured = false;
        this.pendingJanusBitrateBps = 0;
        this.stopPublisherVideoPerformanceGuard();
        this.stopPublisherHealthWatchdog();
        this.clearScreenShareBitrateWarmupTimers();
        this.controlOverlayHideTimers = new WeakMap();
        this.lastParticipantCheckAtMs = 0;
        this.lastRemoteFeedHealthRunAtMs = 0;
        this.lastStreamInfoRefreshAtMs = 0;
        this.remoteFeedHealthCursor = 0;
        this.resetRemoteSpeakingState();

        this.log('init', `session=${config.janusSessionId}, handle=${config.janusHandleId}, room=${config.janusRoomId}`);
        this.log('init', `serverBaseUrl=${config.serverBaseUrl}`);
        this.enforceAndroidHardwareCodecPreference('init');

        // Connectivity test — verify the server API is reachable
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/keepalive`, {});
            this.log('init', 'Server API reachable (keepalive OK)');
        } catch (err) {
            this.logError('init', `Server API NOT reachable at ${this.serverBaseUrl}: ${err.message}`);
        }

        try {
            await this.startLocalStream();
        } catch (err) {
            this.logError('init', 'Failed to get local media: ' + err.message);
        }

        // Always enumerate devices (even if media failed, labels may be blank)
        await this.enumerateDevices();
        this.ensureLocalFeedCellControls();

        try {
            // Publish uses the sync /message endpoint (polling hasn't started, no competition)
            const existingPublishers = await this.publishOwnFeed();

            // Start polling FIRST — it will handle subscriber JSEP responses
            this.startPolling();
            this.startKeepalive();
            this.startParticipantCheck();
            this.startRemoteFeedHealthWatchdog();

            // Apply initial bitrate to publisher (deferred until configured if needed)
            await this.applyBitrate(this.currentConstraints.bitrateKbps);

            // Now initiate subscribes for any existing publishers (fire-and-forget)
            if (existingPublishers.length > 0) {
                const dedupedPublishers = this.collapsePublisherCandidates(existingPublishers);
                this.log('init', `Subscribing to ${dedupedPublishers.length} existing publisher(s)...`);
                for (const pub of dedupedPublishers) {
                    if (this.isSelfPublisherDisplay(pub?.display)) {
                        this.log('init', `Skip self existing publisher feed ${pub?.id} (${pub?.display || 'unknown'})`);
                        continue;
                    }
                    await this.initiateSubscribe(pub.id, pub.display, pub.streams);
                }
            }
        } catch (err) {
            this.logError('init', 'Failed to publish: ' + err.message);
        }
    },

    // ─── Media ──────────────────────────────────────────────────────────────────

    async startLocalStream(constraints) {
        this.removeLocalSpeakingAnalyzer();
        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
        }
        // setupAudioPipeline pulls the raw capture track out of localStream, so the loop above
        // never reaches it — it has to be released explicitly or the mic stays open.
        this.releaseRawMicTrack();

        // Merge with stored constraints
        if (constraints) {
            Object.assign(this.currentConstraints, constraints);
        }
        const c = this.normalizeCurrentConstraints('startLocalStream');

        // Parse resolution
        let width = 1280, height = 720;
        if (c.resolution) {
            const parts = c.resolution.split('x');
            if (parts.length === 2) {
                width = parseInt(parts[0]) || 1280;
                height = parseInt(parts[1]) || 720;
            }
        }

        const fps = parseInt(c.fps) || 30;

        // Always disable browser's native noiseSuppression — DTLN handles it via AudioWorklet when enabled.
        const noiseSuppression = false;
        const audioConstraint = c.micDeviceId
            ? { deviceId: { exact: c.micDeviceId }, noiseSuppression, echoCancellation: true, autoGainControl: true }
            : { noiseSuppression, echoCancellation: true, autoGainControl: true };

        const videoConstraint = {
            width: { ideal: width },
            height: { ideal: height },
            frameRate: { ideal: fps }
        };
        if (c.cameraDeviceId) {
            videoConstraint.deviceId = { exact: c.cameraDeviceId };
        }

        this.log('media', `getUserMedia: audio=${JSON.stringify(audioConstraint)}, video=${width}x${height}@${fps}fps`);

        const defaultVideoConstraint = {
            width: { ideal: width },
            height: { ideal: height },
            frameRate: { ideal: fps }
        };

        const attempts = [
            {
                label: 'audio+video',
                constraints: { audio: audioConstraint, video: videoConstraint }
            }
        ];

        const defaultAudioConstraint = { noiseSuppression, echoCancellation: true, autoGainControl: true };

        if (c.micDeviceId || c.cameraDeviceId) {
            attempts.push({
                label: 'audio+video (default devices)',
                constraints: { audio: defaultAudioConstraint, video: defaultVideoConstraint }
            });
        }

        attempts.push({
            label: 'audio-only',
            constraints: { audio: audioConstraint, video: false }
        });

        if (c.micDeviceId) {
            attempts.push({
                label: 'audio-only (default mic)',
                constraints: { audio: defaultAudioConstraint, video: false }
            });
        }

        attempts.push({
            label: 'video-only',
            constraints: { audio: false, video: videoConstraint }
        });

        if (c.cameraDeviceId) {
            attempts.push({
                label: 'video-only (default camera)',
                constraints: { audio: false, video: defaultVideoConstraint }
            });
        }

        let lastMediaError = null;
        for (const attempt of attempts) {
            try {
                this.localStream = await navigator.mediaDevices.getUserMedia(attempt.constraints);
                this.log('media', `${attempt.label} stream acquired`);
                lastMediaError = null;
                break;
            } catch (err) {
                lastMediaError = err;
                this.log('media', `${attempt.label} failed (${err.message})`);
            }
        }

        if (!this.localStream) {
            const message = lastMediaError?.message || 'Unknown getUserMedia error';
            this.logError('media', `All media sources failed: ${message}`);
            return;
        }

        const cameraVideoTrack = this.localStream.getVideoTracks()[0] || null;
        this.applyVideoTrackContentHint(cameraVideoTrack, 'camera');

        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            localVideo.srcObject = this.localStream;
            const playPromise = localVideo.play?.();
            if (playPromise?.catch) {
                playPromise.catch(err => this.log('media', `Local preview play() blocked: ${err.message}`));
            }
        }
        this.updateLocalFeedAvatarFallback();

        // Ensure the local cell has controls/info panel after each re-render or stream refresh.
        this.ensureLocalFeedCellControls();

        // Set up audio volume control via Web Audio API
        await this.setupAudioPipeline();

        // Apply current mute/camera state
        this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
        if (this.isCameraOff) {
            this.stopLocalCameraTracks('startLocalStream-camera-off');
        } else {
            this.localStream.getVideoTracks().forEach(t => t.enabled = true);
        }
        this.updateLocalFeedAvatarFallback();
        this.ensureLocalSpeakingAnalyzer();
    },

    /** Coerce currentConstraints into their canonical shape. Shared by every path that mutates them. */
    normalizeCurrentConstraints(scope) {
        const c = this.currentConstraints;
        c.preferredVideoCodec = this.normalizeVideoCodec(c.preferredVideoCodec);
        c.h264ProfileOverride = this.normalizeH264ProfileOverrideValue(c.h264ProfileOverride);
        c.hideLocalPreview = c.hideLocalPreview === true;
        this.enforceAndroidHardwareCodecPreference(scope);
        c.screenShareAudioEnabled = c.screenShareAudioEnabled !== false;
        c.screenShareAutoGainEnabled = c.screenShareAutoGainEnabled !== false;
        c.bitrateKbps = this.clampPublisherBitrateKbps(c.bitrateKbps);
        return c;
    },

    /**
     * The raw getUserMedia capture track. setupAudioPipeline swaps localStream's audio for the
     * processed graph output, so this is the only remaining handle on the actual microphone —
     * both for rebuilding the graph and for releasing the device.
     */
    _rawMicTrack: null,

    releaseRawMicTrack() {
        if (!this._rawMicTrack) return;
        try { this._rawMicTrack.stop(); } catch { }
        this._rawMicTrack = null;
    },

    // DTLN WASM state
    _dtlnWasmBinary: null,
    _dtlnWasmModule: null,
    _dtlnWasmModuleCloneUnsupported: false,
    _dtlnWorkletNode: null,
    _dtlnWorkletReady: false,

    async setupAudioPipeline() {
        // Resolve the capture track BEFORE tearing anything down.
        //
        // After the first build, localStream's only audio track is this graph's own
        // MediaStreamAudioDestinationNode output — the raw mic was swapped out below. Closing the
        // context kills that output, so a rebuild that read localStream would feed the new graph
        // a dead track and publish silence. Always go back to the retained capture track.
        const audioTrack = (this._rawMicTrack && this._rawMicTrack.readyState === 'live')
            ? this._rawMicTrack
            : this.localStream?.getAudioTracks()[0];

        // Clean up previous
        this.stopVoiceGate();
        this._destroyDtlnWorklet();
        if (this.audioContext) {
            try { this.audioContext.close(); } catch { }
        }

        if (!audioTrack || audioTrack.readyState !== 'live') {
            this._rawMicTrack = null;
            return;
        }
        this._rawMicTrack = audioTrack;

        this.audioContext = new AudioContext();

        // Resume if suspended (browser autoplay policy / app backgrounding)
        if (this.audioContext.state === 'suspended') {
            this.audioContext.resume().catch(err =>
                this.log('audio', 'Failed to resume AudioContext: ' + err.message));
        }

        const source = this.audioContext.createMediaStreamSource(new MediaStream([audioTrack]));
        this.gainNode = this.audioContext.createGain();

        // AnalyserNode for voice gate level detection
        this.voiceGateAnalyser = this.audioContext.createAnalyser();
        this.voiceGateAnalyser.fftSize = 512;
        this.voiceGateAnalyser.smoothingTimeConstant = 0.3;

        // Route through a MediaStreamDestination so gain control works WITHOUT
        // playing audio through the speakers (which caused echo).
        this.audioDestination = this.audioContext.createMediaStreamDestination();

        // Build pipeline: source → [dtln?] → analyser → gain → destination
        const useDtln = this.currentConstraints.noiseSuppressionEnabled === true;
        let dtlnNode = null;

        if (useDtln) {
            try {
                dtlnNode = await this._createDtlnWorkletNode();
            } catch (err) {
                this.logError('audio', 'Failed to create DTLN worklet, falling back: ' + err.message);
            }
        }

        if (dtlnNode) {
            source.connect(dtlnNode);
            dtlnNode.connect(this.voiceGateAnalyser);
            this.log('audio', 'DTLN noise suppression active (WASM AudioWorklet)');
        } else {
            source.connect(this.voiceGateAnalyser);
        }
        this.voiceGateAnalyser.connect(this.gainNode);
        this.gainNode.connect(this.audioDestination);

        // Publish the gain-processed track instead of the raw capture. On a rebuild the track
        // being dropped here is the PREVIOUS graph's output, not the mic, so clear whatever is
        // there rather than assuming it is `audioTrack`.
        const processedTrack = this.audioDestination.stream.getAudioTracks()[0];
        for (const existing of this.localStream.getAudioTracks()) {
            this.localStream.removeTrack(existing);
        }
        this.localStream.addTrack(processedTrack);

        // Also replace in publisher PeerConnection if already set up
        if (this.publisherPc) {
            const audioSender = this.publisherPc.getSenders()
                .find(s => s.track?.kind === 'audio');
            if (audioSender) {
                audioSender.replaceTrack(processedTrack);
            }
        }

        // Start voice gate if enabled
        if (this.currentConstraints.voiceGateEnabled) {
            this.startVoiceGate();
        }

        this.log('audio', `Audio pipeline set up (dtln=${!!dtlnNode}, voiceGate=${this.currentConstraints.voiceGateEnabled})`);
    },

    /**
     * Resolve the DTLN WASM into whichever form this WebView can hand to the worklet.
     *
     * Preferred form is a compiled WebAssembly.Module: it costs far less resident memory than
     * the ~5.8 MB of raw bytes and skips re-compilation worklet-side on every pipeline rebuild.
     * Raw bytes are kept only where a Module cannot be structured-cloned into an
     * AudioWorkletGlobalScope.
     */
    async _ensureDtlnWasmSource() {
        if (this._dtlnWasmModule || this._dtlnWasmBinary) return;

        const resp = await fetch('js/dtln_rs.wasm');
        if (!resp.ok) throw new Error(`Failed to fetch dtln_rs.wasm: ${resp.status}`);
        const bytes = await resp.arrayBuffer();
        this.log('audio', `DTLN WASM loaded (${(bytes.byteLength / 1024).toFixed(0)} KB)`);

        if (!this._dtlnWasmModuleCloneUnsupported) {
            try {
                this._dtlnWasmModule = await WebAssembly.compile(bytes);
                return; // raw bytes drop out of scope here
            } catch (err) {
                this.log('audio', `DTLN WASM compile failed, keeping raw bytes: ${err.message}`);
            }
        }

        this._dtlnWasmBinary = bytes;
    },

    async _postDtlnWasmSource(node) {
        await this._ensureDtlnWasmSource();

        if (this._dtlnWasmModule) {
            try {
                node.port.postMessage({ type: 'wasm-binary', wasmBinary: this._dtlnWasmModule });
                return;
            } catch (err) {
                // postMessage throws DataCloneError synchronously where Module cloning into an
                // AudioWorkletGlobalScope is unsupported — use that as the feature detect.
                this.log('audio', `DTLN Module clone unsupported, falling back to bytes: ${err.message}`);
                this._dtlnWasmModuleCloneUnsupported = true;
                this._dtlnWasmModule = null;
                await this._ensureDtlnWasmSource();
            }
        }

        const bytes = this._dtlnWasmBinary;
        if (!bytes) throw new Error('DTLN WASM unavailable');
        const copy = bytes.slice(0);
        node.port.postMessage({ type: 'wasm-binary', wasmBinary: copy }, [copy]);
    },

    async _createDtlnWorkletNode() {
        const ctx = this.audioContext;

        // Register the worklet processor (idempotent — browser caches it)
        await ctx.audioWorklet.addModule('js/dtln-worklet.js');

        // Create the worklet node
        const node = new AudioWorkletNode(ctx, 'dtln-processor', {
            processorOptions: { sampleRate: ctx.sampleRate },
            numberOfInputs: 1,
            numberOfOutputs: 1,
            outputChannelCount: [1],
        });

        // Wait for the worklet to request and initialize the WASM
        await new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('DTLN worklet init timeout')), 10000);
            node.port.onmessage = (e) => {
                if (e.data.type === 'request-wasm') {
                    this._postDtlnWasmSource(node).catch(err => {
                        clearTimeout(timeout);
                        reject(err);
                    });
                } else if (e.data.type === 'ready') {
                    clearTimeout(timeout);
                    resolve();
                } else if (e.data.type === 'error') {
                    clearTimeout(timeout);
                    reject(new Error(e.data.error));
                }
            };
        });

        this._dtlnWorkletNode = node;
        this._dtlnWorkletReady = true;
        return node;
    },

    _destroyDtlnWorklet() {
        if (this._dtlnWorkletNode) {
            // Drop the port handler first — it closes over `node` and the WASM source.
            try { this._dtlnWorkletNode.port.onmessage = null; } catch { }
            try { this._dtlnWorkletNode.port.close(); } catch { }
            try { this._dtlnWorkletNode.disconnect(); } catch { }
            this._dtlnWorkletNode = null;
        }
        this._dtlnWorkletReady = false;
    },

    /**
     * Release the cached DTLN WASM. Called on manager stop — keeping it across a call avoids
     * a re-fetch on every settings save, but nothing should retain it once the call is over.
     */
    releaseDtlnWasmCache() {
        this._dtlnWasmBinary = null;
        this._dtlnWasmModule = null;
    },

    // ─── Voice Gate ──────────────────────────────────────────────────────────────

    _voiceGateOpen: true,
    _voiceGateHoldUntil: 0,
    _voiceGateTimerId: 0,
    // 50 Hz — far under the 5ms attack / 250ms hold constants, and independent of refresh rate.
    voiceGatePollIntervalMs: 20,
    _voiceGateRmsBuffer: null,
    _voiceGateAutoFloor: 0, // tracked ambient noise floor (RMS)
    _voiceGateAutoFloorSamples: 0,

    startVoiceGate() {
        this.stopVoiceGate();
        if (!this.voiceGateAnalyser) return;

        const bufLen = this.voiceGateAnalyser.fftSize;
        this._voiceGateRmsBuffer = new Float32Array(bufLen);
        this._voiceGateOpen = true;
        this._voiceGateHoldUntil = 0;
        this._voiceGateAutoFloor = 0;
        this._voiceGateAutoFloorSamples = 0;

        const holdMs = 250; // keep gate open 250ms after voice stops
        const attackMs = 5; // near-instant gate open
        const releaseMs = 40; // smooth gate close to avoid clicks
        // Auto mode: multiplier above the noise floor to open the gate
        const autoMargin = 2.5;

        // The gate used to self-arm a requestAnimationFrame every frame, so it ran at display
        // refresh rate — 2.75x more often than intended on a 165 Hz panel, and it kept the frame
        // loop scheduled for the whole call even in an audio-only call. It now runs on a fixed
        // tick. The floor-tracking constants below were tuned per frame at a nominal 60 Hz, so
        // re-express them for the tick or the adaptation rate changes with it.
        const tickMs = this.voiceGatePollIntervalMs;
        const framesPerTick = 60 / (1000 / tickMs);
        const floorDecay = Math.pow(0.997, framesPerTick);
        const floorRise = 1 - Math.pow(1 - 0.03, framesPerTick);
        const calibrationTicks = Math.max(1, Math.round(0.5 * (1000 / tickMs))); // ~0.5s

        const poll = () => {
            if (!this.voiceGateAnalyser || !this._voiceGateRmsBuffer) {
                // Previously the rAF chain simply stopped rescheduling here, which could strand
                // the gain at 0. stopVoiceGate restores it.
                this.stopVoiceGate();
                return;
            }

            this.voiceGateAnalyser.getFloatTimeDomainData(this._voiceGateRmsBuffer);

            // Compute RMS level
            let sumSq = 0;
            for (let i = 0; i < this._voiceGateRmsBuffer.length; i++) {
                sumSq += this._voiceGateRmsBuffer[i] * this._voiceGateRmsBuffer[i];
            }
            const rms = Math.sqrt(sumSq / this._voiceGateRmsBuffer.length);

            let threshold;
            const isAuto = this.currentConstraints.voiceGateAuto !== false;

            if (isAuto) {
                // Auto mode: track ambient noise floor and set threshold above it
                if (this._voiceGateAutoFloorSamples < calibrationTicks) {
                    // Calibration phase (~0.5s): collect initial noise floor
                    this._voiceGateAutoFloor = this._voiceGateAutoFloorSamples === 0
                        ? rms
                        : Math.max(this._voiceGateAutoFloor, rms);
                    this._voiceGateAutoFloorSamples++;
                    threshold = rms + 0.01; // keep gate open during calibration
                } else {
                    // Running: track floor — decays slowly, rises quickly if
                    // ambient gets louder while gate is closed (not speaking)
                    if (!this._voiceGateOpen && rms > this._voiceGateAutoFloor) {
                        this._voiceGateAutoFloor += (rms - this._voiceGateAutoFloor) * floorRise;
                    } else if (rms < this._voiceGateAutoFloor) {
                        this._voiceGateAutoFloor *= floorDecay;
                    }
                    // Threshold = noise floor * margin, with a minimum floor
                    const floor = Math.max(this._voiceGateAutoFloor, 0.001);
                    threshold = floor * autoMargin;
                }
            } else {
                // Manual mode: convert threshold (1-100) to RMS scale
                threshold = (this.currentConstraints.voiceGateThreshold || 30) / 1000;
            }

            const now = performance.now();

            if (rms >= threshold) {
                // Signal above threshold — open gate
                this._voiceGateHoldUntil = now + holdMs;
                if (!this._voiceGateOpen) {
                    this._voiceGateOpen = true;
                    if (this.gainNode) {
                        this.gainNode.gain.cancelScheduledValues(this.audioContext.currentTime);
                        this.gainNode.gain.linearRampToValueAtTime(1.0, this.audioContext.currentTime + attackMs / 1000);
                    }
                }
            } else if (now > this._voiceGateHoldUntil && this._voiceGateOpen) {
                // Signal below threshold and hold expired — close gate
                this._voiceGateOpen = false;
                if (this.gainNode) {
                    this.gainNode.gain.cancelScheduledValues(this.audioContext.currentTime);
                    this.gainNode.gain.linearRampToValueAtTime(0.0, this.audioContext.currentTime + releaseMs / 1000);
                }
            }

        };

        this._voiceGateTimerId = setInterval(poll, tickMs);
        const mode = this.currentConstraints.voiceGateAuto !== false ? 'auto' : `manual(${this.currentConstraints.voiceGateThreshold}%)`;
        this.log('audio', `Voice gate started (${mode}, ${tickMs}ms tick)`);
    },

    stopVoiceGate() {
        if (this._voiceGateTimerId) {
            clearInterval(this._voiceGateTimerId);
            this._voiceGateTimerId = 0;
        }
        this._voiceGateRmsBuffer = null;
        this._voiceGateAutoFloor = 0;
        this._voiceGateAutoFloorSamples = 0;
        // Restore full gain when gate is disabled
        if (this._voiceGateOpen === false && this.gainNode && this.audioContext) {
            try {
                this.gainNode.gain.cancelScheduledValues(this.audioContext.currentTime);
                this.gainNode.gain.setValueAtTime(1.0, this.audioContext.currentTime);
            } catch { }
        }
        this._voiceGateOpen = true;
    },

    getVoiceGateMicLevel() {
        // Returns mic RMS level as 0-100 for UI display, plus the effective threshold
        if (!this.voiceGateAnalyser) return { level: 0, threshold: 0 };

        const bufLen = this.voiceGateAnalyser.fftSize;
        const buf = this._voiceGateRmsBuffer || new Float32Array(bufLen);
        this.voiceGateAnalyser.getFloatTimeDomainData(buf);

        let sumSq = 0;
        for (let i = 0; i < buf.length; i++) {
            sumSq += buf[i] * buf[i];
        }
        const rms = Math.sqrt(sumSq / buf.length);

        // Scale RMS to 0-100 (typical speech RMS ~0.01-0.1)
        const level = Math.min(100, Math.round(rms * 1000));

        let threshold;
        const isAuto = this.currentConstraints.voiceGateAuto !== false;
        if (isAuto) {
            const floor = Math.max(this._voiceGateAutoFloor || 0, 0.001);
            threshold = Math.min(100, Math.round(floor * 2.5 * 1000));
        } else {
            threshold = this.currentConstraints.voiceGateThreshold || 30;
        }

        return { level, threshold };
    },

    applyVoiceGateSettings() {
        const enabled = this.currentConstraints.voiceGateEnabled === true;
        if (enabled && !this._voiceGateTimerId && this.voiceGateAnalyser) {
            this.startVoiceGate();
        } else if (!enabled && this._voiceGateTimerId) {
            this.stopVoiceGate();
        }
    },

    async ensureAudioContextActive() {
        if (this.audioContext && this.audioContext.state === 'suspended') {
            try {
                await this.audioContext.resume();
                this.log('audio', 'AudioContext resumed from suspended state');
            } catch (err) {
                this.logError('audio', 'Failed to resume AudioContext: ' + err.message);
            }
        }
    },

    async cleanupScreenShareAudioMixTrack() {
        if (this.screenShareAudioMixTrack) {
            try { this.screenShareAudioMixTrack.stop(); } catch { }
        }
        this.screenShareAudioMixTrack = null;

        if (this.screenShareAudioMixContext) {
            try { await this.screenShareAudioMixContext.close(); } catch { }
        }
        this.screenShareAudioMixContext = null;
    },

    async buildScreenShareOutgoingAudioTrack(screenAudioTrack, micAudioTrack) {
        await this.cleanupScreenShareAudioMixTrack();

        const liveScreenTrack = (screenAudioTrack && screenAudioTrack.readyState === 'live') ? screenAudioTrack : null;
        const liveMicTrack = (micAudioTrack && micAudioTrack.readyState === 'live') ? micAudioTrack : null;

        if (liveScreenTrack && liveMicTrack && liveScreenTrack.id !== liveMicTrack.id) {
            try {
                const context = new AudioContext();
                if (context.state === 'suspended') {
                    await context.resume().catch(() => { });
                }

                const destination = context.createMediaStreamDestination();

                const micSource = context.createMediaStreamSource(new MediaStream([liveMicTrack]));
                const micGain = context.createGain();
                micGain.gain.value = 1.0;
                micSource.connect(micGain);
                micGain.connect(destination);

                const screenSource = context.createMediaStreamSource(new MediaStream([liveScreenTrack]));
                const screenGain = context.createGain();
                screenGain.gain.value = 1.0;
                screenSource.connect(screenGain);
                screenGain.connect(destination);

                const mixedTrack = destination.stream.getAudioTracks()[0] || null;
                if (mixedTrack) {
                    this.screenShareAudioMixContext = context;
                    this.screenShareAudioMixTrack = mixedTrack;
                    this.log('screenshare', 'Using mixed outgoing audio track (mic + screen audio)');
                    return mixedTrack;
                }

                try { context.close(); } catch { }
            } catch (err) {
                this.log('screenshare', `Failed to build mixed screen-share audio track (${err.message}), using fallback track selection`);
            }
        }

        return liveScreenTrack || liveMicTrack || null;
    },

    isWindowsNativeFallbackAudioTrack(track) {
        if (!track || !this.screenAudioFallbackTrack) return false;
        return track.id === this.screenAudioFallbackTrack.id;
    },

    selectPreferredScreenShareAudioTrack(stream) {
        const tracks = stream?.getAudioTracks?.() || [];
        if (!Array.isArray(tracks) || tracks.length === 0) return null;

        // Prefer browser/native display-captured audio over host fallback when both exist.
        return tracks.find(t => t && t.readyState === 'live' && !this.isWindowsNativeFallbackAudioTrack(t))
            || tracks.find(t => t && !this.isWindowsNativeFallbackAudioTrack(t))
            || tracks.find(t => t && t.readyState === 'live')
            || tracks[0]
            || null;
    },

    async waitForScreenShareAudioTrack(stream, timeoutMs = 900) {
        if (!stream || typeof stream.getAudioTracks !== 'function') return null;
        const existing = stream.getAudioTracks().find(t => t && t.readyState === 'live') || null;
        if (existing) return existing;

        return await new Promise(resolve => {
            let done = false;
            let timerId = null;
            const finish = (track) => {
                if (done) return;
                done = true;
                if (timerId) clearTimeout(timerId);
                try { stream.removeEventListener?.('addtrack', onAddTrack); } catch { }
                resolve(track || null);
            };
            const onAddTrack = (event) => {
                const track = event?.track;
                if (track?.kind !== 'audio') return;
                finish(track);
            };

            try { stream.addEventListener?.('addtrack', onAddTrack); } catch { }
            timerId = setTimeout(() => {
                const track = stream.getAudioTracks().find(t => t && t.readyState === 'live')
                    || stream.getAudioTracks()[0]
                    || null;
                finish(track);
            }, Math.max(100, parseInt(timeoutMs, 10) || 900));
        });
    },

    async buildBaseOutgoingAudioTrack() {
        if (this.isScreenSharing) {
            // During screen share, keep the primary publisher on microphone audio only.
            return this.localStream?.getAudioTracks?.()[0] || null;
        }
        return this.localStream?.getAudioTracks?.()[0] || null;
    },

    async cleanupMusicBotGraph() {
        if (this.musicBotElement) {
            try { this.musicBotElement.pause(); } catch { }
            try { this.musicBotElement.removeAttribute('src'); } catch { }
            try { this.musicBotElement.load(); } catch { }
            this.musicBotElement.onended = null;
            this.musicBotElement.onerror = null;
        }

        if (this.musicBotMixedTrack) {
            try { this.musicBotMixedTrack.stop(); } catch { }
        }

        this.musicBotSource = null;
        this.musicBotGainNode = null;
        this.musicBotMixedTrack = null;
        this.musicBotDestination = null;
        this.musicBotElement = null;

        if (this.musicBotContext) {
            try { await this.musicBotContext.close(); } catch { }
        }
        this.musicBotContext = null;
    },

    setMusicBotVolume(level) {
        const clamped = Math.max(0, Math.min(2, Number(level) || 0));
        this.musicBotVolume = clamped;
        if (this.musicBotGainNode) {
            this.musicBotGainNode.gain.value = clamped;
        }
    },

    async stopMusicBotPlaybackInternal(options) {
        const opts = options || {};
        const restorePublisherTrack = opts.restorePublisherTrack !== false;
        const notifyState = opts.notifyState !== false;

        const wasActive = this.musicBotActive;
        const previousTitle = this.musicBotTitle || '';

        if (restorePublisherTrack && this.publisherPc) {
            const audioSender = this.findSenderByKind(this.publisherPc, 'audio');
            if (audioSender) {
                const restoreTrack = await this.buildBaseOutgoingAudioTrack();
                await audioSender.replaceTrack(restoreTrack);
                const audioChanged = await this.enforceSinglePublisherAudioSender(restoreTrack);
                if (audioChanged) {
                    await this.renegotiatePublisher('musicbot-stop');
                }
            }
        }

        await this.cleanupMusicBotGraph();

        this.musicBotActive = false;
        this.musicBotTitle = '';
        this.musicBotStreamUrl = '';

        if (notifyState && wasActive) {
            this.notifyDotNet('OnMusicBotStateChanged', false, previousTitle);
        }
    },

    async startMusicBotPlayback(streamUrl, title, volume) {
        const normalizedStreamUrl = (streamUrl || '').toString().trim();
        if (!normalizedStreamUrl) {
            throw new Error('Music bot stream URL is empty');
        }

        if (!this.publisherPc) {
            throw new Error('Call is not connected');
        }

        const audioSender = this.findSenderByKind(this.publisherPc, 'audio');
        if (!audioSender) {
            throw new Error('No outgoing audio sender is available');
        }

        await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: false });
        await this.ensureAudioContextActive();

        const baseTrack = await this.buildBaseOutgoingAudioTrack();
        if (!baseTrack) {
            throw new Error('No microphone or share audio track is available');
        }

        const context = new AudioContext();
        if (context.state === 'suspended') {
            await context.resume().catch(() => { });
        }

        const destination = context.createMediaStreamDestination();

        const baseSource = context.createMediaStreamSource(new MediaStream([baseTrack]));
        const baseGain = context.createGain();
        baseGain.gain.value = 1.0;
        baseSource.connect(baseGain);
        baseGain.connect(destination);

        const mediaElement = document.createElement('audio');
        mediaElement.crossOrigin = 'anonymous';
        mediaElement.preload = 'auto';
        mediaElement.src = normalizedStreamUrl;
        mediaElement.autoplay = false;
        mediaElement.playsInline = true;

        const musicSource = context.createMediaElementSource(mediaElement);
        const musicGain = context.createGain();
        const clampedVolume = Math.max(0, Math.min(2, Number(volume) || this.musicBotVolume || 0.8));
        musicGain.gain.value = clampedVolume;
        musicSource.connect(musicGain);
        musicGain.connect(destination);
        musicGain.connect(context.destination);

        const mixedTrack = destination.stream.getAudioTracks()[0] || null;
        if (!mixedTrack) {
            try { await context.close(); } catch { }
            throw new Error('Unable to create outgoing bot audio track');
        }

        mediaElement.onended = () => {
            this.stopMusicBotPlaybackInternal({ restorePublisherTrack: true, notifyState: true }).catch(() => { });
        };
        mediaElement.onerror = () => {
            this.notifyDotNet('OnError', 'musicbot: playback error');
            this.stopMusicBotPlaybackInternal({ restorePublisherTrack: true, notifyState: true }).catch(() => { });
        };

        try {
            await mediaElement.play();
        } catch (err) {
            try { await context.close(); } catch { }
            throw new Error(`Unable to start playback: ${err.message}`);
        }

        await audioSender.replaceTrack(mixedTrack);
        const audioChanged = await this.enforceSinglePublisherAudioSender(mixedTrack);
        if (audioChanged) {
            await this.renegotiatePublisher('musicbot-start');
        }

        this.musicBotContext = context;
        this.musicBotDestination = destination;
        this.musicBotElement = mediaElement;
        this.musicBotSource = musicSource;
        this.musicBotGainNode = musicGain;
        this.musicBotMixedTrack = mixedTrack;
        this.musicBotActive = true;
        this.musicBotTitle = (title || 'YouTube Track').toString();
        this.musicBotStreamUrl = normalizedStreamUrl;
        this.musicBotVolume = clampedVolume;

        this.notifyDotNet('OnMusicBotStateChanged', true, this.musicBotTitle);
    },

    async stopMusicBotPlayback() {
        await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: true, notifyState: true });
    },

    async startWindowsScreenAudioFallback(options) {
        if (this.screenAudioFallbackActive) {
            return this.screenAudioFallbackTrack;
        }

        if (!window.chrome || !window.chrome.webview || !window.chrome.webview.postMessage) {
            return null;
        }

        try {
            await this.ensureWindowsScreenAudioGraph();
            this.screenAudioFallbackActive = true;
            this.screenAudioFallbackNextTime = this.screenAudioFallbackContext.currentTime;
            this.notifyHostScreenAudioFallback('start', {
                preferProcessIsolation: !!options?.preferProcessIsolation,
                allowSystemFallback: options?.allowSystemFallback !== false,
                targetLabel: options?.targetLabel || '',
                displaySurface: options?.displaySurface || ''
            });
            return this.screenAudioFallbackTrack;
        } catch (err) {
            this.log('screenshare', `Unable to initialize Windows audio fallback graph: ${err.message}`);
            await this.stopWindowsScreenAudioFallback(false);
            return null;
        }
    },

    async ensureWindowsScreenAudioGraph() {
        if (this.screenAudioFallbackTrack && this.screenAudioFallbackContext) {
            if (this.screenAudioFallbackContext.state === 'suspended') {
                await this.screenAudioFallbackContext.resume().catch(() => { });
            }
            return;
        }

        const context = new AudioContext();
        if (context.state === 'suspended') {
            await context.resume().catch(() => { });
        }

        const gain = context.createGain();
        const limiter = context.createDynamicsCompressor();
        const destination = context.createMediaStreamDestination();
        gain.connect(limiter);
        limiter.connect(destination);
        const autoGainEnabled = this.currentConstraints.screenShareAutoGainEnabled !== false;
        const initialGain = autoGainEnabled
            ? Math.max(
                this.screenAudioFallbackMinGain,
                Math.min(this.screenAudioFallbackMaxGain, Number(this.screenAudioFallbackAutoGain) || 1.0)
            )
            : 1.0;
        gain.gain.value = initialGain;
        limiter.threshold.value = -2;
        limiter.knee.value = 0;
        limiter.ratio.value = 20;
        limiter.attack.value = 0.003;
        limiter.release.value = 0.12;

        const track = destination.stream.getAudioTracks()[0];
        if (!track) {
            try { context.close(); } catch { }
            throw new Error('Failed to create fallback audio track');
        }

        this.screenAudioFallbackContext = context;
        this.screenAudioFallbackGain = gain;
        this.screenAudioFallbackLimiter = limiter;
        this.screenAudioFallbackDestination = destination;
        this.screenAudioFallbackTrack = track;
    },

    applyWindowsScreenAudioFallbackGainMode(reason = 'settings') {
        if (!this.screenAudioFallbackGain || !this.screenAudioFallbackContext) return;

        const autoGainEnabled = this.currentConstraints.screenShareAutoGainEnabled !== false;
        const minGain = Math.max(0.1, Number(this.screenAudioFallbackMinGain) || 1.0);
        const maxGain = Math.max(minGain, Number(this.screenAudioFallbackMaxGain) || 4.5);
        const targetGain = autoGainEnabled
            ? Math.max(minGain, Math.min(maxGain, Number(this.screenAudioFallbackAutoGain) || 1.0))
            : 1.0;

        const now = this.screenAudioFallbackContext.currentTime;
        try {
            this.screenAudioFallbackGain.gain.cancelScheduledValues(now);
            this.screenAudioFallbackGain.gain.setTargetAtTime(targetGain, now, 0.06);
        } catch {
            this.screenAudioFallbackGain.gain.value = targetGain;
        }

        this.log('screenshare', `Screen-share fallback auto gain ${autoGainEnabled ? 'enabled' : 'disabled'} (${reason})`);
    },

    decodeBase64ToBytes(base64Data) {
        const raw = atob(base64Data);
        const out = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) {
            out[i] = raw.charCodeAt(i);
        }
        return out;
    },

    trackScheduledScreenAudioSource(source, startAt) {
        const scheduled = this._screenAudioScheduled || (this._screenAudioScheduled = []);
        const entry = { source, startAt };
        scheduled.push(entry);
        source.onended = () => {
            const index = scheduled.indexOf(entry);
            if (index >= 0) scheduled.splice(index, 1);
        };
    },

    /** Cancels every queued chunk starting at or after `fromTime`. Used when the clock resyncs. */
    dropScheduledScreenAudioFrom(fromTime) {
        const scheduled = this._screenAudioScheduled;
        if (!scheduled?.length) return;

        let dropped = 0;
        for (const entry of scheduled.slice()) {
            if (entry.startAt < fromTime) continue;
            try { entry.source.onended = null; } catch { }
            try { entry.source.stop(); } catch { }
            try { entry.source.disconnect(); } catch { }
            const index = scheduled.indexOf(entry);
            if (index >= 0) scheduled.splice(index, 1);
            dropped++;
        }
        if (dropped > 0) {
            this.log('screenshare', `Dropped ${dropped} queued screen-audio chunk(s) on resync`);
        }
    },

    clearScheduledScreenAudio() {
        const scheduled = this._screenAudioScheduled;
        if (!scheduled?.length) return;
        for (const entry of scheduled) {
            try { entry.source.onended = null; } catch { }
            try { entry.source.stop(); } catch { }
            try { entry.source.disconnect(); } catch { }
        }
        scheduled.length = 0;
    },

    getScreenAudioScratchBuffer(frameCount) {
        if (!this._screenAudioScratch || this._screenAudioScratch.length < frameCount) {
            this._screenAudioScratch = new Float32Array(frameCount);
        }
        return this._screenAudioScratch;
    },

    updateWindowsScreenAudioAutoGain(rms, peak) {
        if (!this.screenAudioFallbackGain || !this.screenAudioFallbackContext) return;
        if (this.currentConstraints.screenShareAutoGainEnabled === false) return;
        if (!Number.isFinite(rms) || !Number.isFinite(peak)) return;
        if (rms <= 0 || peak <= 0) return;

        const noiseGateRms = Math.max(0, Number(this.screenAudioFallbackNoiseGateRms) || 0.002);
        if (rms < noiseGateRms && peak < noiseGateRms * 3) {
            return;
        }

        const minGain = Math.max(0.1, Number(this.screenAudioFallbackMinGain) || 1.0);
        const maxGain = Math.max(minGain, Number(this.screenAudioFallbackMaxGain) || 4.5);
        const targetRms = Math.max(0.005, Number(this.screenAudioFallbackTargetRms) || 0.11);
        let desiredGain = targetRms / Math.max(rms, 0.0001);
        desiredGain = Math.max(minGain, Math.min(maxGain, desiredGain));

        if (peak * desiredGain > 0.98 && peak > 0.0001) {
            desiredGain = Math.max(minGain, Math.min(maxGain, 0.98 / peak));
        }

        const previousGain = Math.max(minGain, Math.min(maxGain, Number(this.screenAudioFallbackAutoGain) || 1.0));
        const smoothing = desiredGain >= previousGain ? 0.35 : 0.12;
        const smoothedGain = previousGain + (desiredGain - previousGain) * smoothing;
        const nextGain = Math.max(minGain, Math.min(maxGain, smoothedGain));

        if (!Number.isFinite(nextGain)) return;
        this.screenAudioFallbackAutoGain = nextGain;

        const now = this.screenAudioFallbackContext.currentTime;
        try {
            this.screenAudioFallbackGain.gain.cancelScheduledValues(now);
            this.screenAudioFallbackGain.gain.setTargetAtTime(nextGain, now, 0.06);
        } catch {
            this.screenAudioFallbackGain.gain.value = nextGain;
        }
    },

    ingestWindowsScreenAudioChunk(chunk) {
        if (!this.screenAudioFallbackActive || !this.screenAudioFallbackContext || !this.screenAudioFallbackGain) {
            return;
        }
        if (!chunk || chunk.format !== 'f32le' || !chunk.data) {
            return;
        }

        const sampleRate = parseInt(chunk.sampleRate) || 48000;
        const channels = Math.max(1, parseInt(chunk.channels) || 2);

        const rawBytes = this.decodeBase64ToBytes(chunk.data);
        if (!rawBytes || rawBytes.byteLength < 4 * channels) {
            return;
        }

        const totalSamples = Math.floor(rawBytes.byteLength / 4);
        const frameCount = Math.floor(totalSamples / channels);
        if (frameCount <= 0) {
            return;
        }

        const interleaved = new Float32Array(rawBytes.buffer, rawBytes.byteOffset, frameCount * channels);
        const buffer = this.screenAudioFallbackContext.createBuffer(channels, frameCount, sampleRate);

        // De-interleave and accumulate RMS/peak in one pass over the samples, into a reused
        // scratch buffer — this runs on the main thread for every captured chunk.
        const scratch = this.getScreenAudioScratchBuffer(frameCount);
        let sumSquares = 0;
        let peak = 0;

        for (let ch = 0; ch < channels; ch++) {
            for (let i = 0; i < frameCount; i++) {
                const value = interleaved[i * channels + ch];
                scratch[i] = value;
                const absValue = value < 0 ? -value : value;
                if (absValue > peak) peak = absValue;
                sumSquares += value * value;
            }
            buffer.copyToChannel(scratch.subarray(0, frameCount), ch);
        }

        const scannedSamples = frameCount * channels;
        this.updateWindowsScreenAudioAutoGain(
            scannedSamples > 0 ? Math.sqrt(sumSquares / scannedSamples) : 0,
            peak);

        const source = this.screenAudioFallbackContext.createBufferSource();
        source.buffer = buffer;
        source.connect(this.screenAudioFallbackGain);

        const now = this.screenAudioFallbackContext.currentTime;
        let startAt = Math.max(now + 0.01, this.screenAudioFallbackNextTime || now);

        // The schedule only ever advanced by buffer.duration and was never compared against the
        // context clock, so any burst or positive capture/render drift permanently grew the
        // backlog — every future-scheduled node and its buffer stays alive until its start time.
        if (startAt - now > this.screenAudioFallbackMaxLeadSeconds) {
            this.log(
                'screenshare',
                `Screen-audio playout backlog ${(startAt - now).toFixed(3)}s exceeded ${this.screenAudioFallbackMaxLeadSeconds}s; resyncing to the context clock`);
            startAt = now + 0.05;
            // Pulling the schedule backwards would otherwise play this chunk on top of everything
            // already queued past the new start time.
            this.dropScheduledScreenAudioFrom(startAt);
        }

        this.trackScheduledScreenAudioSource(source, startAt);
        source.start(startAt);
        this.screenAudioFallbackNextTime = startAt + buffer.duration;
    },

    async stopWindowsScreenAudioFallback(notifyHost = true) {
        if (notifyHost) {
            this.notifyHostScreenAudioFallback('stop');
        }

        this.screenAudioFallbackActive = false;
        this.screenAudioFallbackNextTime = 0;
        this.clearScheduledScreenAudio();

        if (this.screenStream && this.screenAudioFallbackTrack) {
            try { this.screenStream.removeTrack(this.screenAudioFallbackTrack); } catch { }
        }

        if (this.screenAudioFallbackTrack) {
            try { this.screenAudioFallbackTrack.stop(); } catch { }
        }
        this.screenAudioFallbackTrack = null;
        this.screenAudioFallbackDestination = null;
        this.screenAudioFallbackGain = null;
        this.screenAudioFallbackLimiter = null;
        this.screenAudioFallbackAutoGain = 1.0;

        if (this.screenAudioFallbackContext) {
            try { await this.screenAudioFallbackContext.close(); } catch { }
        }
        this.screenAudioFallbackContext = null;
    },

    // ─── Publisher ──────────────────────────────────────────────────────────────

    /**
     * Publishes own feed to Janus. Uses the sync /message endpoint because
     * the polling loop hasn't started yet (no event queue competition).
     * Returns an array of existing publishers to subscribe to.
     */
    async publishOwnFeed() {
        const pc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.publisherPc = pc;
        this.publisherConfigured = false;

        // Monitor publisher connection state — detect ICE/DTLS failures
        pc.oniceconnectionstatechange = () => {
            const state = pc.iceConnectionState;
            this.log('publish', `ICE connection state: ${state}`);
            if (state === 'failed') {
                this.logError('publish', 'Publisher ICE connection FAILED — Janus cannot receive our media');
                this.notifyDotNet('OnPublisherConnectionFailed');
            } else if (state === 'disconnected') {
                this.log('publish', 'Publisher ICE disconnected — may recover or fail');
            } else if (state === 'connected' || state === 'completed') {
                this.log('publish', 'Publisher ICE connected to Janus');
                if (this.isAndroidWebRtcEnvironment()) {
                    setTimeout(() => {
                        this.checkVideoEncoder(pc).catch(() => { });
                    }, 2500);
                }
            }
        };
        pc.onconnectionstatechange = () => {
            this.log('publish', `PeerConnection state: ${pc.connectionState}`);
        };

        // Add local tracks (or receive-only transceivers if no media available)
        const stream = this.localStream;
        if (stream && stream.getTracks().length > 0) {
            stream.getTracks().forEach(track => pc.addTrack(track, stream));
            // Ensure both audio and video lines exist in SDP even if only one media type is available
            const hasAudio = stream.getAudioTracks().length > 0;
            const hasVideo = stream.getVideoTracks().length > 0;
            if (!hasAudio) {
                pc.addTransceiver('audio', { direction: 'recvonly' });
                this.log('publish', 'No audio track — added recvonly audio transceiver');
            }
            if (!hasVideo) {
                pc.addTransceiver('video', { direction: 'recvonly' });
                this.log('publish', 'No video track — added recvonly video transceiver');
            }
        } else {
            // No local media at all — add recvonly so Janus gets a valid SDP
            pc.addTransceiver('audio', { direction: 'recvonly' });
            pc.addTransceiver('video', { direction: 'recvonly' });
            this.log('publish', 'No local media — added recvonly transceivers');
        }

        // Buffer ICE candidates until after joinandconfigure completes
        const candidateBuffer = [];
        let handleConfigured = false;

        pc.onicecandidate = ({ candidate }) => {
            if (!handleConfigured) {
                candidateBuffer.push(candidate);
                return;
            }
            this.queueTrickleCandidate(this.janusHandleId, candidate);
        };

        // Create offer with codec/profile fallbacks and browser-default recovery.
        const offer = await this.createPublisherOfferWithCodecRecovery(pc, 'publish');
        this.logCodecSummary(pc, 'publish-offer');
        this.log('publish', 'Created SDP offer, sending joinandconfigure...');

        // Join as publisher with SDP offer (sync — polling not started yet)
        let joinResult;
        const preferredJanusCodec = this.getJanusRequestedVideoCodec();
        let requestedJanusCodec = this.getJanusVideoCodecForSdp(offer.sdp, preferredJanusCodec);
        const sendJoinAndConfigure = async (localOffer, videocodec, reasonTag) => {
            const body = {
                request: 'joinandconfigure',
                room: this.janusRoomId,
                ptype: 'publisher',
                display: this.buildPublisherDisplay('primary')
            };
            if (videocodec) {
                body.videocodec = videocodec;
            }
            const result = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message`,
                {
                    body,
                    jsep: { type: (localOffer?.type || 'offer'), sdp: localOffer.sdp }
                }
            );
            this.log('publish', `joinandconfigure response received (${result?.janus}, requested videocodec=${videocodec || 'auto'}${reasonTag ? `, attempt=${reasonTag}` : ''})`);
            if (result?.janus === 'error') {
                const janusError = result?.error?.reason;
                const janusErrorCode = result?.error?.code;
                const pluginError = result?.plugindata?.data?.error;
                const pluginErrorCode = result?.plugindata?.data?.error_code;
                const errorText = janusError || pluginError || 'Janus returned error';
                const errorCode = janusErrorCode ?? pluginErrorCode;
                const detail = `${errorText}${errorCode ? ` (code ${errorCode})` : ''}`;
                throw new Error(detail);
            }
            return result;
        };
        const getCodecFallbackOrder = (codec) => {
            switch ((codec || '').toLowerCase()) {
                case 'h265':
                    return ['h264', 'vp8'];
                case 'h264':
                    return ['vp8'];
                case 'vp9':
                    return ['vp8', 'h264'];
                case 'vp8':
                    return ['h264'];
                case 'av1':
                    return ['h264', 'vp8'];
                default:
                    return ['h264', 'vp8'];
            }
        };
        try {
            joinResult = await sendJoinAndConfigure(offer, requestedJanusCodec, 'primary');
        } catch (err) {
            const primaryRequestedCodec = requestedJanusCodec;
            const attemptErrors = [`primary(${primaryRequestedCodec || 'auto'}): ${err.message}`];
            const fallbackOrder = getCodecFallbackOrder(primaryRequestedCodec)
                .filter(codec => codec && codec !== primaryRequestedCodec);
            if (fallbackOrder.length === 0) {
                this.logError('publish', `Failed to join room (${primaryRequestedCodec}): ${err.message}`);
                return [];
            }

            this.log(
                'publish',
                `${primaryRequestedCodec.toUpperCase()} join failed (${err.message}), retrying with fallback codec offer(s): ${fallbackOrder.join(', ')}...`
            );

            const previousPreferredCodec = this.currentConstraints.preferredVideoCodec;
            let fallbackSelectedCodec = null;
            let fallbackJoined = false;
            for (const fallbackCodec of fallbackOrder) {
                try {
                    this.currentConstraints.preferredVideoCodec = fallbackCodec;
                    const fallbackOffer = await this.createPublisherOfferWithCodecRecovery(pc, `publish-${fallbackCodec}-fallback`);
                    requestedJanusCodec = this.getJanusVideoCodecForSdp(fallbackOffer?.sdp || '', fallbackCodec);
                    joinResult = await sendJoinAndConfigure(fallbackOffer, requestedJanusCodec, `${fallbackCodec}-fallback`);
                    fallbackSelectedCodec = this.normalizeVideoCodec(requestedJanusCodec || fallbackCodec);
                    fallbackJoined = true;
                    break;
                } catch (fallbackErr) {
                    attemptErrors.push(`${fallbackCodec}: ${fallbackErr.message}`);
                    this.log('publish', `${fallbackCodec.toUpperCase()} fallback join failed (${fallbackErr.message})`);
                }
            }

            if (!fallbackJoined) {
                try {
                    // Last resort: let Janus pick the codec from SDP without explicit videocodec.
                    this.currentConstraints.preferredVideoCodec = previousPreferredCodec;
                    this.resetPublisherCodecPreference('publish-auto-fallback');

                    const autoRawOffer = await pc.createOffer();
                    const autoPatchedOffers = this.buildPreferredH264ProfileOffers(autoRawOffer, 'publish-auto-offer');
                    const autoRawOfferWithBitrate = this.buildPublisherOfferWithBitrateParameters(autoRawOffer);
                    const autoPatchedWithBitrate = (autoPatchedOffers || [])
                        .map(o => this.buildPublisherOfferWithBitrateParameters(o));
                    const autoOffer = await this.setLocalDescriptionWithProfileFallback(
                        pc,
                        autoRawOfferWithBitrate,
                        autoPatchedWithBitrate,
                        'publish-auto-offer');

                    joinResult = await sendJoinAndConfigure(autoOffer, '', 'auto-no-videocodec');
                    requestedJanusCodec = this.getJanusVideoCodecForSdp(autoOffer?.sdp || '', previousPreferredCodec);
                    fallbackSelectedCodec = this.normalizeVideoCodec(requestedJanusCodec || previousPreferredCodec);
                    fallbackJoined = true;
                    this.log('publish', 'Auto fallback succeeded (joinandconfigure without videocodec)');
                } catch (autoErr) {
                    attemptErrors.push(`auto: ${autoErr.message}`);
                }
            }

            if (!fallbackJoined) {
                this.currentConstraints.preferredVideoCodec = previousPreferredCodec;
                this.logError(
                    'publish',
                    `Failed to join room (${primaryRequestedCodec} + fallbacks ${fallbackOrder.join(', ')}): ${attemptErrors.join(' | ')}`
                );
                return [];
            }

            if (fallbackSelectedCodec && fallbackSelectedCodec !== previousPreferredCodec) {
                this.currentConstraints.preferredVideoCodec = fallbackSelectedCodec;
                this.log('codec', `Publish fallback succeeded, locking codec to ${fallbackSelectedCodec.toUpperCase()} for this session`);
            }
        }

        // Mark handle as configured so future ICE candidates send immediately
        handleConfigured = true;

        // Apply answer SDP from Janus
        if (joinResult?.jsep) {
            await pc.setRemoteDescription(new RTCSessionDescription(joinResult.jsep));
            this.log('publish', 'Set remote SDP answer');
            this.logCodecSummary(pc, 'publish-answer');
            const remoteVideoCodec = this.getCodecFromSdp(joinResult.jsep.sdp || '', 'video');
            if (preferredJanusCodec === 'h265' && remoteVideoCodec !== 'H265' && remoteVideoCodec !== 'HEVC' && remoteVideoCodec !== 'H264') {
                this.log('codec', `Janus answered ${remoteVideoCodec} while H265/H264 are preferred; attempting codec reconfigure...`);
                await this.renegotiatePublisher('codec-enforce');
            } else if (preferredJanusCodec === 'h264' && remoteVideoCodec === 'VP8') {
                this.log('codec', 'Janus answered VP8 while H264 is preferred; attempting H264 reconfigure...');
                await this.renegotiatePublisher('codec-enforce');
            }
            await this.markPublisherConfigured('joinandconfigure response');
        } else {
            this.log('publish', 'joinandconfigure response had no JSEP, waiting for async JSEP via polling');
        }

        // Flush buffered ICE candidates now that handle is configured. These accumulate into a
        // single batch instead of one sequentially-awaited request each.
        for (const buffered of candidateBuffer) {
            this.queueTrickleCandidate(this.janusHandleId, buffered);
        }
        this.flushTrickleBatch(this.janusHandleId);
        this.log('publish', `Flushed ${candidateBuffer.length} buffered ICE candidates`);

        // Return existing publishers so init() can subscribe after polling starts
        const pluginData = joinResult?.plugindata?.data;
        if (pluginData) {
            if (pluginData.private_id) this.myPrivateId = pluginData.private_id;
            const pubs = pluginData.publishers;
            if (Array.isArray(pubs) && pubs.length > 0) {
                for (const pub of pubs) {
                    this.updatePublisherStreamHint(pub.id, pub.streams);
                }
                this.log('publish', `Found ${pubs.length} existing publisher(s)`);
                return pubs;
            }
        }
        this.log('publish', 'No existing publishers in room');
        return [];
    },

    isScreenAudioPublisherHandle(handleId) {
        return !!handleId && String(handleId) === String(this.screenAudioPublisherHandleId || '');
    },

    async ensureScreenAudioPublisher(screenAudioTrack) {
        const liveTrack = (screenAudioTrack && screenAudioTrack.readyState === 'live') ? screenAudioTrack : null;
        if (!liveTrack) {
            await this.stopScreenAudioPublisher('no-screen-audio-track');
            return false;
        }
        if (!this.janusSessionId || !this.janusRoomId) return false;

        if (this.screenAudioPublisherPc && this.screenAudioPublisherHandleId) {
            const sender = this.findSenderByKind(this.screenAudioPublisherPc, 'audio');
            if (!sender) return false;
            await sender.replaceTrack(liveTrack);
            this.log('screenshare', 'Updated separate screen-audio publisher track');
            return true;
        }

        let handleId = 0;
        try {
            const attachResult = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/attach`, {});
            handleId = attachResult?.handleId || 0;
            if (!handleId) {
                throw new Error('No screen-audio publisher handleId returned');
            }
        } catch (err) {
            this.log('screenshare', `Unable to attach screen-audio publisher handle: ${err.message}`);
            return false;
        }

        const pc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.screenAudioPublisherHandleId = handleId;
        this.screenAudioPublisherPc = pc;
        this.screenAudioPublisherConfigured = false;
        this.screenAudioPublisherPendingCandidates = [];

        pc.oniceconnectionstatechange = () => {
            this.log('screenshare', `Screen-audio publisher ICE state: ${pc.iceConnectionState}`);
        };
        pc.onconnectionstatechange = () => {
            this.log('screenshare', `Screen-audio publisher PC state: ${pc.connectionState}`);
        };

        pc.onicecandidate = ({ candidate }) => {
            if (!this.screenAudioPublisherConfigured) {
                this.screenAudioPublisherPendingCandidates.push(candidate);
                return;
            }
            this.queueTrickleCandidate(this.screenAudioPublisherHandleId, candidate);
        };

        pc.addTrack(liveTrack, new MediaStream([liveTrack]));

        try {
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            this.log('screenshare', 'Created screen-audio publisher offer, sending joinandconfigure...');

            const joinResult = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.screenAudioPublisherHandleId}/message-nowait`,
                {
                    body: {
                        request: 'joinandconfigure',
                        room: this.janusRoomId,
                        ptype: 'publisher',
                        display: this.buildPublisherDisplay('screen-audio')
                    },
                    jsep: { type: (offer?.type || 'offer'), sdp: offer.sdp }
                }
            );

            if (joinResult?.janus === 'error') {
                const janusError = joinResult?.error?.reason
                    || joinResult?.plugindata?.data?.error
                    || 'Janus returned error';
                throw new Error(janusError);
            }

            this.screenAudioPublisherConfigured = true;

            if (joinResult?.jsep) {
                await pc.setRemoteDescription(new RTCSessionDescription(joinResult.jsep));
                this.log('screenshare', 'Screen-audio publisher configured (joinandconfigure response)');
            } else {
                this.log('screenshare', 'Screen-audio publisher waiting for async JSEP via polling');
            }

            for (const buffered of this.screenAudioPublisherPendingCandidates) {
                this.queueTrickleCandidate(this.screenAudioPublisherHandleId, buffered);
            }
            this.flushTrickleBatch(this.screenAudioPublisherHandleId);
            this.screenAudioPublisherPendingCandidates = [];
            return true;
        } catch (err) {
            this.log('screenshare', `Failed to start screen-audio publisher: ${err.message}`);
            await this.stopScreenAudioPublisher('start-failed');
            return false;
        }
    },

    async stopScreenAudioPublisher(reason = '') {
        const handleId = this.screenAudioPublisherHandleId;
        const pc = this.screenAudioPublisherPc;
        if (!handleId && !pc) return;

        this.log('screenshare', `Stopping separate screen-audio publisher${reason ? ` (${reason})` : ''}`);

        this.screenAudioPublisherConfigured = false;
        this.screenAudioPublisherPendingCandidates = [];

        if (this.janusSessionId && handleId) {
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${handleId}/message-nowait`,
                    { body: { request: 'unpublish' } }
                );
            } catch { }
        }

        if (pc) {
            try { pc.close(); } catch { }
        }

        this.screenAudioPublisherPc = null;
        this.screenAudioPublisherHandleId = 0;
    },

    /**
     * Applies the desired bitrate to the publisher PeerConnection via
     * RTCRtpSender.setParameters() AND sends a Janus "configure" message
     * to inform the SFU of the intended bitrate.
     */
    async applyBitrate(bitrateKbps) {
        if (!bitrateKbps || bitrateKbps <= 0) return;
        const clampedBitrateKbps = this.clampPublisherBitrateKbps(bitrateKbps);
        const minBitrateKbps = this.getEffectiveMinPublisherBitrateKbps();
        const bitrateBps = clampedBitrateKbps * 1000;
        const minBitrateBps = Math.min(bitrateBps, minBitrateKbps * 1000);
        this.currentConstraints.bitrateKbps = clampedBitrateKbps;

        // 1. Set bitrate on local RTCRtpSender (WebRTC-level cap)
        if (this.publisherPc) {
            const senders = this.publisherPc.getSenders();
            for (const sender of senders) {
                if (!sender.track || sender.track.kind !== 'video') continue;
                try {
                    const params = sender.getParameters();
                    if (!params.encodings || params.encodings.length === 0) {
                        params.encodings = [{}];
                    }

                    // Skip the round trip when nothing would change. This is called from
                    // the screen-share warmup, from every settings apply and from each
                    // Janus configure, so the same values were being written over and over
                    // - eight times in the first seconds of a share. Every setParameters
                    // is a chance for the encoder's rate controller to be disturbed while
                    // it is still finding its operating point, which is the worst possible
                    // moment for it.
                    const encoding = params.encodings[0];
                    if (encoding.minBitrate === minBitrateBps && encoding.maxBitrate === bitrateBps) {
                        continue;
                    }

                    encoding.minBitrate = minBitrateBps;
                    encoding.maxBitrate = bitrateBps;
                    await sender.setParameters(params);
                    this.log('bitrate', `Set sender bitrate floor/cap to ${minBitrateKbps}/${clampedBitrateKbps} kbps`);
                } catch (err) {
                    this.log('bitrate', `setParameters failed: ${err.message}`);
                }
            }
        }

        // 2. Tell Janus SFU the desired bitrate (fire-and-forget)
        if (this.janusSessionId && this.janusHandleId) {
            if (!this.publisherConfigured) {
                this.pendingJanusBitrateBps = bitrateBps;
                this.log('bitrate', `Deferring Janus configure bitrate=${bitrateBps} bps until publisher is configured`);
                return;
            }
            await this.sendJanusBitrateConfigure(bitrateBps);
        }
    },

    /**
     * Renegotiates the publisher PeerConnection with Janus by creating a new
     * SDP offer and sending a 'configure' request via fire-and-forget.
     * The JSEP answer arrives via the polling loop (handled at the
     * "Re-negotiation JSEP received for publisher" block).
     * This avoids competing with the polling loop for Janus events.
     */
    async renegotiatePublisher(tag) {
        if (!this.publisherPc || !this.janusSessionId || !this.janusHandleId) {
            this.log(tag, 'Cannot renegotiate — no publisher PC or Janus session');
            return;
        }
        try {
            const offer = await this.createPublisherOfferWithCodecRecovery(this.publisherPc, tag);
            const requestedJanusCodec = this.getJanusVideoCodecForSdp(offer.sdp, this.currentConstraints.preferredVideoCodec);
            this.logCodecSummary(this.publisherPc, `${tag}-offer`);
            this.log(tag, `Created renegotiation SDP offer, sending configure (nowait, videocodec=${requestedJanusCodec})...`);

            // Use message-nowait so we don't compete with the polling loop.
            // The JSEP answer will arrive via the poll event handler.
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message-nowait`,
                {
                    body: {
                        request: 'configure',
                        videocodec: requestedJanusCodec
                    },
                    jsep: { type: (offer?.type || 'offer'), sdp: offer.sdp }
                }
            );
            this.log(tag, 'Configure sent — waiting for JSEP answer via polling loop');
        } catch (err) {
            this.logError(tag, `Renegotiation failed: ${err.message}`);
        }
    },

    // ─── Subscriber (two-phase: initiate → complete via polling) ────────────────

    /**
     * Phase 1: Attach a subscriber handle and send the join message
     * using the fire-and-forget endpoint (/message-nowait) so we do NOT
     * consume events from the Janus event queue.
     * The JSEP offer will arrive via the polling loop → completeSubscribe.
     */
    async initiateSubscribe(feedId, display, streamsHint) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        const feedKey = String(feedId);
        const preferVideoOnly = this.isNativeScreenDisplay(display);
        this.replaceDuplicateNativeScreenFeeds(feedId, display);
        const incomingStreams = this.normalizeSubscribeStreams(streamsHint, feedId);

        if (this.isRemoteFeedHidden(normalizedFeedId)) {
            this.rememberHiddenRemoteFeedMeta(normalizedFeedId, display || '', incomingStreams);
            this.log('subscribe', `Skip subscribe for hidden feed ${normalizedFeedId}`);
            return;
        }

        const inFlight = this.pendingSubscribeFeeds[feedKey];
        if (inFlight) {
            if (incomingStreams.length > 0) {
                inFlight.streams = this.normalizeSubscribeStreams(
                    [...(inFlight.streams || []), ...incomingStreams],
                    feedId
                );
            }
            if (display) inFlight.display = display;
            this.log('subscribe', `Skip duplicate subscribe for feed ${feedId}; handle attach already in-flight`);
            return;
        }
        this.pendingSubscribeFeeds[feedKey] = {
            streams: incomingStreams,
            display: display || '',
            preferVideoOnly
        };

        try {
            if (this.feeds[feedId]) return;
            const alreadyPending = this.findPendingSubscribeByFeed(feedId);
            if (alreadyPending) {
                const mergedStreams = this.normalizeSubscribeStreams(
                    [...(alreadyPending.pending.streams || []), ...(this.pendingSubscribeFeeds[feedKey]?.streams || incomingStreams)],
                    feedId
                );
                if (mergedStreams.length > 0) {
                    alreadyPending.pending.streams = mergedStreams;
                }
                if (display) {
                    alreadyPending.pending.display = display;
                }
                this.log('subscribe', `Skip duplicate subscribe for feed ${feedId}; already pending on handle ${alreadyPending.handleId}`);
                return;
            }
            this.log('subscribe', `Initiating subscribe to feed ${feedId} (${display || 'unknown'})`);
            this.maybeNotifyRemoteParticipant(display);

            // Attach a dedicated handle for this subscriber (synchronous Janus op)
            let subHandleId;
            try {
                const result = await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/attach`, {});
                subHandleId = result?.handleId;
                if (!subHandleId) throw new Error('No handleId returned');
                this.log('subscribe', `Attached subscriber handle ${subHandleId} for feed ${feedId}`);
            } catch (err) {
                this.logError('subscribe', `Failed to attach subscriber handle for feed ${feedId}: ${err.message}`);
                return;
            }

            // Track this pending subscribe so the polling loop can match the response
            let normalizedStreams = this.normalizeSubscribeStreams(
                this.pendingSubscribeFeeds[feedKey]?.streams || incomingStreams,
                feedId
            );
            const effectivePreferVideoOnly = this.pendingSubscribeFeeds[feedKey]?.preferVideoOnly === true;
            if (effectivePreferVideoOnly) {
                normalizedStreams = this.selectVideoPreferredSubscribeStreams(normalizedStreams, feedId);
            }
            const effectiveDisplay = display || this.pendingSubscribeFeeds[feedKey]?.display || '';
            this.pendingSubscribes[subHandleId] = {
                feedId,
                display: effectiveDisplay,
                preferVideoOnly: effectivePreferVideoOnly,
                createdAt: Date.now(),
                subscribeRequested: false,
                subscribeAttempts: 0,
                lastSubscribeRequestAt: 0,
                waitingForJsepUntil: 0,
                streams: normalizedStreams
            };

            // Send join message via fire-and-forget (no event queue competition!)
            try {
                const joinBody = {
                    request: 'join',
                    room: this.janusRoomId,
                    ptype: 'subscriber',
                    feed: feedId,
                    // Janus multistream expects selected streams; keep legacy feed for compatibility.
                    streams: normalizedStreams,
                    offer_audio: !effectivePreferVideoOnly,
                    offer_video: true,
                    offer_data: false
                };
                if (this.myPrivateId && this.myPrivateId > 0) {
                    joinBody.private_id = this.myPrivateId;
                } else {
                    this.log('subscribe', `No private_id available yet for feed ${feedId}; sending subscriber join without private_id`);
                }

                const joinResult = await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                    {
                        body: joinBody
                    }
                );
                this.log(
                    'subscribe',
                    `Subscriber join response for feed ${feedId} (handle ${subHandleId}): janus=${joinResult?.janus || 'n/a'}, videoroom=${joinResult?.plugindata?.data?.videoroom || 'n/a'}, hasJsep=${!!joinResult?.jsep}`
                );

                // Janus may return the subscriber offer inline instead of queueing it for poll.
                if (joinResult?.jsep) {
                    this.log('subscribe', `Subscriber join returned immediate JSEP for feed ${feedId} (handle ${subHandleId})`);
                    await this.completeSubscribe(subHandleId, joinResult.jsep);
                    return;
                }

                const joinData = joinResult?.plugindata?.data;
                if (joinData?.error || joinData?.error_code) {
                    delete this.pendingSubscribes[subHandleId];
                    this.logError(
                        'subscribe',
                        `Subscriber join failed for feed ${feedId} (handle ${subHandleId}): ${joinData.error || 'unknown'} (${joinData.error_code || 'n/a'})`
                    );
                    return;
                }

                // Janus multistream can acknowledge join without an SDP offer.
                // Give Janus a moment to emit attached+jsep; if it doesn't, nudge with subscribe.
                const pending = this.pendingSubscribes[subHandleId];
                if (pending) {
                    pending.waitingForJsepUntil = Date.now() + 2200;
                }
                this.schedulePendingSubscribeNudge(subHandleId, 2200, 'join-no-jsep-timeout');

                this.log('subscribe', `Sent subscriber join for feed ${feedId} (handle ${subHandleId}), awaiting JSEP via poll...`);
            } catch (err) {
                delete this.pendingSubscribes[subHandleId];
                this.logError('subscribe', `Subscriber join send failed for feed ${feedId}: ${err.message}`);
            }
        } finally {
            delete this.pendingSubscribeFeeds[feedKey];
        }
    },

    async requestSubscriberOffer(subHandleId, feedId, reason) {
        const pending = this.pendingSubscribes[subHandleId];
        if (!pending) return;
        const effectiveFeedId = pending.feedId || feedId;
        const preferVideoOnly = pending.preferVideoOnly === true;
        const now = Date.now();
        if (now < (pending.waitingForJsepUntil || 0)) return;
        if (pending.subscribeRequested && (now - (pending.lastSubscribeRequestAt || 0)) < 1200) return;
        if ((pending.subscribeAttempts || 0) >= 6) {
            this.logError('subscribe', `Subscriber ${reason}: giving up for feed ${effectiveFeedId} (handle ${subHandleId}) after ${pending.subscribeAttempts} attempts`);
            delete this.pendingSubscribes[subHandleId];
            return;
        }
        pending.subscribeRequested = true;
        pending.subscribeAttempts = (pending.subscribeAttempts || 0) + 1;
        pending.lastSubscribeRequestAt = now;
        pending.createdAt = now;
        let streams = this.normalizeSubscribeStreams(pending.streams, effectiveFeedId);
        if (preferVideoOnly) {
            streams = this.selectVideoPreferredSubscribeStreams(streams, effectiveFeedId);
        }
        pending.streams = streams;

        try {
            this.log('subscribe', `Subscriber ${reason}: sending subscribe attempt ${pending.subscribeAttempts} for feed ${effectiveFeedId} (handle ${subHandleId})`);
            const result = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                {
                    body: {
                        request: 'subscribe',
                        streams,
                        offer_audio: !preferVideoOnly,
                        offer_video: true,
                        offer_data: false
                    }
                }
            );

            if (result?.jsep) {
                this.log('subscribe', `Subscriber ${reason}: immediate JSEP for feed ${effectiveFeedId} (handle ${subHandleId})`);
                await this.completeSubscribe(subHandleId, result.jsep);
                return;
            }

            const data = result?.plugindata?.data;
            if (data?.error || data?.error_code) {
                // "subscribe" may be unsupported on older Janus API variants.
                pending.subscribeRequested = false;
                pending.waitingForJsepUntil = 0;
                this.log('subscribe', `Subscriber ${reason}: subscribe request returned ${data.error || 'unknown'} (${data.error_code || 'n/a'})`);
                return;
            }

            pending.subscribeRequested = false;
            pending.waitingForJsepUntil = Date.now() + 2600;
            this.log('subscribe', `Subscriber ${reason}: subscribe request sent for feed ${effectiveFeedId} (handle ${subHandleId}), waiting for poll JSEP`);
        } catch (err) {
            pending.subscribeRequested = false;
            pending.waitingForJsepUntil = 0;
            this.log('subscribe', `Subscriber ${reason}: subscribe request failed for feed ${effectiveFeedId}: ${err.message}`);
        }
    },

    /**
     * Phase 2: Called from the polling loop when the JSEP offer arrives
     * for a pending subscriber. Creates the PeerConnection, sets
     * remote/local descriptions, and sends the "start" command.
     */
    async completeSubscribe(subHandleId, jsep) {
        const pending = this.pendingSubscribes[subHandleId];
        if (!pending) return;
        delete this.pendingSubscribes[subHandleId];

        const { feedId, display } = pending;
        this.log('subscribe', `Completing subscribe for feed ${feedId} (handle ${subHandleId})`);

        const subPc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.feeds[feedId] = {
            handleId: subHandleId,
            pc: subPc,
            display,
            stream: null,
            preferVideoOnly: pending.preferVideoOnly === true
        };

        // Monitor subscriber connection state
        subPc.oniceconnectionstatechange = () => {
            const state = subPc.iceConnectionState;
            this.log('subscribe', `Feed ${feedId} ICE state: ${state}`);
            if (state === 'failed') {
                this.logError('subscribe', `Feed ${feedId} ICE FAILED — cannot receive their media`);
                this.scheduleFeedHardRecovery(feedId, 'ice-failed', 250);
            } else if (state === 'disconnected') {
                // 'disconnected' is not 'failed' - it is the state ICE sits in while consent
                // checks are still in flight, and it commonly returns to 'connected' on its
                // own. 4.5s was inside that window, so an ordinary blip was converted into a
                // teardown. ICE reaches 'failed' by itself when it really is gone, and that
                // path recovers in 250ms.
                this.scheduleFeedHardRecovery(feedId, 'ice-disconnected-timeout', 9000);
            } else if (state === 'connected' || state === 'completed') {
                this.cancelFeedHardRecovery(feedId);
            }
        };
        subPc.onconnectionstatechange = () => {
            const state = subPc.connectionState;
            this.log('subscribe', `Feed ${feedId} PC state: ${state}`);
            if (state === 'failed') {
                this.scheduleFeedHardRecovery(feedId, 'pc-failed', 250);
            } else if (state === 'disconnected') {
                this.scheduleFeedHardRecovery(feedId, 'pc-disconnected-timeout', 9000);
            } else if (state === 'connected') {
                this.cancelFeedHardRecovery(feedId);
            }
        };

        // Handle remote tracks
        subPc.ontrack = ({ streams, receiver, track }) => {
            this.log('subscribe', `ontrack fired for feed ${feedId}, streams: ${streams.length}`);
            this.tuneReceiverPlayout(receiver, track?.kind || '', `feed ${feedId}`);
            if (track?.kind === 'video') {
                this.enforceVideoReceiverPlayoutDelay(subPc, `feed ${feedId}`);
            }
            const feedState = this.feeds[feedId];
            const incomingPlaybackStream = this.normalizeRemotePlaybackStream(
                streams[0] || null,
                feedId,
                track || null
            );
            const streamForPlayback = this.mergeRemotePlaybackStreams(
                feedState?.stream || null,
                incomingPlaybackStream,
                feedId,
                track || null
            );
            if (feedState) {
                const now = Date.now();
                feedState.lastTrackAt = now;
                if (track?.kind === 'audio') feedState.lastAudioTrackAt = now;
                if (track?.kind === 'video') feedState.lastVideoTrackAt = now;
                feedState.display = display;
                feedState.stream = streamForPlayback || feedState.stream || null;
                const parsedDisplay = this.parsePublisherDisplay((display || '').toString());

                if (parsedDisplay.role === 'screen-audio' || parsedDisplay.role === 'native-screen') {
                    feedState.roleHint = 'stream';
                } else {
                    const trackLabel = (track?.label || '').toString();
                    if (/(screen|share|window|display|monitor|present|slides|application)/i.test(trackLabel)) {
                        feedState.roleHint = 'stream';
                    } else {
                        const identityKey = this.getFeedIdentityKey(feedId);
                        if (!feedState.roleHint && identityKey) {
                            const siblingFeeds = this.listIdentityFeeds(identityKey, false).filter(id => id !== String(feedId));
                            if (siblingFeeds.length > 0 && track?.kind === 'video') {
                                feedState.roleHint = 'stream';
                            }
                        }
                    }
                }
            }
            const cell = this.ensureRemoteFeedCell(feedId, display);
            if (cell) {
                const vid = cell.querySelector('video');
                this.configureRemoteVideoElement(vid, feedId);
                const currentPlaybackStream = vid?.srcObject || null;
                const currentHasLiveVideo = !!currentPlaybackStream?.getVideoTracks?.().some(t => t && t.readyState === 'live');
                const shouldReloadStream =
                    !!streamForPlayback
                    && (!currentPlaybackStream || currentPlaybackStream !== streamForPlayback)
                    && (track?.kind === 'video' || !currentHasLiveVideo);
                if (shouldReloadStream) {
                    vid.srcObject = streamForPlayback;
                    const playPromise = vid.play?.();
                    if (playPromise?.catch) {
                        playPromise.catch(err => this.log('subscribe', `Remote play() blocked for feed ${feedId}: ${err.message}`));
                    }
                } else if (track?.kind === 'video') {
                    this.tryResumeRemoteVideo(vid, feedId, 'video-track-arrived');
                }
                this.applyRemoteFeedVolume(feedId, vid);
            }
            if (streamForPlayback) {
                this.ensureRemoteSpeakingAnalyzer(feedId, streamForPlayback);
            } else if (feedState?.stream) {
                this.ensureRemoteSpeakingAnalyzer(feedId, feedState.stream);
            }
            this.refreshRemoteAudioRouting();
            this.refreshFloatContainerIfPresent();
        };

        // Trickle ICE for subscriber
        subPc.onicecandidate = ({ candidate }) => {
            this.queueTrickleCandidate(subHandleId, candidate);
        };

        // Set Janus's offer and create answer
        await subPc.setRemoteDescription(new RTCSessionDescription(jsep));
        this.tunePeerReceiversPlayout(subPc, `feed ${feedId}`);
        if (pending.preferVideoOnly === true) {
            this.enforceSubscriberVideoOnlyTransceivers(subPc, jsep?.sdp || '', `feed ${feedId}`);
        }
        const answer = await subPc.createAnswer();
        await subPc.setLocalDescription(answer);
        this.logCodecSummary(subPc, `subscribe-feed-${feedId}`);
        const remoteAudioCodec = this.getCodecFromSdp(jsep?.sdp || '', 'audio');
        const remoteVideoCodec = this.getCodecFromSdp(jsep?.sdp || '', 'video');
        const hint = this.publisherStreamHints[String(feedId)];
        const feedRole = this.getFeedRole(feedId);
        const likelyMicFeed = feedRole !== 'stream';
        const preferVideoOnly = pending.preferVideoOnly === true;
        if (remoteVideoCodec === 'none' && hint?.hasVideo) {
            this.log('subscribe', `Feed ${feedId} attached audio-only but publisher hints include video; scheduling subscriber refresh`);
            this.scheduleExistingSubscriberRefresh(feedId, 1500, 'attached-audio-only', { requireVideo: true });
        }
        if (!preferVideoOnly && remoteAudioCodec === 'none' && (hint?.hasAudio || likelyMicFeed)) {
            this.log('subscribe', `Feed ${feedId} attached without audio but publisher likely has audio; scheduling subscriber refresh`);
            this.scheduleExistingSubscriberRefresh(feedId, 1200, 'attached-video-only', {
                requireAudio: true,
                force: !hint || hint.hasAudio !== true
            });
        }

        // Send "start" with answer — fire-and-forget (no event competition)
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                {
                    body: { request: 'start', room: this.janusRoomId },
                    jsep: { type: (answer?.type || 'answer'), sdp: answer.sdp }
                }
            );
            this.log('subscribe', `Subscriber started for feed ${feedId}`);
        } catch (err) {
            this.logError('subscribe', `Subscriber start failed for feed ${feedId}: ${err.message}`);
        }

        // Recovery nudge for intermittent reconnect races where the mic m-line
        // is missing from the first subscriber offer despite an audio publisher.
        if (!preferVideoOnly && likelyMicFeed) {
            this.scheduleExistingSubscriberRefresh(feedId, 2000, 'post-subscribe-audio-check', {
                requireAudio: true,
                force: true
            });
        }
    },

    /**
     * Handles a JSEP re-offer for an existing subscriber handle (already attached).
     * This happens when a remote publisher changes streams (e.g. starts/stops screen share).
     */
    async renegotiateExistingSubscriber(subHandleId, jsep) {
        if (!subHandleId || !jsep) return false;

        const feedEntry = Object.entries(this.feeds).find(([, value]) => value?.handleId == subHandleId);
        if (!feedEntry) return false;

        const [feedId, feed] = feedEntry;
        if (!feed?.pc) return false;

        this.log('subscribe', `Renegotiating existing subscriber feed ${feedId} (handle ${subHandleId})`);

        try {
            await feed.pc.setRemoteDescription(new RTCSessionDescription(jsep));
            this.tunePeerReceiversPlayout(feed.pc, `feed ${feedId} renegotiate`);
            if (feed.preferVideoOnly === true) {
                this.enforceSubscriberVideoOnlyTransceivers(feed.pc, jsep?.sdp || '', `feed ${feedId} renegotiate`);
            }
            const answer = await feed.pc.createAnswer();
            await feed.pc.setLocalDescription(answer);
            this.logCodecSummary(feed.pc, `subscribe-renegotiate-feed-${feedId}`);

            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                {
                    body: { request: 'start', room: this.janusRoomId },
                    jsep: { type: (answer?.type || 'answer'), sdp: answer.sdp }
                }
            );

            this.log('subscribe', `Subscriber renegotiation completed for feed ${feedId}`);
            return true;
        } catch (err) {
            this.logError('subscribe', `Subscriber renegotiation failed for feed ${feedId}: ${err.message}`);
            return true;
        }
    },

    removeFeed(feedId) {
        const streamKey = `remote-${feedId}`;
        this.removeRemoteSpeakingAnalyzer(feedId);
        const removedIdentityKey = this.getFeedIdentityKey(feedId);
        const feed = this.feeds[feedId];
        if (feed) {
            if (feed.hardRecoveryTimer) {
                clearTimeout(feed.hardRecoveryTimer);
                feed.hardRecoveryTimer = null;
            }
            feed.pc.close();
            this.clearTrickleBatch(feed.handleId);
            delete this.pendingSubscribes[feed.handleId];
            delete this.feeds[feedId];
        }
        delete this.openStreamInfoPanels[streamKey];
        this.clearStreamStatsCache(streamKey);
        this.stopStreamInfoLoopIfIdle();
        if (this.selectedSpotlightStreamKey === streamKey) {
            this.selectedSpotlightStreamKey = null;
        }
        delete this.remoteFeedVolumes[feedId];
        delete this.hiddenRemoteFeeds[feedId];
        delete this.hiddenRemoteFeedMeta[feedId];
        const el = document.getElementById('remote-' + feedId);
        if (el) {
            this.clearCellOverlayHideTimer(el);
            const vid = el.querySelector('video');
            if (vid) {
                this.detachRemoteFeedElementGain(vid);
                try { vid.pause?.(); } catch { }
                try { vid.srcObject = null; } catch { }
            }
            el.remove();
        }

        if (removedIdentityKey) {
            const remainingIdentityFeeds = this.listIdentityFeeds(removedIdentityKey, false);
            if (remainingIdentityFeeds.length > 0) {
                for (const remainingFeedId of remainingIdentityFeeds) {
                    const remainingCell = document.getElementById('remote-' + remainingFeedId);
                    if (remainingCell) {
                        this.ensureRemoteFeedVolumeControl(remainingCell, remainingFeedId);
                    }
                }
            }
        }

        const floatVideo = document.getElementById('floatLocalVideo');
        if (floatVideo && floatVideo.dataset.feedId === String(feedId)) {
            this.detachRemoteFeedElementGain(floatVideo);
            try { floatVideo.pause?.(); } catch { }
            try { floatVideo.srcObject = null; } catch { }
            delete floatVideo.dataset.feedId;
            floatVideo.muted = true;
            floatVideo.volume = 0;
        }

        this.applySpotlightLayout();
        this.refreshRemoteAudioRouting();
        this.refreshFloatContainerIfPresent();
        this.log('subscribe', `Removed feed ${feedId}`);
    },

    getNativeScreenFeedId(userId) {
        const normalized = this.normalizeUserId(userId);
        return `native-screen-${normalized || userId}`;
    },

    parseNativeScreenPayload(payload) {
        if (!payload) return null;
        if (typeof payload === 'object') return payload;
        if (typeof payload !== 'string') return null;
        try {
            return JSON.parse(payload);
        } catch {
            return null;
        }
    },

    sendNativeScreenSignal(targetUserId, signalType, payloadObj) {
        try {
            const payloadJson = JSON.stringify(payloadObj || {});
            this.notifyDotNet('OnNativeScreenSignal', String(targetUserId || ''), String(signalType || ''), payloadJson);
        } catch (err) {
            this.logError('native-screen', `Failed to send native signal ${signalType}: ${err.message}`);
        }
    },

    cleanupNativeScreenReceiver(userId, removeCell = true) {
        const key = this.normalizeUserId(userId);
        if (!key) return;

        const existing = this.nativeScreenReceivers[key];
        if (existing?.pc) {
            try { existing.pc.onicecandidate = null; } catch { }
            try { existing.pc.ontrack = null; } catch { }
            try { existing.pc.close(); } catch { }
        }
        delete this.nativeScreenReceivers[key];
        delete this.nativeScreenPendingCandidates[key];

        if (removeCell) {
            const feedId = this.getNativeScreenFeedId(key);
            this.removeFeed(feedId);
        }
    },

    async handleNativeScreenSignal(fromUserId, signalType, payload) {
        const key = this.normalizeUserId(fromUserId);
        if (!key) return;

        const parsed = this.parseNativeScreenPayload(payload);
        const normalizedType = (signalType || '').toString().trim().toLowerCase();

        if (normalizedType === 'native-screen-stop') {
            this.cleanupNativeScreenReceiver(key, true);
            return;
        }

        if (normalizedType === 'native-screen-offer') {
            const sdp = parsed?.sdp || parsed?.Sdp;
            if (!sdp) return;

            this.cleanupNativeScreenReceiver(key, true);

            const pc = new RTCPeerConnection({ iceServers: this.iceServers });
            this.nativeScreenReceivers[key] = { pc, stream: null };

            pc.onicecandidate = (event) => {
                const c = event?.candidate;
                if (!c || !c.candidate) return;
                this.sendNativeScreenSignal(key, 'native-screen-candidate', {
                    mid: c.sdpMid || '',
                    candidate: c.candidate
                });
            };

            pc.ontrack = (event) => {
                let stream = event?.streams?.[0] || null;
                if (!stream && event?.track) {
                    stream = new MediaStream([event.track]);
                }
                if (!stream) return;

                const displayName = this.getParticipantDisplayName(key) || key;
                const feedId = this.getNativeScreenFeedId(key);
                const cell = this.ensureRemoteFeedCell(feedId, `Screen • ${displayName}`);
                if (!cell) return;

                const vid = cell.querySelector('video');
                if (!vid) return;
                this.configureRemoteVideoElement(vid, feedId);
                vid.srcObject = stream;
                vid.play?.().catch(() => { });
                this.nativeScreenReceivers[key].stream = stream;
            };

            pc.onconnectionstatechange = () => {
                const state = pc.connectionState;
                if (state === 'failed' || state === 'closed') {
                    this.cleanupNativeScreenReceiver(key, true);
                }
            };

            await pc.setRemoteDescription({ type: 'offer', sdp });
            const pending = this.nativeScreenPendingCandidates[key];
            if (Array.isArray(pending) && pending.length > 0) {
                for (const queued of pending) {
                    const cnd = queued?.candidate || '';
                    const mid = queued?.mid || null;
                    if (!cnd) continue;
                    try {
                        await pc.addIceCandidate({ candidate: cnd, sdpMid: mid });
                    } catch (err) {
                        this.log('native-screen', `Queued candidate add failed for ${key}: ${err.message}`);
                    }
                }
            }
            delete this.nativeScreenPendingCandidates[key];

            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);

            this.sendNativeScreenSignal(key, 'native-screen-answer', {
                type: 'answer',
                sdp: answer?.sdp || ''
            });
            return;
        }

        if (normalizedType === 'native-screen-candidate') {
            const entry = this.nativeScreenReceivers[key];
            const candidate = parsed?.candidate || parsed?.Candidate;
            const mid = parsed?.mid || parsed?.Mid || '';
            if (!candidate) return;

            if (!entry?.pc) {
                if (!Array.isArray(this.nativeScreenPendingCandidates[key])) {
                    this.nativeScreenPendingCandidates[key] = [];
                }
                this.nativeScreenPendingCandidates[key].push({ candidate, mid });
                return;
            }

            try {
                await entry.pc.addIceCandidate({ candidate, sdpMid: mid || null });
            } catch (err) {
                this.log('native-screen', `Ignoring candidate add failure for ${key}: ${err.message}`);
            }
        }
    },

    ensureRemoteFeedCell(feedId, display) {
        const videoGrid = document.getElementById('videoGrid');
        if (!videoGrid) return null;
        this.ensureVideoPauseWatchdog();
        const streamKey = `remote-${feedId}`;

        let cell = document.getElementById('remote-' + feedId);
        if (cell) {
            cell.dataset.feedId = String(feedId);
            cell.dataset.display = (display || '').toString();
            cell.classList.toggle('remote-audio-only-feed', this.isFeedTaggedScreenAudio(feedId));
            this.bindSpotlightInteraction(cell, streamKey, true);
            this.bindCellOverlayAutoHide(cell);
            const existingVideo = cell.querySelector('video');
            this.configureRemoteVideoElement(existingVideo, feedId);
            const label = cell.querySelector('.video-label');
            if (label) {
                label.textContent = this.getRemoteFeedDisplayName(feedId, display);
            }
            this.ensureRemoteFeedAvatarFallback(cell, feedId, display);
            const controls = cell.querySelector('.video-controls');
            this.ensureSpotlightButton(controls, streamKey);
            this.ensureRemoteFeedVolumeControl(cell, feedId);
            this.ensureRemoteFeedVisibilityControl(cell, feedId);
            this.applyRemoteFeedVisibilityState(cell, feedId);
            this.refreshIdentityVolumeControls(this.getFeedIdentityKey(feedId));
            this.applySpotlightLayout();
            return cell;
        }

        cell = document.createElement('div');
        cell.className = 'video-cell';
        cell.id = 'remote-' + feedId;
        cell.dataset.feedId = String(feedId);
        cell.dataset.display = (display || '').toString();
        cell.classList.toggle('remote-audio-only-feed', this.isFeedTaggedScreenAudio(feedId));
        this.bindSpotlightInteraction(cell, streamKey, true);
        this.bindCellOverlayAutoHide(cell);

        const vid = document.createElement('video');
        this.configureRemoteVideoElement(vid, feedId);
        // A feed that joins after the output device was chosen must honour it too.
        this.applySelectedSinkTo(vid);
        vid.dataset.feedId = String(feedId);

        const label = document.createElement('span');
        label.className = 'video-label';
        label.textContent = this.getRemoteFeedDisplayName(feedId, display);

        const controls = document.createElement('div');
        controls.className = 'video-controls';
        this.ensureSpotlightButton(controls, streamKey);

        const infoBtn = document.createElement('button');
        infoBtn.type = 'button';
        infoBtn.className = 'video-control-btn video-info-btn';
        infoBtn.textContent = 'Info';
        infoBtn.addEventListener('click', (ev) => {
            ev.stopPropagation();
            this.toggleStreamInfoPanel(streamKey);
        });

        const fullscreenBtn = document.createElement('button');
        fullscreenBtn.type = 'button';
        fullscreenBtn.className = 'video-control-btn';
        fullscreenBtn.textContent = 'Fullscreen';
        fullscreenBtn.addEventListener('click', () => this.toggleRemoteFullscreen(feedId));
        controls.append(infoBtn, fullscreenBtn);

        vid.addEventListener('dblclick', () => this.toggleRemoteFullscreen(feedId));

        const infoPanel = this.createStreamInfoPanel(streamKey);
        cell.append(vid, label, controls, infoPanel);
        this.ensureRemoteFeedAvatarFallback(cell, feedId, display);
        this.ensureRemoteFeedVolumeControl(cell, feedId);
        this.ensureRemoteFeedVisibilityControl(cell, feedId);
        this.applyRemoteFeedVisibilityState(cell, feedId);
        videoGrid.appendChild(cell);

        const initialVolume = this.getFeedVolumePercent(feedId);
        this.setRemoteFeedVolume(feedId, initialVolume);
        this.refreshIdentityVolumeControls(this.getFeedIdentityKey(feedId));
        this.applySpotlightLayout();
        return cell;
    },

    canToggleRemoteFeedVisibility(feedId) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return false;
        if (this.hiddenRemoteFeeds[normalizedFeedId]) return true;

        const feed = this.feeds[normalizedFeedId];
        if (!feed) return false;
        if (this.feedHasLiveTrack(feed, 'video')) return true;
        return this.getFeedRole(normalizedFeedId) === 'stream';
    },

    rememberHiddenRemoteFeedMeta(feedId, display, streamsHint) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;

        const previous = this.hiddenRemoteFeedMeta[normalizedFeedId] || {};
        const currentFeed = this.feeds[normalizedFeedId] || null;
        const effectiveDisplay = (display || previous.display || '').toString();
        const displayRole = this.getDisplayRole(effectiveDisplay);
        const keywordStreamLike = /(screen|share|window|display|monitor|present|slides|application)/i.test(effectiveDisplay);
        const streamLikeFromLiveFeed = !!currentFeed && (
            this.feedHasLiveTrack(currentFeed, 'video')
            || this.isFeedTaggedScreenAudio(normalizedFeedId)
            || this.getFeedRole(normalizedFeedId) === 'stream'
            || this.feedLooksLikeStreamShare(normalizedFeedId)
        );
        const streamLike = streamLikeFromLiveFeed
            || previous.streamLike === true
            || displayRole === 'screen-audio'
            || displayRole === 'native-screen'
            || keywordStreamLike;
        const identityKey = this.getFeedIdentityKey(normalizedFeedId)
            || previous.identityKey
            || this.getDisplayIdentityKey(effectiveDisplay)
            || '';
        const mergedStreams = this.normalizeSubscribeStreams(
            [...(previous.streams || []), ...(streamsHint || [])],
            normalizedFeedId
        );
        this.hiddenRemoteFeedMeta[normalizedFeedId] = {
            display: effectiveDisplay,
            streams: mergedStreams,
            identityKey,
            streamLike
        };
    },

    cancelPendingSubscribeForFeed(feedId, reason = 'cancelled') {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;

        for (const [handleId, pending] of Object.entries(this.pendingSubscribes || {})) {
            if (!pending || this.normalizeFeedId(pending.feedId) !== normalizedFeedId) continue;
            delete this.pendingSubscribes[handleId];
            this.log('subscribe', `Cancelled pending subscribe for feed ${normalizedFeedId} (handle ${handleId}, reason=${reason})`);
        }

        if (this.pendingSubscribeFeeds[normalizedFeedId]) {
            delete this.pendingSubscribeFeeds[normalizedFeedId];
        }
    },

    suspendRemoteFeedSubscription(feedId, reason = 'hidden-preference') {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;
        this.removeRemoteSpeakingAnalyzer(normalizedFeedId);

        const feed = this.feeds[normalizedFeedId];
        if (!feed) {
            this.cancelPendingSubscribeForFeed(normalizedFeedId, reason);
            return;
        }

        this.rememberHiddenRemoteFeedMeta(normalizedFeedId, feed.display || '', this.publisherStreamHints[normalizedFeedId]?.streams || []);
        this.cancelPendingSubscribeForFeed(normalizedFeedId, reason);

        if (feed.hardRecoveryTimer) {
            clearTimeout(feed.hardRecoveryTimer);
            feed.hardRecoveryTimer = null;
        }

        try { feed.pc.onicecandidate = null; } catch { }
        try { feed.pc.ontrack = null; } catch { }
        try { feed.pc.close(); } catch { }
        delete this.pendingSubscribes[feed.handleId];
        delete this.feeds[normalizedFeedId];
        const streamKey = `remote-${normalizedFeedId}`;
        delete this.openStreamInfoPanels[streamKey];
        this.clearStreamStatsCache(streamKey);
        this.stopStreamInfoLoopIfIdle();

        const cell = document.getElementById('remote-' + normalizedFeedId);
        if (cell) {
            const video = cell.querySelector('video');
            if (video) {
                this.detachRemoteFeedElementGain(video);
                try { video.pause?.(); } catch { }
                try { video.srcObject = null; } catch { }
            }
            this.applyRemoteFeedVisibilityState(cell, normalizedFeedId);
        }

        const floatVideo = document.getElementById('floatLocalVideo');
        if (floatVideo && floatVideo.dataset.feedId === String(normalizedFeedId)) {
            this.detachRemoteFeedElementGain(floatVideo);
            try { floatVideo.pause?.(); } catch { }
            try { floatVideo.srcObject = null; } catch { }
            delete floatVideo.dataset.feedId;
            floatVideo.muted = true;
            floatVideo.volume = 0;
        }

        this.refreshRemoteAudioRouting();
        this.refreshFloatContainerIfPresent();
        this.log('subscribe', `Unsubscribed from feed ${normalizedFeedId} (reason=${reason})`);
    },

    requestHiddenFeedResubscribe(feedId, reason = 'show-preference') {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;
        if (this.hiddenRemoteFeeds[normalizedFeedId]) return;
        if (this.feeds[normalizedFeedId]) return;

        const pending = this.findPendingSubscribeByFeed(normalizedFeedId);
        if (pending) return;

        const meta = this.hiddenRemoteFeedMeta[normalizedFeedId] || {};
        const subscribeFeedId = Number.isFinite(Number(normalizedFeedId)) ? Number(normalizedFeedId) : normalizedFeedId;
        this.log('subscribe', `Re-subscribing feed ${normalizedFeedId} after unhide (reason=${reason})`);
        this.initiateSubscribe(subscribeFeedId, meta.display || '', meta.streams || [])
            .catch(err => this.logError('subscribe', `Failed to re-subscribe feed ${normalizedFeedId}: ${err.message}`));
    },

    isRemoteFeedHidden(feedId) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        return !!(normalizedFeedId && this.hiddenRemoteFeeds[normalizedFeedId]);
    },

    setRemoteFeedHidden(feedId, hidden) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;

        const nextHidden = !!hidden;
        const targetFeed = this.feeds[normalizedFeedId] || null;
        const targetMeta = this.hiddenRemoteFeedMeta[normalizedFeedId] || {};
        const targetDisplay = (targetFeed?.display || targetMeta.display || '').toString();
        const targetDisplayRole = this.getDisplayRole(targetDisplay);
        const targetKeywordStreamLike = /(screen|share|window|display|monitor|present|slides|application)/i.test(targetDisplay);
        const targetIsStreamLike = (!!targetFeed && (
            this.feedHasLiveTrack(targetFeed, 'video')
            || this.isFeedTaggedScreenAudio(normalizedFeedId)
            || this.getFeedRole(normalizedFeedId) === 'stream'
            || this.feedLooksLikeStreamShare(normalizedFeedId)
        )) || targetMeta.streamLike === true
            || targetDisplayRole === 'screen-audio'
            || targetDisplayRole === 'native-screen'
            || targetKeywordStreamLike;

        const affectedFeedIds = new Set([normalizedFeedId]);
        const identityKey = this.getFeedIdentityKey(normalizedFeedId)
            || targetMeta.identityKey
            || this.getDisplayIdentityKey(targetDisplay);
        if (identityKey && targetIsStreamLike) {
            const siblingCandidates = new Set(this.listIdentityFeeds(identityKey, false).map(id => this.normalizeFeedId(id)));
            for (const [candidateFeedId, candidateMeta] of Object.entries(this.hiddenRemoteFeedMeta || {})) {
                const normalizedCandidate = this.normalizeFeedId(candidateFeedId);
                if (!normalizedCandidate) continue;
                const candidateIdentityKey = this.getFeedIdentityKey(normalizedCandidate)
                    || candidateMeta?.identityKey
                    || this.getDisplayIdentityKey(candidateMeta?.display || '');
                if (candidateIdentityKey === identityKey) {
                    siblingCandidates.add(normalizedCandidate);
                }
            }

            for (const siblingId of siblingCandidates) {
                const normalizedSibling = this.normalizeFeedId(siblingId);
                if (!normalizedSibling || normalizedSibling === normalizedFeedId) continue;

                const siblingFeed = this.feeds[normalizedSibling] || null;
                const siblingMeta = this.hiddenRemoteFeedMeta[normalizedSibling] || {};
                const siblingDisplay = (siblingFeed?.display || siblingMeta.display || '').toString();
                const siblingDisplayRole = this.getDisplayRole(siblingDisplay);
                const siblingKeywordStreamLike = /(screen|share|window|display|monitor|present|slides|application)/i.test(siblingDisplay);
                const siblingIsStreamLike = (!!siblingFeed && (
                    this.isFeedTaggedScreenAudio(normalizedSibling)
                    || this.feedHasLiveTrack(siblingFeed, 'video')
                    || this.getFeedRole(normalizedSibling) === 'stream'
                    || this.feedLooksLikeStreamShare(normalizedSibling)
                )) || siblingMeta.streamLike === true
                    || siblingDisplayRole === 'screen-audio'
                    || siblingDisplayRole === 'native-screen'
                    || siblingKeywordStreamLike;

                if (siblingIsStreamLike) {
                    affectedFeedIds.add(normalizedSibling);
                }
            }
        }

        let changed = false;
        for (const affectedId of affectedFeedIds) {
            const currentHidden = !!this.hiddenRemoteFeeds[affectedId];
            if (nextHidden === currentHidden) continue;
            changed = true;
            if (nextHidden) {
                this.hiddenRemoteFeeds[affectedId] = true;
            } else {
                delete this.hiddenRemoteFeeds[affectedId];
            }
        }
        if (!changed) return;

        for (const affectedId of affectedFeedIds) {
            if (nextHidden) {
                this.suspendRemoteFeedSubscription(affectedId, 'hidden-preference');
            } else {
                this.requestHiddenFeedResubscribe(affectedId, 'show-preference');
            }
        }

        for (const affectedId of affectedFeedIds) {
            const cell = document.getElementById('remote-' + affectedId);
            if (cell) {
                this.applyRemoteFeedVisibilityState(cell, affectedId);
            }
        }
        if (identityKey) {
            this.refreshIdentityVolumeControls(identityKey);
        }

        this.refreshRemoteAudioRouting();
        this.refreshFloatContainerIfPresent();
        const affectedList = Array.from(affectedFeedIds.values()).join(',');
        this.log('subscribe', `Feed ${normalizedFeedId} ${nextHidden ? 'hidden' : 'shown'} by local preference (affected: ${affectedList})`);
    },

    toggleRemoteFeedHidden(feedId) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;
        this.setRemoteFeedHidden(normalizedFeedId, !this.isRemoteFeedHidden(normalizedFeedId));
    },

    ensureRemoteFeedVisibilityControl(cell, feedId) {
        if (!cell) return;
        const controls = cell.querySelector('.video-controls');
        if (!controls) return;

        const normalizedFeedId = this.normalizeFeedId(feedId);
        const canToggle = this.canToggleRemoteFeedVisibility(normalizedFeedId);
        let button = controls.querySelector('.video-visibility-btn');

        if (!canToggle) {
            if (button) button.remove();
            cell.classList.remove('video-cell-stream-hidden');
            return;
        }

        if (!button) {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'video-control-btn video-visibility-btn';
            button.addEventListener('click', (ev) => {
                ev.stopPropagation();
                const targetFeedId = ev.currentTarget?.dataset?.feedId || normalizedFeedId;
                this.toggleRemoteFeedHidden(targetFeedId);
            });
            controls.insertBefore(button, controls.firstChild || null);
        }

        button.dataset.feedId = String(normalizedFeedId);
        const hidden = this.isRemoteFeedHidden(normalizedFeedId);
        button.textContent = hidden ? 'Show' : 'Hide';
        button.classList.toggle('active', hidden);
    },

    applyRemoteFeedVisibilityState(cell, feedId) {
        if (!cell) return;
        const normalizedFeedId = this.normalizeFeedId(feedId);
        const hidden = this.isRemoteFeedHidden(normalizedFeedId);
        cell.classList.toggle('video-cell-stream-hidden', hidden);

        const video = cell.querySelector('video');
        if (video) {
            video.style.visibility = hidden ? 'hidden' : '';
            video.style.opacity = hidden ? '0' : '';
            video.style.pointerEvents = hidden ? 'none' : '';
            if (!hidden) {
                const volumePercent = this.getFeedVolumePercent(normalizedFeedId);
                this.applyRemoteFeedVolumeToElement(video, normalizedFeedId, volumePercent);
                this.tryResumeRemoteVideo(video, normalizedFeedId, 'show-hidden-stream');
            }
        }

        this.ensureRemoteFeedVisibilityControl(cell, normalizedFeedId);
    },

    refreshIdentityVolumeControls(identityKey) {
        if (!identityKey) return;
        const feedIds = this.listIdentityFeeds(identityKey, false);
        for (const identityFeedId of feedIds) {
            const feedCell = document.getElementById('remote-' + identityFeedId);
            if (feedCell) {
                this.ensureRemoteFeedVolumeControl(feedCell, identityFeedId);
            }
        }
    },

    /// Speaker toggle that sits in front of a per-stream volume slider.
    ///
    /// Mute is represented by the slider's own value being 0 rather than a separate
    /// flag, so the button, the slider and what is actually applied cannot disagree,
    /// and dragging away from zero is itself an unmute. The level to come back to is
    /// remembered on the button.
    buildVolumeMuteButton(slider, applyValue) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'video-volume-mute-btn';
        button._volumeBeforeMute = 100;

        const sync = () => {
            const muted = Number(slider.value) <= 0;
            button.classList.toggle('muted', muted);
            button.setAttribute('aria-pressed', muted ? 'true' : 'false');
            button.setAttribute('aria-label', muted ? 'Unmute' : 'Mute');
            button.title = muted ? 'Unmute' : 'Mute';
            // Inline SVG rather than a font glyph: this is built outside Blazor, so it
            // cannot reach the MudBlazor icon set the rest of the UI uses.
            button.innerHTML = muted
                ? '<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" aria-hidden="true"><path d="M16.5 12A4.5 4.5 0 0 0 14 7.97v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51A8.8 8.8 0 0 0 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06a8.99 8.99 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"/></svg>'
                : '<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" aria-hidden="true"><path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"/></svg>';
        };

        button.addEventListener('click', (ev) => {
            ev.stopPropagation();
            const current = Number(slider.value) || 0;
            if (current > 0) {
                button._volumeBeforeMute = current;
                slider.value = '0';
            } else {
                // 100 when they were already silent, so unmuting is never a no-op.
                slider.value = String(button._volumeBeforeMute > 0 ? button._volumeBeforeMute : 100);
            }

            applyValue(slider.value);
            sync();
            this.flushPersistedRemoteIdentityVolumes();
        });

        slider.addEventListener('input', () => {
            const value = Number(slider.value) || 0;
            if (value > 0) {
                button._volumeBeforeMute = value;
            }
            sync();
        });

        // Exposed so the reconcile pass below can refresh the icon after writing the
        // slider's value straight, which does not raise an input event.
        button.syncMuteState = sync;

        sync();
        return button;
    },

    ensureRemoteFeedVolumeControl(cell, feedId) {
        if (!cell) return;
        const normalizedFeedId = this.normalizeFeedId(feedId);

        let volumePanel = cell.querySelector('.remote-volume-panel');
        if (!volumePanel) {
            volumePanel = document.createElement('div');
            volumePanel.className = 'remote-volume-panel';

            const micRow = document.createElement('div');
            micRow.className = 'remote-volume-row remote-volume-row-mic';

            const micCaption = document.createElement('span');
            micCaption.className = 'remote-volume-caption';
            micCaption.textContent = 'Mic';

            const micSlider = document.createElement('input');
            micSlider.type = 'range';
            micSlider.min = '0';
            micSlider.max = '200';
            micSlider.step = '1';
            micSlider.value = '100';
            micSlider.className = 'video-volume-slider video-volume-slider-mic';
            const applyMicVolume = (value) => {
                const targetFeedId = volumePanel.dataset.feedId || normalizedFeedId;
                const identityKey = this.getFeedIdentityKey(targetFeedId);
                if (identityKey) {
                    this.setIdentityRoleVolume(identityKey, 'mic', value, { skipPanel: volumePanel });
                } else {
                    this.setRemoteFeedVolume(targetFeedId, value);
                }
                micValue.textContent = `${value}%`;
            };
            micSlider.addEventListener('input', () => applyMicVolume(micSlider.value));
            const micMuteButton = this.buildVolumeMuteButton(micSlider, applyMicVolume);
            // Commit the debounced write as soon as the drag ends.
            micSlider.addEventListener('change', () => this.flushPersistedRemoteIdentityVolumes());

            const micValue = document.createElement('span');
            micValue.className = 'video-volume-label video-volume-label-mic';
            micValue.textContent = '100%';

            const streamRow = document.createElement('div');
            streamRow.className = 'remote-volume-row remote-volume-row-stream';

            const streamCaption = document.createElement('span');
            streamCaption.className = 'remote-volume-caption';
            streamCaption.textContent = 'Stream';

            const streamSlider = document.createElement('input');
            streamSlider.type = 'range';
            streamSlider.min = '0';
            streamSlider.max = '200';
            streamSlider.step = '1';
            streamSlider.value = '100';
            streamSlider.className = 'video-volume-slider video-volume-slider-stream';
            const applyStreamVolume = (value) => {
                const targetFeedId = volumePanel.dataset.feedId || normalizedFeedId;
                const identityKey = this.getFeedIdentityKey(targetFeedId);
                if (identityKey) {
                    this.setIdentityRoleVolume(identityKey, 'stream', value, { skipPanel: volumePanel });
                } else {
                    this.setRemoteFeedVolume(targetFeedId, value);
                }
                streamValue.textContent = `${value}%`;
            };
            streamSlider.addEventListener('input', () => applyStreamVolume(streamSlider.value));
            const streamMuteButton = this.buildVolumeMuteButton(streamSlider, applyStreamVolume);
            // Commit the debounced write as soon as the drag ends.
            streamSlider.addEventListener('change', () => this.flushPersistedRemoteIdentityVolumes());

            const streamValue = document.createElement('span');
            streamValue.className = 'video-volume-label video-volume-label-stream';
            streamValue.textContent = '100%';

            // Prevent tile click/double-click handlers from stealing interaction.
            const stopPropagation = (ev) => ev.stopPropagation();
            volumePanel.addEventListener('click', stopPropagation);
            volumePanel.addEventListener('dblclick', stopPropagation);
            volumePanel.addEventListener('mousedown', stopPropagation);
            volumePanel.addEventListener('pointerdown', stopPropagation);

            micRow.append(micCaption, micMuteButton, micSlider, micValue);
            streamRow.append(streamCaption, streamMuteButton, streamSlider, streamValue);
            volumePanel.append(micRow, streamRow);
            cell.appendChild(volumePanel);
        }

        volumePanel.dataset.feedId = String(normalizedFeedId);
        const identityKey = this.getFeedIdentityKey(normalizedFeedId);
        const micVolume = identityKey
            ? this.getIdentityRoleVolumePercent(identityKey, 'mic')
            : this.getFeedVolumePercent(normalizedFeedId);
        const streamVolume = identityKey
            ? this.getIdentityRoleVolumePercent(identityKey, 'stream')
            : micVolume;
        const streamVisible = !!identityKey && this.isStreamVolumeSliderVisible(identityKey);

        const micSlider = volumePanel.querySelector('.video-volume-slider-mic');
        const micLabel = volumePanel.querySelector('.video-volume-label-mic');
        if (micSlider) micSlider.value = String(micVolume);
        if (micLabel) micLabel.textContent = `${micVolume}%`;

        const streamRow = volumePanel.querySelector('.remote-volume-row-stream');
        if (streamRow) streamRow.hidden = !streamVisible;
        const streamSlider = volumePanel.querySelector('.video-volume-slider-stream');
        const streamLabel = volumePanel.querySelector('.video-volume-label-stream');
        if (streamSlider) streamSlider.value = String(streamVolume);
        if (streamLabel) streamLabel.textContent = `${streamVolume}%`;

        // The writes above bypass the input event the buttons listen to, so a feed that
        // reconnects at 0% would otherwise come back showing an unmuted speaker.
        volumePanel.querySelectorAll('.video-volume-mute-btn')
            .forEach(button => button.syncMuteState?.());
    },

    getRemoteFeedDisplayName(feedId, display) {
        if (display && typeof display === 'string') {
            const parsed = this.parsePublisherDisplay(display);
            if (parsed.displayName) {
                return parsed.displayName;
            }
            if (parsed.userId) {
                const mapped = this.getParticipantDisplayName(parsed.userId);
                if (mapped) return mapped;
                return `User ${parsed.userId.slice(0, 8)}`;
            }
        }
        return 'User ' + String(feedId).slice(0, 8);
    },

    toggleRemoteFullscreen(feedId) {
        const cell = document.getElementById('remote-' + feedId);
        if (!cell) return;

        const isThisFullscreen = document.fullscreenElement === cell;
        if (isThisFullscreen) {
            document.exitFullscreen?.().catch(() => { });
            if (this._desktopFullscreenRequested) {
                this.notifyHostDesktopFullscreen('exit');
                this._desktopFullscreenRequested = false;
            }
            return;
        }

        const enterDesktop = () => {
            if (this.notifyHostDesktopFullscreen('enter')) {
                this._desktopFullscreenRequested = true;
            }
        };

        const enterCell = () => {
            if (!cell.requestFullscreen) {
                enterDesktop();
                return;
            }
            cell.requestFullscreen()
                .then(() => {
                    enterDesktop();
                })
                .catch(() => { });
        };

        if (document.fullscreenElement && document.fullscreenElement !== cell) {
            const exitCurrent = document.exitFullscreen?.bind(document);
            if (exitCurrent) {
                exitCurrent()
                    .then(() => enterCell())
                    .catch(() => { });
            } else {
                enterCell();
            }
            return;
        }

        enterCell();
    },

    resolveFeedIdFromVideoElement(videoElement) {
        if (!videoElement) return '';
        const fromDataset = (videoElement.dataset?.feedId || '').toString();
        if (fromDataset) return fromDataset;

        const cell = videoElement.closest?.('.video-cell[id^="remote-"]');
        if (!cell) return '';
        const feedFromData = (cell.dataset?.feedId || '').toString();
        if (feedFromData) return feedFromData;
        const cellId = (cell.id || '').toString();
        return cellId.startsWith('remote-') ? cellId.substring('remote-'.length) : '';
    },

    normalizeFeedId(feedId) {
        return (feedId ?? '').toString();
    },

    // ─── Pass-scoped memo for derived feed state ────────────────────────────────
    //
    // getFeedRole/listIdentityFeeds/feedLooksLikeStreamShare are mutually recursive across
    // siblings, so a single routing pass re-derives the same values O(N^2)-to-O(N^3) times.
    // These values also depend on live track state, which changes without any of our code
    // running — so the memo is deliberately scoped to one synchronous pass rather than cached
    // across time. Nested passes share the outermost memo via the depth counter.

    _feedDerivedMemo: null,
    _feedDerivedMemoDepth: 0,

    withFeedDerivedMemo(fn) {
        if (this._feedDerivedMemoDepth === 0) {
            this._feedDerivedMemo = {
                identity: new Map(),
                role: new Map(),
                streamShare: new Map(),
                liveAudio: new Map(),
                identityFeeds: new Map()
            };
        }
        this._feedDerivedMemoDepth++;
        try {
            return fn();
        } finally {
            this._feedDerivedMemoDepth--;
            if (this._feedDerivedMemoDepth <= 0) {
                this._feedDerivedMemoDepth = 0;
                this._feedDerivedMemo = null;
            }
        }
    },

    getFeedIdentityKey(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return '';
        const feed = this.feeds[key];
        if (!feed) return '';

        const memo = this._feedDerivedMemo?.identity;
        if (memo?.has(key)) return memo.get(key);
        const computed = this.computeFeedIdentityKey(key, feed);
        memo?.set(key, computed);
        return computed;
    },

    computeFeedIdentityKey(key, feed) {
        const parsed = this.parsePublisherDisplay((feed.display || '').toString());
        if (parsed.userId) {
            const normalizedUser = this.normalizeUserId(parsed.userId);
            if (normalizedUser) return `uid:${normalizedUser}`;
        }
        if (parsed.displayName) {
            const normalizedDisplay = parsed.displayName.trim().toLowerCase();
            if (normalizedDisplay) return `name:${normalizedDisplay}`;
        }
        const normalizedRaw = (feed.display || '').toString().trim().toLowerCase();
        return normalizedRaw ? `raw:${normalizedRaw}` : '';
    },

    isFeedTaggedScreenAudio(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return false;
        const feed = this.feeds[key];
        if (!feed) return false;
        const parsed = this.parsePublisherDisplay((feed.display || '').toString());
        return parsed.role === 'screen-audio';
    },

    feedHasLiveTrack(feed, kind) {
        if (!feed) return false;

        // Audio liveness is the hot one — it is probed for every sibling of every feed.
        const memo = kind === 'audio' ? this._feedDerivedMemo?.liveAudio : null;
        if (memo) {
            const cached = memo.get(feed);
            if (cached !== undefined) return cached;
            const computed = this.computeFeedHasLiveTrack(feed, kind);
            memo.set(feed, computed);
            return computed;
        }
        return this.computeFeedHasLiveTrack(feed, kind);
    },

    computeFeedHasLiveTrack(feed, kind) {
        const liveFromStream = kind === 'audio'
            ? feed.stream?.getAudioTracks?.().some(t => t && t.readyState === 'live')
            : feed.stream?.getVideoTracks?.().some(t => t && t.readyState === 'live');
        if (liveFromStream) return true;

        const receivers = feed.pc?.getReceivers?.() || [];
        return receivers.some(r => r?.track?.kind === kind && r.track.readyState === 'live');
    },

    listIdentityFeeds(identityKey, requireLiveAudio = false) {
        if (!identityKey) return [];

        const memo = this._feedDerivedMemo?.identityFeeds;
        const memoKey = requireLiveAudio ? `live:${identityKey}` : `all:${identityKey}`;
        if (memo?.has(memoKey)) return memo.get(memoKey);
        const computed = this.computeIdentityFeeds(identityKey, requireLiveAudio);
        memo?.set(memoKey, computed);
        return computed;
    },

    computeIdentityFeeds(identityKey, requireLiveAudio) {
        const feeds = [];
        for (const [feedId, feed] of Object.entries(this.feeds)) {
            if (!feed) continue;
            if (this.getFeedIdentityKey(feedId) !== identityKey) continue;
            if (requireLiveAudio && !this.feedHasLiveTrack(feed, 'audio')) continue;
            feeds.push(String(feedId));
        }
        return feeds;
    },

    feedLooksLikeStreamShare(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return false;
        const feed = this.feeds[key];
        if (!feed) return false;

        const memo = this._feedDerivedMemo?.streamShare;
        if (memo?.has(key)) return memo.get(key);
        const computed = this.computeFeedLooksLikeStreamShare(key, feed);
        memo?.set(key, computed);
        return computed;
    },

    computeFeedLooksLikeStreamShare(key, feed) {
        if (feed.roleHint === 'stream') return true;
        if (feed.roleHint === 'mic') return false;

        const keywordRegex = /(screen|share|window|display|monitor|present|slides|application)/i;
        const textCandidates = [];
        textCandidates.push((feed.display || '').toString());

        const hint = this.publisherStreamHints[key];
        if (hint?.raw && Array.isArray(hint.raw)) {
            for (const streamInfo of hint.raw) {
                try {
                    textCandidates.push(JSON.stringify(streamInfo));
                } catch { }
            }
        }

        const feedStream = feed.stream;
        if (feedStream && typeof feedStream.getTracks === 'function') {
            for (const track of feedStream.getTracks()) {
                textCandidates.push((track?.label || '').toString());
            }
        }

        const receivers = feed.pc?.getReceivers?.() || [];
        for (const receiver of receivers) {
            textCandidates.push((receiver?.track?.label || '').toString());
        }

        for (const text of textCandidates) {
            if (!text) continue;
            if (keywordRegex.test(text)) {
                return true;
            }
        }

        return false;
    },

    getFeedRole(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return 'mic';
        const feed = this.feeds[key];
        if (!feed) return 'mic';

        const memo = this._feedDerivedMemo?.role;
        if (memo?.has(key)) return memo.get(key);
        // Written only after computing, so a re-entrant call recomputes rather than reading a
        // half-built value.
        const computed = this.computeFeedRole(key, feed);
        memo?.set(key, computed);
        return computed;
    },

    computeFeedRole(key, feed) {
        const parsedDisplay = this.parsePublisherDisplay((feed.display || '').toString());
        if (parsedDisplay.role === 'screen-audio' || parsedDisplay.role === 'native-screen') {
            return 'stream';
        }

        const identityKey = this.getFeedIdentityKey(key);
        if (parsedDisplay.role === 'primary' && identityKey) {
            const hasDedicatedScreenAudioSibling = this.listIdentityFeeds(identityKey, true).some(otherFeedId => {
                const otherKey = String(otherFeedId);
                if (otherKey === key) return false;
                return this.isFeedTaggedScreenAudio(otherKey);
            });
            if (hasDedicatedScreenAudioSibling) {
                return 'mic';
            }
        }

        if (feed.roleHint === 'mic' || feed.roleHint === 'stream') {
            return feed.roleHint;
        }

        if (!identityKey) return 'mic';

        const identityAudioFeeds = this.listIdentityFeeds(identityKey, true);
        if (identityAudioFeeds.length <= 1) {
            return 'mic';
        }

        const streamCandidates = identityAudioFeeds.filter(fId => this.feedLooksLikeStreamShare(fId));
        if (streamCandidates.length > 0) {
            return streamCandidates.includes(key) ? 'stream' : 'mic';
        }

        const primaryFeedId = this.resolvePrimaryAudibleFeedForIdentity(identityKey);
        if (primaryFeedId && primaryFeedId !== key) {
            return 'stream';
        }

        return 'mic';
    },

    getIdentityVolumeState(identityKey) {
        if (!identityKey) {
            return { mic: 100, stream: 100 };
        }
        const existing = this.remoteIdentityVolumes[identityKey];
        if (existing && typeof existing === 'object') {
            const parsedMic = parseInt(existing.mic, 10);
            const parsedStream = parseInt(existing.stream, 10);
            return {
                mic: Math.max(0, Math.min(200, Number.isFinite(parsedMic) ? parsedMic : 100)),
                stream: Math.max(0, Math.min(200, Number.isFinite(parsedStream) ? parsedStream : 100))
            };
        }
        const next = { mic: 100, stream: 100 };
        this.remoteIdentityVolumes[identityKey] = next;
        return next;
    },

    getFeedVolumePercent(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return 100;

        const identityKey = this.getFeedIdentityKey(key);
        if (!identityKey) {
            return this.remoteFeedVolumes[key] ?? 100;
        }
        const state = this.getIdentityVolumeState(identityKey);
        if (this.isFeedTaggedScreenAudio(key)) {
            return state.stream;
        }
        const role = this.getFeedRole(key);
        if (role === 'stream') {
            return state.stream;
        }
        return state.mic;
    },

    getIdentityRoleVolumePercent(identityKey, role) {
        const state = this.getIdentityVolumeState(identityKey);
        return role === 'stream' ? state.stream : state.mic;
    },

    identityHasStreamAudioFeed(identityKey) {
        if (!identityKey) return false;
        const identityAudioFeeds = this.listIdentityFeeds(identityKey, true);
        return identityAudioFeeds.some(feedId =>
            this.getFeedRole(feedId) === 'stream'
            || this.isFeedTaggedScreenAudio(feedId)
            || this.feedLooksLikeStreamShare(feedId));
    },

    isStreamVolumeSliderVisible(identityKey) {
        if (!identityKey) return false;
        if (!this.identityHasStreamAudioFeed(identityKey)) return false;

        const identityFeeds = this.listIdentityFeeds(identityKey, false);
        const streamFeeds = identityFeeds.filter(feedId => {
            const feed = this.feeds[String(feedId)];
            if (!feed || !this.feedHasLiveTrack(feed, 'audio')) return false;
            return this.getFeedRole(feedId) === 'stream'
                || this.isFeedTaggedScreenAudio(feedId)
                || this.feedLooksLikeStreamShare(feedId);
        });
        if (streamFeeds.length === 0) return false;

        const hiddenStreamByFeed = streamFeeds.some(feedId => this.isRemoteFeedHidden(feedId));
        if (hiddenStreamByFeed) return false;

        const hiddenStreamByVideoSibling = identityFeeds.some(feedId => {
            if (!this.isRemoteFeedHidden(feedId)) return false;
            const feed = this.feeds[String(feedId)];
            return !!feed && this.feedHasLiveTrack(feed, 'video');
        });
        return !hiddenStreamByVideoSibling;
    },

    resolvePrimaryAudibleFeedForIdentity(identityKey, role = null) {
        if (!identityKey) return '';

        const candidates = [];
        for (const [feedId, feed] of Object.entries(this.feeds)) {
            if (!feed) continue;
            if (this.getFeedIdentityKey(feedId) !== identityKey) continue;
            if (!this.feedHasLiveTrack(feed, 'audio')) continue;
            if (role === 'mic' || role === 'stream') {
                if (this.getFeedRole(feedId) !== role) continue;
            }

            candidates.push({
                feedId: String(feedId),
                hasVideo: this.feedHasLiveTrack(feed, 'video'),
                activity: feed.lastAudioTrackAt || feed.lastTrackAt || 0
            });
        }

        if (candidates.length === 0) return '';
        if (candidates.length === 1) return candidates[0].feedId;

        const withVideo = candidates.filter(c => c.hasVideo);
        const selectionPool = withVideo.length > 0 ? withVideo : candidates;
        selectionPool.sort((a, b) => {
            if (a.activity !== b.activity) return b.activity - a.activity;
            return Number(b.feedId) - Number(a.feedId);
        });
        return selectionPool[0].feedId;
    },

    isFeedAudioSuppressedByDuplicateRouting(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return false;
        const feed = this.feeds[key];
        if (!feed) return false;
        if (!this.feedHasLiveTrack(feed, 'audio')) return false;

        const identityKey = this.getFeedIdentityKey(key);
        if (!identityKey) return false;

        const role = this.getFeedRole(key);
        const primaryFeedId = this.resolvePrimaryAudibleFeedForIdentity(identityKey, role);
        return !!primaryFeedId && primaryFeedId !== key;
    },

    isFeedAudioSuppressedByVisibilityPreference(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return false;

        const feed = this.feeds[key];
        if (!feed) return false;
        if (!this.feedHasLiveTrack(feed, 'audio')) return false;

        const role = this.getFeedRole(key);
        const hasVideoTrack = this.feedHasLiveTrack(feed, 'video');
        const looksLikeStream = this.feedLooksLikeStreamShare(key);
        const taggedScreenAudio = this.isFeedTaggedScreenAudio(key);
        const likelyScreenAudio = taggedScreenAudio || (role === 'stream' && !hasVideoTrack && looksLikeStream);

        // Direct hide of this specific feed only suppresses dedicated/likely screen-audio tracks.
        if (this.isRemoteFeedHidden(key)) {
            return likelyScreenAudio;
        }

        const identityKey = this.getFeedIdentityKey(key);
        if (!identityKey) return false;

        // If any sibling feed is hidden, always suppress dedicated screen-audio feeds.
        // This keeps Hide behavior intuitive when stream audio is published on a separate feed.
        if (taggedScreenAudio) {
            const hiddenSiblingExists = this.listIdentityFeeds(identityKey, false).some(otherFeedId => {
                const normalizedOther = this.normalizeFeedId(otherFeedId);
                if (!normalizedOther || normalizedOther === key) return false;
                return this.isRemoteFeedHidden(normalizedOther);
            });
            if (hiddenSiblingExists) return true;
        }

        // If any stream/video sibling is hidden, suppress sibling screen-audio tracks.
        const hiddenStreamSiblingExists = this.listIdentityFeeds(identityKey, false).some(otherFeedId => {
            if (!this.isRemoteFeedHidden(otherFeedId)) return false;
            const other = this.feeds[String(otherFeedId)];
            if (!other) return false;
            if (this.isFeedTaggedScreenAudio(otherFeedId)) return true;
            if (this.feedHasLiveTrack(other, 'video')) return true;
            return this.getFeedRole(otherFeedId) === 'stream' || this.feedLooksLikeStreamShare(otherFeedId);
        });
        if (!hiddenStreamSiblingExists) return false;

        return likelyScreenAudio;
    },

    syncVolumeAcrossIdentity(feedId, volumePercent) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return;

        const identityKey = this.getFeedIdentityKey(key);
        if (!identityKey) return;

        const role = (this.isFeedTaggedScreenAudio(key) || this.getFeedRole(key) === 'stream')
            ? 'stream'
            : 'mic';
        this.setIdentityRoleVolume(identityKey, role, volumePercent);
    },

    setIdentityRoleVolume(identityKey, role, value, options = null) {
        if (!identityKey) return;

        // Slider `input` fires at pointer rate; one memo for the whole update keeps the
        // sibling role/identity derivation from being redone per feed.
        this.withFeedDerivedMemo(() => {
            const normalizedRole = role === 'stream' ? 'stream' : 'mic';
            const volumePercent = Math.max(0, Math.min(200, parseInt(value, 10) || 0));
            const volumeState = this.getIdentityVolumeState(identityKey);

            if (normalizedRole === 'stream') {
                volumeState.stream = volumePercent;
            } else {
                volumeState.mic = volumePercent;
                // Keep stream tied to mic unless stream-specific volume is available.
                if (!this.identityHasStreamAudioFeed(identityKey)) {
                    volumeState.stream = volumePercent;
                }
            }
            this.remoteIdentityVolumes[identityKey] = volumeState;
            this.schedulePersistedRemoteIdentityVolumes();

            for (const [otherFeedId] of Object.entries(this.feeds)) {
                const normalizedOther = String(otherFeedId);
                if (this.getFeedIdentityKey(normalizedOther) !== identityKey) continue;

                const otherRole = this.isFeedTaggedScreenAudio(normalizedOther)
                    ? 'stream'
                    : this.getFeedRole(normalizedOther);
                this.remoteFeedVolumes[normalizedOther] =
                    otherRole === 'stream' ? volumeState.stream : volumeState.mic;
            }

            const skipPanel = options?.skipPanel || null;
            for (const [otherFeedId] of Object.entries(this.feeds)) {
                const normalizedOther = String(otherFeedId);
                if (this.getFeedIdentityKey(normalizedOther) !== identityKey) continue;

                const otherCell = document.getElementById('remote-' + normalizedOther);
                if (!otherCell) continue;
                // Don't rewrite the panel the user is currently dragging — it costs a full
                // control rebuild per pointer sample and fights the in-progress drag.
                if (!skipPanel || !otherCell.contains(skipPanel)) {
                    this.ensureRemoteFeedVolumeControl(otherCell, normalizedOther);
                }

                const otherVideo = otherCell.querySelector('video');
                if (otherVideo) {
                    this.applyRemoteFeedVolumeToElement(otherVideo, normalizedOther, this.getFeedVolumePercent(normalizedOther));
                }
            }
        });
        this.scheduleRemoteAudioRoutingRefresh();
    },

    /**
     * Coalesce routing refreshes onto one frame. Bursts of feed churn (a join storm) and
     * pointer-rate slider input otherwise trigger a full document sweep per event.
     */
    scheduleRemoteAudioRoutingRefresh() {
        if (this._remoteAudioRoutingRafId || this._remoteAudioRoutingTimerId) return;
        this._remoteAudioRoutingRafId = requestAnimationFrame(() => {
            this._remoteAudioRoutingRafId = 0;
            this.refreshRemoteAudioRouting();
        });
        // rAF is paused while the window is hidden — this makes sure a queued refresh still
        // lands. Whichever fires first cancels the other.
        this._remoteAudioRoutingTimerId = setTimeout(() => {
            this._remoteAudioRoutingTimerId = 0;
            this.refreshRemoteAudioRouting();
        }, 100);
    },

    cancelScheduledRemoteAudioRoutingRefresh() {
        if (this._remoteAudioRoutingRafId) {
            cancelAnimationFrame(this._remoteAudioRoutingRafId);
            this._remoteAudioRoutingRafId = 0;
        }
        if (this._remoteAudioRoutingTimerId) {
            clearTimeout(this._remoteAudioRoutingTimerId);
            this._remoteAudioRoutingTimerId = 0;
        }
    },

    refreshRemoteAudioRouting() {
        this.cancelScheduledRemoteAudioRoutingRefresh();
        this.withFeedDerivedMemo(() => {
            document.querySelectorAll('.video-cell[id^="remote-"] video')
                .forEach(vid => {
                    const feedId = this.resolveFeedIdFromVideoElement(vid);
                    if (!feedId) return;
                    const volumePercent = this.getFeedVolumePercent(feedId);
                    this.applyRemoteFeedVolumeToElement(vid, feedId, volumePercent);
                    const cell = vid.closest('.video-cell');
                    if (cell) {
                        this.ensureRemoteFeedAvatarFallback(cell, feedId, cell.dataset.display || '');
                    }
                });
        });
    },

    isSilentFloatPreviewVideo(videoElement) {
        return !!videoElement && videoElement.id === 'floatLocalVideo';
    },

    setElementAudioTrackEnabled(videoElement, enabled) {
        if (!videoElement) return;
        const stream = videoElement.srcObject;
        if (!stream || typeof stream.getAudioTracks !== 'function') return;
        const nextEnabled = !!enabled;
        for (const track of stream.getAudioTracks()) {
            if (!track) continue;
            try {
                track.enabled = nextEnabled;
            } catch { }
        }
    },

    applyRemoteFeedVolumeToElement(videoElement, feedId, volumePercent) {
        if (!videoElement) return;
        if (this.isSilentFloatPreviewVideo(videoElement)) {
            this.setElementAudioTrackEnabled(videoElement, false);
            videoElement.muted = true;
            videoElement.volume = 0;
            this.setRemoteFeedElementGain(videoElement, 0);
            return;
        }

        const normalizedFeedId = this.normalizeFeedId(feedId);
        const feed = normalizedFeedId ? this.feeds[normalizedFeedId] : null;
        const forceElementVolume = !!normalizedFeedId && (
            this.isFeedTaggedScreenAudio(normalizedFeedId)
            || (!!feed && !this.feedHasLiveTrack(feed, 'video') && this.getFeedRole(normalizedFeedId) === 'stream')
        );

        if (normalizedFeedId && this.isFeedAudioSuppressedByVisibilityPreference(normalizedFeedId)) {
            this.setElementAudioTrackEnabled(videoElement, false);
            videoElement.muted = true;
            videoElement.volume = 0;
            if (forceElementVolume) {
                this.detachRemoteFeedElementGain(videoElement);
            } else {
                this.setRemoteFeedElementGain(videoElement, 0);
            }
            return;
        }

        if (normalizedFeedId && this.isFeedAudioSuppressedByDuplicateRouting(normalizedFeedId)) {
            this.setElementAudioTrackEnabled(videoElement, false);
            videoElement.muted = true;
            videoElement.volume = 0;
            if (forceElementVolume) {
                this.detachRemoteFeedElementGain(videoElement);
            } else {
                this.setRemoteFeedElementGain(videoElement, 0);
            }
            return;
        }

        const clampedPercent = Math.max(0, Math.min(200, parseInt(volumePercent, 10) || 0));
        const linearGain = clampedPercent / 100;
        const streamGain = Math.max(0, Math.min(2, Number(this.streamVolume) || 0));
        const effectiveGain = Math.max(0, Math.min(2, linearGain * streamGain));

        if (this.screenShareSystemAudioSuppressed && this.isScreenSharing) {
            this.setElementAudioTrackEnabled(videoElement, false);
            videoElement.muted = true;
            videoElement.volume = 0;
            if (forceElementVolume) {
                this.detachRemoteFeedElementGain(videoElement);
            } else {
                this.setRemoteFeedElementGain(videoElement, 0);
            }
            return;
        }

        if (forceElementVolume) {
            this.detachRemoteFeedElementGain(videoElement);
            const shouldMute = effectiveGain <= 0.0001;
            this.setElementAudioTrackEnabled(videoElement, !shouldMute);
            videoElement.muted = shouldMute;
            videoElement.volume = shouldMute ? 0 : Math.min(1, effectiveGain);
            return;
        }

        this.setElementAudioTrackEnabled(videoElement, effectiveGain > 0.0001);
        const gainApplied = this.setRemoteFeedElementGain(videoElement, effectiveGain);
        if (gainApplied) {
            // Stream-source gain active: keep element muted and drive loudness from WebAudio.
            videoElement.muted = true;
            videoElement.volume = 1;
            return;
        }

        // Fallback path if WebAudio gain creation fails.
        videoElement.muted = false;
        videoElement.volume = Math.min(1, effectiveGain);
    },

    applySystemScreenSharePlaybackState(videoElement) {
        if (!videoElement) return;
        const feedId = this.resolveFeedIdFromVideoElement(videoElement);
        if (!feedId) return;
        const volumePercent = this.getFeedVolumePercent(feedId);
        this.applyRemoteFeedVolumeToElement(videoElement, feedId, volumePercent);
    },

    setSystemScreenShareAppPlaybackSuppressed(enabled) {
        const next = !!enabled;
        if (this.screenShareSystemAudioSuppressed === next) return;
        this.screenShareSystemAudioSuppressed = next;

        this.refreshRemoteAudioRouting();

        this.log(
            'screenshare',
            next
                ? 'Suppressed in-app playback during system share to avoid chat-audio recapture'
                : 'Restored in-app playback after system share'
        );
    },

    setRemoteFeedVolume(feedId, value) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;
        const volumePercent = Math.max(0, Math.min(200, parseInt(value, 10) || 0));

        this.remoteFeedVolumes[normalizedFeedId] = volumePercent;
        this.syncVolumeAcrossIdentity(normalizedFeedId, volumePercent);

        const cell = document.getElementById('remote-' + normalizedFeedId);
        if (cell) {
            const vid = cell.querySelector('video');
            if (vid) {
                this.applyRemoteFeedVolumeToElement(vid, normalizedFeedId, this.getFeedVolumePercent(normalizedFeedId));
            }
            this.ensureRemoteFeedVolumeControl(cell, normalizedFeedId);
        }

        this.refreshRemoteAudioRouting();
    },

    applyRemoteFeedVolume(feedId, videoElement) {
        if (!videoElement) return;
        const normalizedFeedId = this.normalizeFeedId(feedId);
        const volumePercent = this.getFeedVolumePercent(normalizedFeedId);
        this.applyRemoteFeedVolumeToElement(videoElement, normalizedFeedId, volumePercent);
    },

    // ─── Per-user volume by userId (drawer context menu) ─────────────────────
    // Identity keys are `uid:<userId>` whenever the publisher display carries a
    // user id, which is how the drawer knows participants — so the menu can
    // address a user's mic volume without knowing any feed ids. Works even
    // before the user's feed lands: the identity state is read at feed attach.

    getUserMicVolume(userId) {
        const normalized = this.normalizeUserId(userId || '');
        if (!normalized) return 100;
        return this.getIdentityRoleVolumePercent(`uid:${normalized}`, 'mic');
    },

    setUserMicVolume(userId, value) {
        const normalized = this.normalizeUserId(userId || '');
        if (!normalized) return;
        // setIdentityRoleVolume owns the whole pipeline: state, persistence,
        // propagation to every live feed of the identity, tile panel refresh,
        // and the routing sweep.
        this.setIdentityRoleVolume(`uid:${normalized}`, 'mic', value);
    },

    // ─── Hide tiles of participants without video ────────────────────────────
    // Purely visual: cells already carry `video-cell-no-video` (kept current on
    // track changes), so a class on the grid lets CSS drop them. Audio is
    // untouched — this deliberately does NOT go through the per-feed hide
    // preference, which suppresses the hidden feed's audio.

    hideNoVideoTiles: false,

    setHideNoVideoTiles(enabled) {
        this.hideNoVideoTiles = !!enabled;
        this.applyHideNoVideoTiles();
    },

    applyHideNoVideoTiles() {
        const grid = document.getElementById('videoGrid');
        if (!grid) return;
        grid.classList.toggle('hide-no-video-cells', !!this.hideNoVideoTiles);
    },

    // ─── Paused-video detection ──────────────────────────────────────────────
    // A minimized captured window keeps its track alive but stops producing
    // frames, so the tile freezes or goes black while everything reports
    // "streaming". Each tile gets a frame watchdog: requestVideoFrameCallback
    // stamps the last delivered frame, and a 1s sweep marks any tile whose
    // live track has gone quiet as paused. The next frame clears it, which is
    // the whole recovery story — nothing to renegotiate.

    videoPauseWatchdogTimerId: 0,
    videoPauseStallMilliseconds: 2000,

    ensureVideoPauseWatchdog() {
        if (this.videoPauseWatchdogTimerId) return;
        if (typeof HTMLVideoElement === 'undefined'
            || !HTMLVideoElement.prototype.requestVideoFrameCallback) {
            return; // No rVFC (very old runtime): keep old behavior rather than guess.
        }

        this.videoPauseWatchdogTimerId = setInterval(() => {
            try {
                this.sweepVideoPauseStates();
            } catch { }
        }, 1000);
    },

    sweepVideoPauseStates() {
        const videos = document.querySelectorAll('.video-cell video');
        if (videos.length === 0) return;

        const now = performance.now();
        for (const video of videos) {
            const cell = video.closest('.video-cell');
            if (!cell) continue;

            if (!video.dataset.pauseWatch) {
                video.dataset.pauseWatch = '1';
                video._lastFrameTs = now;
                const arm = () => {
                    try {
                        video.requestVideoFrameCallback(() => {
                            video._lastFrameTs = performance.now();
                            // Recover immediately on the first frame instead of
                            // waiting for the next sweep tick.
                            const liveCell = video.closest('.video-cell');
                            if (liveCell) this.setCellPausedState(liveCell, false);
                            arm();
                        });
                    } catch { }
                };
                arm();
            }

            // The avatar fallback owns the "no track at all" case; paused is only
            // meaningful while a live track exists but delivers nothing.
            const hasLiveTrack = (video.srcObject?.getVideoTracks?.() || [])
                .some(t => t && t.readyState === 'live');
            const suppressedByFallback = cell.classList.contains('video-cell-no-video');
            const stalled = hasLiveTrack
                && !suppressedByFallback
                && (now - (video._lastFrameTs || 0)) > this.videoPauseStallMilliseconds;

            this.setCellPausedState(cell, stalled);
        }
    },

    setCellPausedState(cell, paused) {
        if (!cell) return;
        const isPaused = cell.classList.contains('video-cell-paused');
        if (paused === isPaused) return;

        cell.classList.toggle('video-cell-paused', paused);
        if (paused && !cell.querySelector('.video-paused-overlay')) {
            const overlay = document.createElement('div');
            overlay.className = 'video-paused-overlay';
            overlay.innerHTML =
                '<span class="material-icons" aria-hidden="true">pause_circle</span>'
                + '<span>Video paused</span>';
            cell.appendChild(overlay);
        }

        const feedId = cell.dataset?.feedId || (cell.classList.contains('local-cell') ? 'local' : '?');
        this.log('screenshare', `Feed ${feedId} video ${paused ? 'paused (no frames from source)' : 'resumed'}`);
    },

    getPrimaryRemoteFeedForFloat() {
        if (this.selectedSpotlightStreamKey) {
            if (this.selectedSpotlightStreamKey === 'local') {
                const localSpotlight = this.isScreenSharing && this.screenStream ? this.screenStream : this.localStream;
                if (localSpotlight) {
                    return { feedId: 'local', stream: localSpotlight, isLocal: true };
                }
            } else if (this.selectedSpotlightStreamKey.startsWith('remote-')) {
                const spotlightFeedId = this.selectedSpotlightStreamKey.substring('remote-'.length);
                const spotlightFeed = this.feeds[spotlightFeedId];
                if (
                    spotlightFeed &&
                    !this.isRemoteFeedHidden(spotlightFeedId) &&
                    !this.isFeedTaggedScreenAudio(spotlightFeedId) &&
                    this.feedHasLiveTrack(spotlightFeed, 'video')
                ) {
                    let spotlightStream = spotlightFeed.stream || null;
                    if (!spotlightStream && spotlightFeed.pc) {
                        const tracks = spotlightFeed.pc.getReceivers()
                            .map(r => r.track)
                            .filter(t => t && t.readyState === 'live');
                        if (tracks.length > 0) {
                            spotlightStream = new MediaStream(tracks);
                            spotlightFeed.stream = spotlightStream;
                        }
                    }
                    if (spotlightStream) {
                        return { feedId: spotlightFeedId, stream: spotlightStream, isLocal: false };
                    }
                }
            }
        }

        for (const [feedId, feed] of Object.entries(this.feeds)) {
            if (!feed) continue;
            if (this.isRemoteFeedHidden(feedId)) continue;
            if (this.isFeedTaggedScreenAudio(feedId)) continue;
            if (!this.feedHasLiveTrack(feed, 'video')) continue;

            let stream = feed.stream || null;
            if (!stream && feed.pc) {
                const tracks = feed.pc.getReceivers()
                    .map(r => r.track)
                    .filter(t => t && t.readyState === 'live');
                if (tracks.length > 0) {
                    stream = new MediaStream(tracks);
                    feed.stream = stream;
                }
            }

            if (stream) {
                return { feedId, stream, isLocal: false };
            }
        }

        return null;
    },

    refreshFloatContainerIfPresent() {
        const container = document.getElementById('floatVideoContainer');
        if (!container) return;
        this.moveVideoToFloat('floatVideoContainer');
    },

    // ─── Event polling loop ─────────────────────────────────────────────────────

    async startPolling() {
        this.polling = true;
        this.lastPollActivityAt = Date.now();
        // Fresh session: don't inherit the previous one's failure count, or its very first
        // transient error would sleep the full backoff. Mirrors startKeepalive.
        this.pollFailures = 0;
        // Retires any loop still parked in a backoff sleep from a previous session — otherwise
        // it wakes, sees this.polling true again, and races this one against the new session.
        const generation = ++this.pollGeneration;
        this.log('poll', 'Polling loop started');
        let emptyPollResponses = 0;

        while (this.polling && generation === this.pollGeneration) {
            try {
                this.pollAbort = new AbortController();

                const event = await this.apiGet(
                    `/api/janus/session/${this.janusSessionId}/poll`,
                    this.pollAbort.signal
                );
                this.lastPollActivityAt = Date.now();
                this.pollFailures = 0;
                if (!this.polling) break;
                if (!event) {
                    emptyPollResponses++;
                    if (emptyPollResponses <= 3 || emptyPollResponses % 10 === 0) {
                        this.log('poll', `Empty poll response (${emptyPollResponses}); continuing`);
                    }
                    await this.sleep(150);
                    continue;
                }
                emptyPollResponses = 0;

                const type = event?.janus;
                if (!type) {
                    this.log('poll', 'Poll payload missing janus type; ignoring');
                    continue;
                }

                // Log FULL raw event for every non-keepalive (critical for debugging)
                if (this.verbosePollEventLogging && type !== 'keepalive') {
                    const raw = JSON.stringify(event);
                    this.log('poll', `RAW EVENT (${raw.length} chars): ${raw.substring(0, 600)}`);
                }

                if (type === 'event') {
                    const sender = event.sender;
                    const data = event?.plugindata?.data;
                    const eventFromMainPublisherHandle =
                        !!sender && String(sender) === String(this.janusHandleId || '');
                    const eventFromScreenAudioPublisherHandle = this.isScreenAudioPublisherHandle(sender);

                    // ── Check for pending subscriber JSEP response ──
                    // When we fire-and-forget a subscriber join, the JSEP offer
                    // arrives here with the subscriber handle as the sender.
                    this.cleanupStalePendingSubscribes();
                    const pendingKeys = Object.keys(this.pendingSubscribes);
                    if (this.verbosePollEventLogging && pendingKeys.length > 0) {
                        this.log('poll', `Pending subscribes: [${pendingKeys.join(',')}], event sender: ${sender}`);
                    }

                    if (sender && this.pendingSubscribes[sender] && event.jsep) {
                        this.log('poll', `Matched subscriber JSEP for handle ${sender}`);
                        await this.completeSubscribe(sender, event.jsep);
                        continue;
                    }
                    if (sender && this.pendingSubscribes[sender] && !event.jsep
                        && (data?.videoroom === 'attached' || data?.videoroom === 'updating' || data?.videoroom === 'updated')) {
                        const pending = this.pendingSubscribes[sender];
                        if (Array.isArray(data?.streams) && data.streams.length > 0) {
                            pending.streams = this.normalizeSubscribeStreams(data.streams, pending?.feedId);
                            this.log('poll', `Subscriber handle ${sender} provided ${pending.streams.length} stream hint(s) while pending`);
                        }
                        const videoroomState = data?.videoroom;
                        if (videoroomState === 'updating') {
                            pending.waitingForJsepUntil = Math.max(pending.waitingForJsepUntil || 0, Date.now() + 2200);
                        } else if (videoroomState === 'attached') {
                            pending.waitingForJsepUntil = Math.max(pending.waitingForJsepUntil || 0, Date.now() + 800);
                        }
                        this.log('poll', `Subscriber handle ${sender} state=${videoroomState} without JSEP for feed ${pending?.feedId}, scheduling subscribe nudge...`);
                        const nudgeDelay = videoroomState === 'updated' ? 250 : 1200;
                        this.schedulePendingSubscribeNudge(sender, nudgeDelay, `poll-${videoroomState}-no-jsep`);
                    }
                    if (sender && this.pendingSubscribes[sender] && data?.error) {
                        const pending = this.pendingSubscribes[sender];
                        delete this.pendingSubscribes[sender];
                        this.logError('subscribe', `Subscriber join failed for feed ${pending?.feedId} (handle ${sender}): ${data.error} (${data.error_code || 'n/a'})`);
                        continue;
                    }

                    // Existing subscriber handle renegotiation (publisher stream updates)
                    if (sender && event.jsep) {
                        const handledExistingSubJsep = await this.renegotiateExistingSubscriber(sender, event.jsep);
                        if (handledExistingSubJsep) {
                            continue;
                        }
                    }

                    if (data) {
                        if (eventFromMainPublisherHandle
                            && typeof data.private_id === 'number'
                            && data.private_id > 0
                            && data.private_id !== this.myPrivateId) {
                            this.myPrivateId = data.private_id;
                            this.log('publish', `Updated private_id=${this.myPrivateId}`);
                        }
                        // New or updated remote publishers
                        if (Array.isArray(data.publishers) && data.publishers.length > 0) {
                            if (!eventFromMainPublisherHandle) {
                                if (eventFromScreenAudioPublisherHandle) {
                                    this.log('poll', `Ignoring publisher list from screen-audio handle ${sender}`);
                                } else {
                                    this.log('poll', `Ignoring publisher list from non-main handle ${sender}`);
                                }
                                // Keep processing this event: it may also carry JSEP for the screen-audio publisher.
                            } else {
                                const publisherCandidates = this.collapsePublisherCandidates(data.publishers);
                                this.log('poll', `Publisher(s) event: ${publisherCandidates.length}`,
                                    publisherCandidates.map(p => ({ id: p.id, display: p.display })));

                                for (const pub of publisherCandidates) {
                                    if (this.isSelfPublisherDisplay(pub?.display)) {
                                        this.log('poll', `Skip self publisher event feed ${pub?.id} (${pub?.display || 'unknown'})`);
                                        continue;
                                    }
                                    if (this.isRemoteFeedHidden(pub?.id)) {
                                        this.updatePublisherStreamHint(pub.id, pub.streams);
                                        this.rememberHiddenRemoteFeedMeta(pub.id, pub.display || '', pub.streams || []);
                                        this.log('poll', `Skip hidden publisher feed ${pub?.id} (${pub?.display || 'unknown'})`);
                                        continue;
                                    }
                                    this.updatePublisherStreamHint(pub.id, pub.streams);
                                    const existingPending = this.findPendingSubscribeByFeed(pub.id);
                                    if (existingPending) {
                                        const mergedStreams = this.normalizeSubscribeStreams(pub.streams, pub.id);
                                        if (mergedStreams.length > 0) {
                                            existingPending.pending.streams = mergedStreams;
                                        }
                                        if (pub.display) {
                                            existingPending.pending.display = pub.display;
                                        }
                                        this.log('poll', `Publisher ${pub.id} update received while subscribe is pending on handle ${existingPending.handleId}; skipping duplicate subscribe`);
                                        continue;
                                    }
                                    const subscribedFeed = this.feeds[pub.id];
                                    if (subscribedFeed) {
                                        const hint = this.publisherStreamHints[String(pub.id)];
                                        const hasAudio = this.hasIncomingAudioTrack(subscribedFeed?.pc);
                                        const hasVideo = this.hasIncomingVideoTrack(subscribedFeed?.pc);
                                        const preferVideoOnly = subscribedFeed.preferVideoOnly === true;
                                        const needAudioRefresh = !preferVideoOnly && !!hint?.hasAudio && !hasAudio;
                                        const needVideoRefresh = !!hint?.hasVideo && !hasVideo;
                                        if (needAudioRefresh || needVideoRefresh) {
                                            this.log(
                                                'poll',
                                                `Publisher ${pub.id} stream mismatch on subscriber (needAudio=${needAudioRefresh}, needVideo=${needVideoRefresh}); requesting refresh on existing handle`
                                            );
                                            this.scheduleExistingSubscriberRefresh(pub.id, 250, 'publisher-event-stream-mismatch', {
                                                requireAudio: needAudioRefresh,
                                                requireVideo: needVideoRefresh
                                            });
                                            continue;
                                        }
                                    }
                                    if (this.feeds[pub.id]) {
                                        // Already subscribed and the mismatch check above found
                                        // nothing missing, so this publisher event needs no media
                                        // change. Tearing the subscription down and rebuilding it
                                        // here is what made remote users blink out of the grid and
                                        // drop audio for seconds whenever anyone toggled a share:
                                        // Janus keeps delivering the publisher's replaced track on
                                        // the existing subscription, so a re-subscribe buys nothing
                                        // and costs a full ICE/DTLS round trip.
                                        //
                                        // Anything genuinely missing is repaired by the
                                        // scheduleExistingSubscriberRefresh branch above, which
                                        // renegotiates in place and keeps the handle.
                                        if (pub.display) {
                                            // Keep the label/role fresh (a share start changes the
                                            // display payload) without touching the transport.
                                            this.feeds[pub.id].display = pub.display;
                                            this.ensureRemoteFeedCell(pub.id, pub.display);
                                        }
                                        this.log('poll', `Publisher ${pub.id} stream update needs no resubscribe (audio/video already present)`);
                                    } else {
                                        await this.initiateSubscribe(pub.id, pub.display, pub.streams);
                                    }
                                }
                                continue;
                            }
                        }

                        // listparticipants response (fallback publisher discovery)
                        if (data.videoroom === 'participants' && Array.isArray(data.participants)) {
                            this.log('poll', `listparticipants: ${data.participants.length} participant(s) in room`);
                            for (const p of data.participants) {
                                // Skip ourselves
                                if (p.id == this.janusHandleId || this.isSelfPublisherDisplay(p.display)) continue;
                                if (this.isRemoteFeedHidden(p.id)) {
                                    this.rememberHiddenRemoteFeedMeta(p.id, p.display || '', p.streams || []);
                                    continue;
                                }
                                // Skip already-subscribed feeds
                                if (this.feeds[p.id]) continue;
                                // Skip feeds we're already subscribing to
                                const alreadyPending = Object.values(this.pendingSubscribes)
                                    .some(ps => ps.feedId === p.id);
                                if (alreadyPending) continue;

                                if (p.publisher) {
                                    this.log('poll', `Discovered new publisher via listparticipants: feed ${p.id} (${p.display || 'unknown'})`);
                                    await this.initiateSubscribe(p.id, p.display);
                                }
                            }
                            continue;
                        }

                        // Publisher left or unpublished
                        if (data.leaving) {
                            this.log('poll', `Publisher leaving: ${data.leaving}`);
                            this.removeFeed(data.leaving);
                        }
                        if (data.unpublished && data.unpublished !== 'ok') {
                            this.log('poll', `Publisher unpublished: ${data.unpublished}`);
                            this.removeFeed(data.unpublished);
                        }
                    }

                    // Re-negotiation JSEP on publisher handle
                    if (event.jsep && sender == this.janusHandleId && this.publisherPc) {
                        const wasConfigured = this.publisherConfigured;
                        this.log('poll', wasConfigured
                            ? 'Re-negotiation JSEP received for publisher'
                            : 'Initial publisher JSEP received via polling');
                        await this.publisherPc.setRemoteDescription(
                            new RTCSessionDescription(event.jsep)
                        );
                        this.logCodecSummary(this.publisherPc, wasConfigured ? 'publish-renegotiation-answer' : 'publish-initial-answer');
                        if (!wasConfigured) {
                            await this.markPublisherConfigured('polling event');
                        }
                    } else if (event.jsep && this.isScreenAudioPublisherHandle(sender) && this.screenAudioPublisherPc) {
                        const wasConfigured = this.screenAudioPublisherConfigured;
                        this.log('poll', wasConfigured
                            ? 'Re-negotiation JSEP received for screen-audio publisher'
                            : 'Initial screen-audio publisher JSEP received via polling');
                        await this.screenAudioPublisherPc.setRemoteDescription(
                            new RTCSessionDescription(event.jsep)
                        );
                        this.screenAudioPublisherConfigured = true;
                    }
                } else if (type === 'trickle') {
                    // Handle Janus trickle ICE candidates (sent when full_trickle is enabled)
                    const candidate = event.candidate;
                    const sender = event.sender;
                    if (candidate && sender == this.janusHandleId && this.publisherPc) {
                        if (candidate.completed) {
                            this.log('poll', 'Janus trickle: end-of-candidates for publisher');
                        } else if (candidate.candidate) {
                            this.log('poll', `Janus trickle: adding remote candidate for publisher`);
                            try {
                                await this.publisherPc.addIceCandidate(new RTCIceCandidate(candidate));
                            } catch (err) {
                                this.log('poll', `Failed to add trickle candidate: ${err.message}`);
                            }
                        }
                    } else if (candidate && this.isScreenAudioPublisherHandle(sender) && this.screenAudioPublisherPc) {
                        if (candidate.completed) {
                            this.log('poll', 'Janus trickle: end-of-candidates for screen-audio publisher');
                        } else if (candidate.candidate) {
                            this.log('poll', 'Janus trickle: adding remote candidate for screen-audio publisher');
                            try {
                                await this.screenAudioPublisherPc.addIceCandidate(new RTCIceCandidate(candidate));
                            } catch (err) {
                                this.log('poll', `Failed to add screen-audio trickle candidate: ${err.message}`);
                            }
                        }
                    } else if (candidate && sender) {
                        // Check subscriber feeds
                        const feed = Object.values(this.feeds).find(f => f.handleId == sender);
                        if (feed && feed.pc && candidate.candidate) {
                            this.log('poll', `Janus trickle: adding remote candidate for subscriber handle ${sender}`);
                            try {
                                await feed.pc.addIceCandidate(new RTCIceCandidate(candidate));
                            } catch (err) {
                                this.log('poll', `Failed to add subscriber trickle candidate: ${err.message}`);
                            }
                        }
                    }
                } else if (type === 'detached' || type === 'hangup') {
                    const sender = event.sender;
                    const reason = event.reason ? ` (${event.reason})` : '';

                    if (sender && sender == this.janusHandleId) {
                        this.logError('poll', `Publisher handle ${sender} ${type}${reason}; requesting Janus session recovery`);
                        this.notifyDotNet('OnJanusSessionLost');
                        break;
                    }

                    if (sender && this.isScreenAudioPublisherHandle(sender)) {
                        this.log('poll', `Screen-audio publisher handle ${sender} ${type}${reason}`);
                        await this.stopScreenAudioPublisher(`poll-${type}`);
                        continue;
                    }

                    if (sender && this.pendingSubscribes[sender]) {
                        const pendingFeed = this.pendingSubscribes[sender]?.feedId;
                        delete this.pendingSubscribes[sender];
                        this.log('poll', `Pending subscriber handle ${sender} ${type}${reason} (feed ${pendingFeed || 'unknown'})`);
                    }

                    if (sender) {
                        const feedMatch = Object.entries(this.feeds).find(([, feed]) => feed?.handleId == sender);
                        if (feedMatch) {
                            const [feedId] = feedMatch;
                            this.log('poll', `Subscriber handle ${sender} ${type}${reason} for feed ${feedId}, removing feed`);
                            this.removeFeed(feedId);
                            continue;
                        }
                    }

                    this.log('poll', `${type} event for handle ${sender || 'n/a'}${reason}`);
                } else if (type !== 'keepalive') {
                    this.log('poll', `Non-event type: ${type}`);
                }
            } catch (err) {
                if (err.name === 'AbortError') continue;
                if (!this.polling) break;

                // Detect Janus session lost (HTTP 404 / error codes 458, 459)
                const errMsg = err.message || '';
                if (errMsg.includes('404') || errMsg.includes('458') || errMsg.includes('459')) {
                    this.log('poll', `Janus session lost (${errMsg}), notifying C#...`);
                    this.notifyDotNet('OnJanusSessionLost');
                    break;
                }

                // Only 404/458/459 above are terminal. Everything else — 5xx, DNS, TLS, offline —
                // used to retry at a flat 1 Hz forever, so a degraded backend got one failed
                // request per second per client until the user hung up manually.
                this.pollFailures = (this.pollFailures || 0) + 1;
                const backoffMs = Math.min(
                    this.pollBackoffMaxMs,
                    1000 * Math.pow(2, Math.min(this.pollFailures - 1, 5)));

                if (this.pollFailures % this.pollFailureLogEscalation === 0) {
                    this.logError('poll', `Poll has failed ${this.pollFailures} consecutive times; backing off toward ${this.pollBackoffMaxMs}ms`);
                }

                this.log('poll', `Poll error (retry ${this.pollFailures} in ${backoffMs}ms): ${errMsg}`);
                await this.sleep(backoffMs);
                if (generation !== this.pollGeneration) break;
            } finally {
                this.pollAbort = null;
            }
        }
        this.log('poll', 'Polling loop ended');
    },

    // ─── Keepalive ──────────────────────────────────────────────────────────────

    startKeepalive() {
        this.keepaliveFailures = 0;
        // Janus reaps a session after session_timeout without a keepalive, 60s by
        // default. At 30s a single lost keepalive plus one slow round trip was enough
        // to cross that line, and the server log shows exactly that outcome - repeated
        // "Poll session <id> not found in Janus" for sessions belonging to live calls,
        // each of which ends as a full client reconnect. 15s means four consecutive
        // failures before the session can be reaped, at negligible cost.
        this.keepaliveTimer = setInterval(async () => {
            if (!this.polling) return;
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/keepalive`, {});
                this.keepaliveFailures = 0;
            } catch (err) {
                this.keepaliveFailures++;
                this.log('keepalive', `Keepalive failed (${this.keepaliveFailures}/3): ${err.message}`);
                if (this.keepaliveFailures >= 3) {
                    // If long-poll is still healthy, avoid false-positive session loss.
                    const lastPollAgeMs = this.lastPollActivityAt > 0
                        ? (Date.now() - this.lastPollActivityAt)
                        : Number.POSITIVE_INFINITY;
                    const pollLooksHealthy = this.polling && Number.isFinite(lastPollAgeMs) && lastPollAgeMs < 90_000;
                    if (pollLooksHealthy) {
                        this.log('keepalive', `Suppressing session-lost trigger: poll active ${Math.round(lastPollAgeMs / 1000)}s ago`);
                        this.keepaliveFailures = 0;
                        return;
                    }

                    this.log('keepalive', 'Janus session lost (3 consecutive keepalive failures)');
                    this.notifyDotNet('OnJanusSessionLost');
                    this.keepaliveFailures = 0; // Reset so we don't fire again
                }
            }
        }, 15_000);
    },

    stopKeepalive() {
        if (this.keepaliveTimer) {
            clearInterval(this.keepaliveTimer);
            this.keepaliveTimer = null;
        }
    },

    // ─── Participant discovery (fallback for missing "new publisher" events) ────

    /**
     * Queries Janus for the current room participants via a dedicated server
     * endpoint that uses its own temporary session (no event queue competition).
     * Subscribes to any publishers we haven't seen yet.
     * This is the primary publisher-discovery mechanism — it doesn't rely on
     * Janus "event" type notifications which don't arrive reliably via long-poll.
     */
    async checkForNewPublishers() {
        if (!this.polling || !this.janusRoomId) return;
        const nowMs = Date.now();
        const minIntervalMs = this.isAppVisible() ? 10000 : 30000;
        if ((nowMs - (this.lastParticipantCheckAtMs || 0)) < minIntervalMs) {
            return;
        }
        this.lastParticipantCheckAtMs = nowMs;

        try {
            this.cleanupStalePendingSubscribes();
            for (const [handleId, pending] of Object.entries(this.pendingSubscribes)) {
                if (!pending) continue;
                const now = Date.now();
                if (now < (pending.waitingForJsepUntil || 0)) continue;
                const sinceLastRequestMs = now - (pending.lastSubscribeRequestAt || 0);
                if (sinceLastRequestMs < 2500) continue;
                this.schedulePendingSubscribeNudge(handleId, 0, 'participant-check-pending');
            }
            const result = await this.apiGet(`/api/janus/room/${this.janusRoomId}/participants`);
            const data = result?.plugindata?.data;
            const participants = data?.participants;
            if (!Array.isArray(participants)) {
                this.log('participants', 'No participants array in response', result);
                return;
            }

            const feedIds = Object.keys(this.feeds);
            this.log('participants', `Room has ${participants.length} participant(s), we have ${feedIds.length} subscribed feed(s) [${feedIds.join(',')}]`);

            for (const p of participants) {
                // Skip ourselves (by handle ID or by display/userId for stale sessions)
                if (p.id == this.janusHandleId) {
                    this.log('participants', `  skip self (handle match): ${p.id}`);
                    continue;
                }
                if (p.display && this.isSelfPublisherDisplay(p.display)) {
                    this.log('participants', `  skip self (display match): ${p.id} display=${p.display}`);
                    continue;
                }
                if (this.isRemoteFeedHidden(p.id)) {
                    this.rememberHiddenRemoteFeedMeta(p.id, p.display || '', p.streams || []);
                    this.log('participants', `  skip hidden feed: ${p.id}`);
                    continue;
                }
                // Already subscribed: heal missing tracks if publisher still reports them.
                const subscribedFeed = this.feeds[p.id];
                if (subscribedFeed) {
                    this.updatePublisherStreamHint(p.id, p.streams);
                    const hint = this.publisherStreamHints[String(p.id)];
                    const hasAudio = this.hasIncomingAudioTrack(subscribedFeed.pc);
                    const hasVideo = this.hasIncomingVideoTrack(subscribedFeed.pc);
                    if (hasAudio) {
                        subscribedFeed.audioRecoveryAttempts = 0;
                    }
                    if (hasVideo) {
                        subscribedFeed.videoRecoveryAttempts = 0;
                    }
                    const preferVideoOnly = subscribedFeed.preferVideoOnly === true;
                    const needAudioRefresh = !preferVideoOnly && !!hint?.hasAudio && !hasAudio;
                    const needVideoRefresh = !!hint?.hasVideo && !hasVideo;
                    if (needAudioRefresh || needVideoRefresh) {
                        this.log(
                            'participants',
                            `  subscribed feed ${p.id} missing track(s) (needAudio=${needAudioRefresh}, needVideo=${needVideoRefresh}); scheduling refresh`
                        );
                        this.scheduleExistingSubscriberRefresh(p.id, 100, 'participant-check-stream-mismatch', {
                            requireAudio: needAudioRefresh,
                            requireVideo: needVideoRefresh
                        });
                    } else if (!preferVideoOnly && !hasAudio && this.getFeedRole(p.id) !== 'stream') {
                        // force:true deliberately bypasses the hint guard, because this is the
                        // "no hint at all" case. But a publisher with no mic hardware or a denied
                        // permission never produces audio, so without a cap this fires a subscribe
                        // POST every participant-check tick for the whole call. Reset above once
                        // audio actually arrives.
                        const attempts = subscribedFeed.audioRecoveryAttempts || 0;
                        if (attempts < this.maxAudioRecoveryAttempts) {
                            subscribedFeed.audioRecoveryAttempts = attempts + 1;
                            this.log('participants', `  subscribed feed ${p.id} missing mic audio without hint; scheduling forced audio refresh (${attempts + 1}/${this.maxAudioRecoveryAttempts})`);
                            this.scheduleExistingSubscriberRefresh(p.id, 100, 'participant-check-no-audio', {
                                requireAudio: true,
                                force: true
                            });
                        } else {
                            this.log('participants', `  skip audio recovery for ${p.id}: no audio after ${attempts} attempts`);
                        }
                    } else if (!hasVideo && !hint && !this.isFeedTaggedScreenAudio(p.id)) {
                        // The video counterpart of the branch above, which did not exist: a
                        // subscription that came up without video and never sees another
                        // publisher event had nothing left to repair it, so joining a call where
                        // someone was ALREADY sharing left their screen permanently blank. The
                        // mismatch branch cannot cover it - it needs hint.hasVideo, and a feed
                        // discovered by this sweep has no hint to read.
                        //
                        // Gated on the hint being absent rather than false, so the very common
                        // camera-off participant (whose hint says so, from a real publishers
                        // event) does not get renegotiated three times for nothing. Screen-audio
                        // companion feeds are audio-only by design and never want this.
                        const attempts = subscribedFeed.videoRecoveryAttempts || 0;
                        if (attempts < this.maxAudioRecoveryAttempts) {
                            subscribedFeed.videoRecoveryAttempts = attempts + 1;
                            this.log('participants', `  subscribed feed ${p.id} missing video without hint; scheduling forced video refresh (${attempts + 1}/${this.maxAudioRecoveryAttempts})`);
                            this.scheduleExistingSubscriberRefresh(p.id, 100, 'participant-check-no-video', {
                                requireVideo: true,
                                force: true
                            });
                        } else {
                            this.log('participants', `  skip video recovery for ${p.id}: no video after ${attempts} attempts`);
                        }
                    } else {
                        this.log('participants', `  skip already subscribed: ${p.id}`);
                    }
                    continue;
                }
                // Skip feeds we're already subscribing to
                const alreadyPending = Object.values(this.pendingSubscribes)
                    .some(ps => ps.feedId === p.id);
                if (alreadyPending) {
                    this.log('participants', `  skip pending subscribe: ${p.id}`);
                    continue;
                }

                if (p.publisher) {
                    this.log('participants', `Discovered new publisher: feed ${p.id} (${p.display || 'unknown'})`);
                    await this.initiateSubscribe(p.id, p.display);
                } else {
                    this.log('participants', `  skip non-publisher: ${p.id} (publisher=${p.publisher})`);
                }
            }
        } catch (err) {
            this.log('participants', `listparticipants request failed: ${err.message}`);
        }
    },

    startParticipantCheck() {
        // First check after 2 seconds, then throttled checks.
        setTimeout(() => this.checkForNewPublishers(), 2000);
        this.participantCheckTimer = setInterval(() => {
            this.checkForNewPublishers();
        }, 5000);
    },

    stopParticipantCheck() {
        if (this.participantCheckTimer) {
            clearInterval(this.participantCheckTimer);
            this.participantCheckTimer = null;
        }
    },

    startRemoteFeedHealthWatchdog() {
        if (this.remoteFeedHealthTimer) return;
        this.remoteFeedHealthTimer = setInterval(() => {
            this.checkRemoteFeedHealth();
        }, 2000);
    },

    stopRemoteFeedHealthWatchdog() {
        if (this.remoteFeedHealthTimer) {
            clearInterval(this.remoteFeedHealthTimer);
            this.remoteFeedHealthTimer = null;
        }
        this.remoteFeedHealthBusy = false;
    },

    async getInboundVideoHealthStats(peerConnection) {
        if (!peerConnection || typeof peerConnection.getStats !== 'function') return null;
        try {
            // Scope the query to the inbound video track. A selector-less getStats() builds and
            // marshals the entire stats graph (transports, candidate pairs, codecs, both
            // directions) just to read three counters off one inbound-rtp entry.
            const videoTrack = peerConnection.getReceivers?.()
                ?.find(r => r?.track?.kind === 'video')?.track || null;
            const stats = await peerConnection.getStats(videoTrack);
            const videoReport = this.extractMediaRtpReport(stats, 'inbound', 'video');
            if (!videoReport) return null;
            return {
                bytesReceived: Number(videoReport.bytesReceived ?? 0),
                packetsReceived: Number(videoReport.packetsReceived ?? 0),
                framesDecoded: Number(videoReport.framesDecoded ?? videoReport.framesReceived ?? 0),
                timestamp: Number(videoReport.timestamp ?? 0)
            };
        } catch {
            return null;
        }
    },

    /// Cheap liveness probe, used before anything that destroys working state in order
    /// to rebuild it. Doubles as a keepalive.
    async isBackendReachable() {
        if (!this.janusSessionId) return false;
        try {
            await this.apiPost(`/api/janus/session/${this.janusSessionId}/keepalive`, {});
            return true;
        } catch {
            return false;
        }
    },

    async forceResubscribeFeed(feedId, reason) {
        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (!normalizedFeedId) return;

        const existing = this.feeds[normalizedFeedId];
        if (!existing) return;

        // Ask the server whether this is even possible before tearing anything down.
        // This removes the feed and rebuilds it from a fresh attach, so when the backend
        // cannot answer the participant just disappears and stays gone - which is exactly
        // what the captured session shows: both feeds destroyed, both re-attaches
        // returning 502, two empty tiles until the whole call restarted.
        //
        // The consecutive-poll-failure check cannot cover this. The poll is a long poll,
        // so at the moment an ICE timeout fires it is still in flight and the counter is
        // still zero - which is why that guard did not fire in the capture. A direct
        // probe is the only thing that answers the question at the time it is asked.
        if (!(await this.isBackendReachable())) {
            this.log(
                'subscribe',
                `Skipping hard re-subscribe for feed ${normalizedFeedId} (${reason}): backend unreachable, keeping the existing subscription`);
            return;
        }

        const display = existing.display || '';
        const hint = this.publisherStreamHints[normalizedFeedId];
        const streamsHint = hint?.raw || hint?.streams || null;
        const numericFeedId = Number(normalizedFeedId);
        const subscribeFeedId = Number.isFinite(numericFeedId) ? numericFeedId : normalizedFeedId;

        delete this.pendingSubscribeFeeds[normalizedFeedId];
        this.log('subscribe', `Feed ${normalizedFeedId} forcing hard re-subscribe (${reason})`);
        this.removeFeed(normalizedFeedId);

        try {
            await this.initiateSubscribe(subscribeFeedId, display, streamsHint);
        } catch (err) {
            // The cell and the subscription are already gone by this point, so letting
            // this escape would drop the participant out of the grid AND abort the health
            // watchdog's sweep partway through - it awaits this inside a try/finally with
            // no catch. Log and leave them to the listparticipants check, which
            // re-subscribes within 5s.
            this.logError(
                'subscribe',
                `Hard re-subscribe failed for feed ${normalizedFeedId} (${reason}): ${err?.message || err}; waiting for participant check`);
        }
    },

    cancelFeedHardRecovery(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return;
        const feed = this.feeds[key];
        if (!feed) return;
        if (feed.hardRecoveryTimer) {
            clearTimeout(feed.hardRecoveryTimer);
            feed.hardRecoveryTimer = null;
        }
    },

    scheduleFeedHardRecovery(feedId, reason, delayMs = 0) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return;
        const feed = this.feeds[key];
        if (!feed?.pc) return;

        const now = Date.now();
        const nextAllowed = Number(feed.nextHardRecoveryAtMs) || 0;
        if (nextAllowed > now) return;

        const delay = Math.max(0, parseInt(delayMs, 10) || 0);
        feed.nextHardRecoveryAtMs = now + Math.max(7000, delay + 3500);
        this.cancelFeedHardRecovery(key);

        feed.hardRecoveryTimer = setTimeout(async () => {
            const current = this.feeds[key];
            if (!current?.pc) return;

            // Re-check at fire time. ICE 'disconnected' is usually transient and heals on
            // its own; the state can be back well before the timer runs, and the cancel on
            // 'connected' can also race this callback. A hard re-subscribe on a connection
            // that already recovered costs a full attach + ICE + DTLS round trip and a
            // visible gap in that person's tile, for nothing.
            const iceState = current.pc.iceConnectionState;
            const pcState = current.pc.connectionState;
            if (iceState === 'connected' || iceState === 'completed' || pcState === 'connected') {
                this.log('subscribe', `Skipping hard recovery for feed ${key} (${reason}): recovered (ice=${iceState}, pc=${pcState})`);
                return;
            }

            // Free early-out for a sustained outage, before paying for the probe in
            // forceResubscribeFeed. It lags - the long poll is still in flight when an ICE
            // timeout first fires, so this reads zero on the first failure - which is why
            // the authoritative check lives there rather than here.
            if ((this.pollFailures || 0) > 0) {
                this.log('subscribe', `Deferring hard recovery for feed ${key} (${reason}): backend unreachable (${this.pollFailures} consecutive poll failure(s))`);
                return;
            }

            try {
                await this.forceResubscribeFeed(key, reason);
            } catch (err) {
                this.log('subscribe', `Hard recovery failed for feed ${key} (${reason}): ${err.message}`);
            }
        }, delay);
    },

    async checkRemoteFeedHealth() {
        if (this.remoteFeedHealthBusy) return;
        if (!this.polling) return;
        if (this.isScreenSharing) return;

        const now = Date.now();
        const minIntervalMs = this.isAppVisible() ? 4000 : 12000;
        if ((now - (this.lastRemoteFeedHealthRunAtMs || 0)) < minIntervalMs) {
            return;
        }
        this.lastRemoteFeedHealthRunAtMs = now;

        const allFeedEntries = Object.entries(this.feeds || {});
        if (allFeedEntries.length === 0) {
            return;
        }

        const maxFeedsPerRun = this.isAppVisible() ? 3 : 1;
        const feedsToInspect = [];
        const startIndex = Math.max(0, this.remoteFeedHealthCursor % allFeedEntries.length);
        const takeCount = Math.min(maxFeedsPerRun, allFeedEntries.length);
        for (let i = 0; i < takeCount; i++) {
            feedsToInspect.push(allFeedEntries[(startIndex + i) % allFeedEntries.length]);
        }
        this.remoteFeedHealthCursor = (startIndex + takeCount) % Math.max(1, allFeedEntries.length);

        this.remoteFeedHealthBusy = true;

        try {
        for (const [feedId, feed] of feedsToInspect) {
            if (!feed?.pc) continue;
            if (!this.feedHasLiveTrack(feed, 'video')) continue;

            const cell = document.getElementById('remote-' + feedId);
            const video = cell?.querySelector?.('video');
            if (!video) continue;
            if (video.paused) {
                this.tryResumeRemoteVideo(video, feedId, 'health-paused');
            }

            const currentTime = Number(video.currentTime) || 0;
            const inboundStats = await this.getInboundVideoHealthStats(feed.pc);
            const inboundBytes = Number(inboundStats?.bytesReceived ?? 0);
            const inboundFrames = Number(inboundStats?.framesDecoded ?? 0);
            const inboundPackets = Number(inboundStats?.packetsReceived ?? 0);

            const hasDecodedFrameNow = video.readyState >= 2 && (currentTime > 0.01 || inboundFrames > 0);
            if (!feed.firstVideoFrameSeenAtMs && hasDecodedFrameNow) {
                feed.firstVideoFrameSeenAtMs = now;
            }

            const firstTrackAtMs = Number(feed.lastVideoTrackAt || feed.lastTrackAt || 0);
            if (!feed.firstVideoFrameSeenAtMs && firstTrackAtMs > 0) {
                const sinceTrackMs = now - firstTrackAtMs;
                if (sinceTrackMs >= 6000) {
                    const nextRecoveryAtMs = Number(feed.nextVideoRecoveryAtMs) || 0;
                    if (nextRecoveryAtMs <= now) {
                        const recoveryCount = (parseInt(feed.videoStallRecoveryCount, 10) || 0) + 1;
                        feed.videoStallRecoveryCount = recoveryCount;
                        const useHardRecovery = recoveryCount >= 2;
                        this.log(
                            'subscribe',
                            `Feed ${feedId} has no first decoded frame after ${Math.round(sinceTrackMs)}ms; recovery=${useHardRecovery ? 'hard-resubscribe' : 'refresh'} attempt=${recoveryCount}, frames=${inboundFrames}, bytes=${inboundBytes}, packets=${inboundPackets}`
                        );
                        this.tryResumeRemoteVideo(video, feedId, 'no-first-frame');
                        if (useHardRecovery) {
                            feed.videoStallRecoveryCount = 0;
                            feed.nextVideoRecoveryAtMs = now + 10000;
                            await this.forceResubscribeFeed(feedId, 'no-first-frame-watchdog');
                            continue;
                        }
                        feed.nextVideoRecoveryAtMs = now + 6000;
                        this.scheduleExistingSubscriberRefresh(feedId, 150, 'no-first-frame-watchdog', {
                            requireVideo: true,
                            force: true
                        });
                    }
                }
                continue;
            }

            if (video.readyState < 2) continue;
            if (!Number.isFinite(feed.lastRenderVideoAtMs)) {
                feed.lastRenderVideoAtMs = now;
                feed.lastRenderVideoTime = currentTime;
                feed.lastInboundVideoBytes = inboundBytes;
                feed.lastInboundVideoFrames = inboundFrames;
                feed.lastInboundVideoPackets = inboundPackets;
                continue;
            }

            const lastVideoTime = Number(feed.lastRenderVideoTime) || 0;
            const lastInboundBytes = Number(feed.lastInboundVideoBytes) || 0;
            const lastInboundFrames = Number(feed.lastInboundVideoFrames) || 0;
            const lastInboundPackets = Number(feed.lastInboundVideoPackets) || 0;

            const renderProgressed = currentTime > (lastVideoTime + 0.03);
            const framesProgressed = inboundFrames > lastInboundFrames;
            const bytesProgressed = inboundBytes > (lastInboundBytes + 8192);
            const packetsProgressed = inboundPackets > (lastInboundPackets + 3);
            const hadDecodedFrame = (Number(feed.maxInboundDecodedFrames) || 0) > 0 || inboundFrames > 0;
            if (inboundFrames > (Number(feed.maxInboundDecodedFrames) || 0)) {
                feed.maxInboundDecodedFrames = inboundFrames;
            }

            // Decode/render progress is the primary health signal.
            // Bytes/packets alone can keep increasing while video is frozen.
            const mediaProgressed = renderProgressed || framesProgressed;
            const bootstrapTrafficProgressed = !hadDecodedFrame && (bytesProgressed || packetsProgressed);

            feed.lastInboundVideoBytes = inboundBytes;
            feed.lastInboundVideoFrames = inboundFrames;
            feed.lastInboundVideoPackets = inboundPackets;

            if (mediaProgressed || bootstrapTrafficProgressed) {
                feed.lastRenderVideoAtMs = now;
                feed.lastRenderVideoTime = currentTime;
                feed.videoStallRecoveryCount = 0;
                continue;
            }

            const decodeStuckWithTraffic = hadDecodedFrame && !framesProgressed && !renderProgressed && (bytesProgressed || packetsProgressed);

            const stalledMs = now - (Number(feed.lastRenderVideoAtMs) || now);
            if (stalledMs < 5000) continue;

            const nextRecoveryAtMs = Number(feed.nextVideoRecoveryAtMs) || 0;
            if (nextRecoveryAtMs > now) continue;

            const recoveryCount = (parseInt(feed.videoStallRecoveryCount, 10) || 0) + 1;
            feed.videoStallRecoveryCount = recoveryCount;
            const useHardRecovery = decodeStuckWithTraffic ? (recoveryCount >= 2) : (recoveryCount >= 3);

            this.log(
                'subscribe',
                `Feed ${feedId} video appears stalled for ${Math.round(stalledMs)}ms; recovery=${useHardRecovery ? 'hard-resubscribe' : 'refresh'} attempt=${recoveryCount}, decodeStuckWithTraffic=${decodeStuckWithTraffic}, frameDelta=${inboundFrames - lastInboundFrames}, byteDelta=${inboundBytes - lastInboundBytes}, packetDelta=${inboundPackets - lastInboundPackets}`
            );
            this.tryResumeRemoteVideo(video, feedId, 'stalled-watchdog');

            if (useHardRecovery) {
                feed.videoStallRecoveryCount = 0;
                feed.nextVideoRecoveryAtMs = now + 10000;
                await this.forceResubscribeFeed(feedId, 'video-stalled-watchdog');
                continue;
            }

            feed.nextVideoRecoveryAtMs = now + 6000;
            this.scheduleExistingSubscriberRefresh(feedId, 150, 'video-stalled-watchdog', {
                requireVideo: true,
                force: true
            });
        }
        } finally {
            this.remoteFeedHealthBusy = false;
        }
    },

    // ─── Controls ───────────────────────────────────────────────────────────────

    toggleMute() {
        // In PTT mode, mute button forces mute but keeps PTT enabled.
        if (this.pttEnabled) {
            this.log('ptt', 'Mute button pressed during PTT — forcing mute, PTT stays enabled');
            this.pttKeyDown = false;
            this.isMuted = true;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = false);
            }
            this.notifyDotNet('OnMuteChanged', true);
            return;
        }
        this.ensureAudioContextActive();
        this.isMuted = !this.isMuted;
        if (this.localStream) {
            this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
        }
        this.notifyDotNet('OnMuteChanged', this.isMuted);
    },

    async toggleCamera() {
        // Before the publisher is negotiated there is no sender to attach to, so this
        // used to light the camera up locally and publish nothing - the preview looked
        // right while nobody else ever saw a frame.
        if (!this.isPublisherReadyForMedia()) {
            this.log('media', 'Ignoring camera toggle: publisher is not negotiated yet');
            return;
        }

        const nextCameraOff = !this.isCameraOff;
        let shouldRenegotiate = false;

        try {
            if (nextCameraOff) {
                this.stopLocalCameraTracks('toggle-camera-off');

                if (this.publisherPc && !this.isScreenSharing) {
                    const videoSender = this.findSenderByKind(this.publisherPc, 'video');
                    if (videoSender) {
                        await videoSender.replaceTrack(null);
                        const transceiver = this.publisherPc.getTransceivers().find(t => t.sender === videoSender)
                            || this.publisherPc.getTransceivers().find(t =>
                                t.receiver?.track?.kind === 'video' || t.mid === '1');
                        if (transceiver && (transceiver.direction === 'sendrecv' || transceiver.direction === 'sendonly')) {
                            transceiver.direction = 'recvonly';
                            shouldRenegotiate = true;
                        }
                    }
                }

                this.isCameraOff = true;
            } else {
                const videoTrack = await this.ensureLocalCameraTrack('toggle-camera-on');
                videoTrack.enabled = true;

                if (this.publisherPc && !this.isScreenSharing) {
                    let videoSender = this.findSenderByKind(this.publisherPc, 'video');
                    if (!videoSender) {
                        videoSender = this.publisherPc.addTrack(videoTrack, this.localStream);
                        shouldRenegotiate = true;
                    } else {
                        await videoSender.replaceTrack(videoTrack);
                    }

                    const transceiver = this.publisherPc.getTransceivers().find(t => t.sender === videoSender)
                        || this.publisherPc.getTransceivers().find(t =>
                            t.receiver?.track?.kind === 'video' || t.mid === '1');
                    if (transceiver && transceiver.direction !== 'sendrecv') {
                        transceiver.direction = 'sendrecv';
                        shouldRenegotiate = true;
                    }
                }

                this.isCameraOff = false;
            }

            if (shouldRenegotiate) {
                await this.renegotiatePublisher(nextCameraOff ? 'camera-off' : 'camera-on');
            }
        } catch (err) {
            this.logError('camera', `Camera toggle failed: ${err.message}`);
            this.notifyDotNet('OnError', `Camera toggle failed: ${err.message}`);
            if (!nextCameraOff) {
                this.isCameraOff = true;
                this.stopLocalCameraTracks('camera-toggle-revert');
            }
        }

        this.notifyDotNet('OnCameraChanged', this.isCameraOff);
    },

    async enforceSinglePublisherAudioSender(preferredTrack = null) {
        if (!this.publisherPc) return false;

        const audioTransceivers = this.publisherPc.getTransceivers().filter(t =>
            t?.sender?.track?.kind === 'audio' ||
            t?.receiver?.track?.kind === 'audio' ||
            t?.mid === '0');
        if (audioTransceivers.length <= 1) return false;

        let primary = null;
        if (preferredTrack?.id) {
            primary = audioTransceivers.find(t => t?.sender?.track?.id === preferredTrack.id) || null;
        }
        if (!primary) {
            primary = audioTransceivers.find(t => t?.sender?.track?.kind === 'audio') || audioTransceivers[0];
        }

        let renegotiate = false;
        for (const transceiver of audioTransceivers) {
            if (!transceiver || transceiver === primary) continue;

            try {
                if (transceiver.sender?.track) {
                    await transceiver.sender.replaceTrack(null);
                }
            } catch { }

            if (transceiver.direction === 'sendrecv' || transceiver.direction === 'sendonly') {
                transceiver.direction = 'recvonly';
                renegotiate = true;
            }
        }

        return renegotiate;
    },

    async startScreenShare() {
        // Same reason as toggleCamera, and it cannot be deferred either: getDisplayMedia
        // needs the user gesture that opened the picker, so replaying the intent once the
        // publisher comes up is not an option. Refuse now; the UI keeps the control
        // disabled until OnPublisherReadyChanged says otherwise.
        if (!this.isPublisherReadyForMedia()) {
            this.logError('screenshare', 'Cannot start screen share yet: publisher is not negotiated');
            return;
        }

        if (!navigator?.mediaDevices || typeof navigator.mediaDevices.getDisplayMedia !== 'function') {
            const message = this.isAndroidWebRtcEnvironment()
                ? 'Screen sharing is not supported on this Android WebView build.'
                : 'Screen sharing is not supported in this environment (getDisplayMedia unavailable).';
            this.logError('screenshare', message);
            return;
        }

        try {
            if (this.musicBotActive) {
                await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: true });
            }

            this.stopPublisherVideoPerformanceGuard();
            this.clearScreenShareBitrateWarmupTimers();
            await this.stopWindowsScreenAudioFallback(false);
            this.setSystemScreenShareAppPlaybackSuppressed(false);

            const fps = parseInt(this.currentConstraints.fps) || 30;
            const targetResolution = this.parseResolutionSetting(this.currentConstraints?.resolution, 1920, 1080);
            const videoConstraints = {
                cursor: 'always',
                width: { ideal: targetResolution.width },
                height: { ideal: targetResolution.height },
                // Ceiling rather than the target itself - see getFrameRateCeiling. This is
                // the very first limiter the frames meet, so pinning it here cost frames
                // before any of the later tuning could matter.
                frameRate: { ideal: fps, max: this.getFrameRateCeiling(fps) }
            };
            // Display audio is ALWAYS requested: the share picker carries its own
            // "share audio" consent, and that choice belongs to the user at pick
            // time. The settings switch governs only the native Windows fallback
            // that steps in when the browser grants no display audio.
            const nativeFallbackAllowed = this.currentConstraints.screenShareAudioEnabled !== false;
            this.log('screenshare', `Native screen-audio fallback ${nativeFallbackAllowed ? 'enabled' : 'disabled'} by settings`);

            // Chromium-specific options alongside the standard constraints. Anything that
            // does not implement them ignores unknown dictionary members, so this stays
            // safe on other engines.
            const displayMediaOptions = (withAudio) => ({
                video: {
                    ...videoConstraints,
                    // A hint, not a constraint - it only preselects the picker's tab, and
                    // a window can still be chosen. Worth knowing this is safe to remove:
                    // it was added on the theory that only whole-screen shares can carry
                    // audio, which turned out to be false. Windows 11 captures per-window
                    // audio through WASAPI process loopback, so biasing the picker buys
                    // nothing for audio and only nudges away from app shares.
                    displaySurface: 'monitor'
                },
                // 'include' is already Chromium's default; stating it is the documented
                // way to ask, and it costs nothing.
                systemAudio: withAudio ? 'include' : 'exclude',
                // Lets the sharer swap which window or screen is being shared without
                // stopping and re-picking.
                surfaceSwitching: 'include',
                // false keeps the captured audio playing on the sharer's own speakers.
                // Suppressing it would silence the game or app for the person sharing it -
                // a different problem from the app's own playback being recaptured, which
                // is what setSystemScreenShareAppPlaybackSuppressed handles.
                audio: withAudio ? { suppressLocalAudioPlayback: false } : false
            });

            let stream;
            // Try with system audio capture first, fall back to video-only
            // (audio capture is not supported in all environments / screen types)
            try {
                stream = await navigator.mediaDevices.getDisplayMedia(displayMediaOptions(true));
            } catch (audioErr) {
                // If user cancelled the picker, don't retry
                if (audioErr.name === 'NotAllowedError' || audioErr.name === 'AbortError') {
                    throw audioErr;
                }
                // The name matters more than the message when this is reported: NotReadableError
                // is the OS refusing to open the audio endpoint (a virtual or exclusive-mode
                // default output, typically), which is worth telling apart from the surface
                // simply not offering audio.
                this.log('screenshare', `Audio capture unavailable (${audioErr.name}: ${audioErr.message}), sharing video-only`);
                stream = await navigator.mediaDevices.getDisplayMedia(displayMediaOptions(false));
            }

            const sharedVideoTrack = stream.getVideoTracks()[0] || null;
            this.applyVideoTrackContentHint(sharedVideoTrack, 'screenshare');

            // Instant sender-side pause state: minimizing the captured window mutes
            // the capture track well before the frame watchdog would notice.
            if (sharedVideoTrack) {
                sharedVideoTrack.addEventListener('mute', () => {
                    this.log('screenshare', 'Share source stopped producing frames (window minimized?)');
                    const localCell = document.getElementById('localVideo')?.closest('.video-cell');
                    this.setCellPausedState(localCell, true);
                });
                sharedVideoTrack.addEventListener('unmute', () => {
                    this.log('screenshare', 'Share source resumed producing frames');
                    const localVideo = document.getElementById('localVideo');
                    if (localVideo) localVideo._lastFrameTs = performance.now();
                    this.setCellPausedState(localVideo?.closest('.video-cell'), false);
                });
            }
            this.ensureVideoPauseWatchdog();
            const videoSettings = sharedVideoTrack?.getSettings?.() || {};
            const displaySurface = (videoSettings.displaySurface || '').toString();
            const trackLabel = (sharedVideoTrack?.label || '').toString();
            const streamIdHint = (stream?.id || '').toString();
            const deviceIdHint = (videoSettings.deviceId || '').toString();
            const targetLabel = this.buildScreenShareTargetLabel(trackLabel, streamIdHint, deviceIdHint);
            const shareTarget = this.classifyScreenShareTarget(displaySurface, `${targetLabel} ${trackLabel}`);
            const isSystemShare = shareTarget === 'system';
            const treatAsWindowShare = shareTarget !== 'system';
            // Remembered so a mid-share settings toggle can engage the native audio
            // fallback with the same target the share was started with.
            this.activeScreenShareTarget = { targetLabel, displaySurface, shareTarget };
            await this.tuneScreenShareVideoTrackForPerformance(
                sharedVideoTrack,
                fps,
                'screenshare-capture',
                { enforceResolutionCap: true }
            );

            // Some Windows builds attach display-audio slightly after getDisplayMedia resolves.
            // Give it a short grace window before engaging native fallback. Gated on the
            // fallback setting: without a fallback to avoid, a late display track is
            // picked up by the stream's onaddtrack reconcile instead.
            if (nativeFallbackAllowed && stream.getAudioTracks().length === 0) {
                const delayedAudioTrack = await this.waitForScreenShareAudioTrack(stream, 1100);
                if (delayedAudioTrack) {
                    this.log('screenshare', 'Display audio track appeared after capture start; skipping native fallback');
                }
            }

            // WebView2 often returns no display-audio track for window/screen picks.
            // On Windows desktop, request native loopback fallback via host bridge only when still needed.
            if (nativeFallbackAllowed && stream.getAudioTracks().length === 0) {
                const preferProcessIsolation = treatAsWindowShare;
                // 'unknown' also allows the system fallback. It used to be excluded,
                // which turned "old WebView2 runtime reports neither displaySurface
                // nor a recognizable label" into a guaranteed-silent full-screen
                // share. The native side still tries process isolation first, so a
                // real window share that lands here only widens to system loopback
                // when the window's process cannot be resolved.
                const allowSystemFallback = isSystemShare || shareTarget === 'unknown';
                if (!allowSystemFallback) {
                    this.log('screenshare', 'window capture detected; system audio fallback is disabled');
                } else if (shareTarget === 'unknown') {
                    this.log('screenshare', 'share target classification uncertain; allowing system audio fallback as best effort');
                }

                const fallbackAudioTrack = await this.startWindowsScreenAudioFallback({
                    preferProcessIsolation,
                    allowSystemFallback,
                    targetLabel,
                    displaySurface
                });
                if (fallbackAudioTrack) {
                    stream.addTrack(fallbackAudioTrack);
                    this.log('screenshare', 'Attached Windows native audio fallback track');
                } else {
                    if (treatAsWindowShare) {
                        this.log('screenshare', 'Window-share audio unavailable; continuing without audio (no system fallback)');
                    } else {
                        this.log('screenshare', 'No display audio and fallback unavailable, sharing video-only');
                    }
                }
            }

            // Keep exactly one screen-share audio track to avoid duplicate playback/sending.
            const screenAudioTracks = stream.getAudioTracks();
            if (screenAudioTracks.length > 1) {
                const primaryAudioTrack = this.selectPreferredScreenShareAudioTrack(stream) || screenAudioTracks[0] || null;
                for (const track of screenAudioTracks) {
                    if (!track || (primaryAudioTrack && track.id === primaryAudioTrack.id)) continue;
                    try { stream.removeTrack(track); } catch { }
                    try { track.stop(); } catch { }
                }
                this.log(
                    'screenshare',
                    `Detected ${screenAudioTracks.length} screen audio tracks; using only ${primaryAudioTrack?.id || 'primary'}`
                );
            }

            // Both of these key off actual track state now, not the settings switch:
            // display audio can exist with the fallback disabled and vice versa.
            const preferredPreFallbackShutdown = this.selectPreferredScreenShareAudioTrack(stream);
            if (preferredPreFallbackShutdown
                && this.isWindowsNativeFallbackAudioTrack(this.screenAudioFallbackTrack)
                && preferredPreFallbackShutdown.id !== this.screenAudioFallbackTrack.id) {
                this.log('screenshare', 'Browser display audio track available; disabling Windows native fallback to avoid double audio');
                await this.stopWindowsScreenAudioFallback(true);
            }

            const hasLiveShareAudioTrack = stream.getAudioTracks().some(t => t && t.readyState === 'live');
            const shouldSuppressAppPlayback = isSystemShare && hasLiveShareAudioTrack;
            this.setSystemScreenShareAppPlaybackSuppressed(shouldSuppressAppPlayback);
            if (!shouldSuppressAppPlayback && isSystemShare) {
                this.log('screenshare', 'System share has no active share-audio track; keeping in-app playback enabled');
            }

            this.screenStream = stream;
            this.isScreenSharing = true;

            stream.onaddtrack = async (event) => {
                try {
                    const addedTrack = event?.track;
                    if (!this.isScreenSharing || this.screenStream !== stream || addedTrack?.kind !== 'audio') {
                        return;
                    }

                    // If browser display-audio appears after we already attached fallback, switch away from fallback.
                    if (this.isWindowsNativeFallbackAudioTrack(this.screenAudioFallbackTrack)
                        && addedTrack.id !== this.screenAudioFallbackTrack?.id) {
                        this.log('screenshare', 'Late browser display-audio track detected; disabling native fallback to avoid duplicate audio');
                        await this.stopWindowsScreenAudioFallback(true);
                    }

                    // Route whatever screen audio exists — the settings switch only
                    // decides whether the native fallback gets STARTED, never whether
                    // browser-granted display audio is published.
                    const preferredTrack = this.selectPreferredScreenShareAudioTrack(stream);
                    if (preferredTrack) {
                        const separateAudioReady = await this.ensureScreenAudioPublisher(preferredTrack);
                        if (this.publisherPc) {
                            const audioSender = this.findSenderByKind(this.publisherPc, 'audio');
                            const currentMicTrack = this.localStream?.getAudioTracks?.()[0] || null;
                            if (audioSender) {
                                if (separateAudioReady) {
                                    await audioSender.replaceTrack(currentMicTrack);
                                    await this.enforceSinglePublisherAudioSender(currentMicTrack);
                                    this.log('screenshare', 'Updated main publisher audio sender with microphone (screen audio on separate feed)');
                                } else {
                                    const fallbackTrack = await this.buildScreenShareOutgoingAudioTrack(preferredTrack, currentMicTrack);
                                    await audioSender.replaceTrack(fallbackTrack);
                                    await this.enforceSinglePublisherAudioSender(fallbackTrack);
                                    const fallbackIsMixed = !!(fallbackTrack && currentMicTrack && fallbackTrack.id !== currentMicTrack.id);
                                    this.log(
                                        'screenshare',
                                        fallbackIsMixed
                                            ? 'Screen-audio publisher unavailable; updated main publisher audio sender with mixed fallback track'
                                            : 'Screen-audio publisher unavailable; updated main publisher audio sender with microphone fallback'
                                    );
                                }
                            }
                        }
                    } else {
                        await this.stopScreenAudioPublisher('no-screen-audio-track');
                    }
                } catch (err) {
                    this.log('screenshare', `onaddtrack audio reconcile failed: ${err.message}`);
                }
            };

            const outgoingScreenVideoTrack = this.screenStream.getVideoTracks()[0] || null;
            const outgoingScreenAudioTrack = this.selectPreferredScreenShareAudioTrack(this.screenStream);
            const outgoingMainMicTrack = this.localStream?.getAudioTracks?.()[0] || null;
            let outgoingMainAudioTrack = outgoingMainMicTrack;
            let usingSeparateScreenAudioPublisher = false;
            // Publish is driven by track presence, not the settings switch: display
            // audio granted in the picker always goes out.
            if (outgoingScreenAudioTrack) {
                usingSeparateScreenAudioPublisher = await this.ensureScreenAudioPublisher(outgoingScreenAudioTrack);
                if (!usingSeparateScreenAudioPublisher) {
                    outgoingMainAudioTrack = await this.buildScreenShareOutgoingAudioTrack(outgoingScreenAudioTrack, outgoingMainMicTrack);
                    const fallbackIsMixed = !!(outgoingMainAudioTrack && outgoingMainMicTrack && outgoingMainAudioTrack.id !== outgoingMainMicTrack.id);
                    if (fallbackIsMixed) {
                        this.log('screenshare', 'Separate screen-audio publisher unavailable; using mixed audio fallback on main publisher');
                    } else if (outgoingMainAudioTrack) {
                        this.log('screenshare', 'Separate screen-audio publisher unavailable; using microphone-only fallback on main publisher');
                    } else {
                        this.log('screenshare', 'Separate screen-audio publisher unavailable and no fallback audio track is available');
                    }
                }
            } else {
                await this.stopScreenAudioPublisher('no-screen-audio-track');
            }

            const localVideo = document.getElementById('localVideo');
            if (localVideo) localVideo.srcObject = this.screenStream;
            this.updateLocalFeedAvatarFallback();

            this.screenStream.getVideoTracks()[0].onended = () => {
                const finalizeStop = () => {
                    if (!this.isScreenSharing || this.screenStream !== stream) return;
                    const activeVideoTrack = this.screenStream.getVideoTracks()[0] || null;
                    if (activeVideoTrack && activeVideoTrack.readyState !== 'ended') return;
                    this.stopScreenShare();
                };

                if (document.hidden) {
                    this.log('screenshare', 'Screen-share track ended while app hidden; deferring stop until app is visible');
                    const waitUntilVisible = () => {
                        if (!this.isScreenSharing || this.screenStream !== stream) return;
                        if (document.hidden) {
                            setTimeout(waitUntilVisible, 350);
                            return;
                        }
                        finalizeStop();
                    };
                    waitUntilVisible();
                    return;
                }

                finalizeStop();
            };

            // Replace tracks in publisher PC and renegotiate with Janus if needed
            if (this.publisherPc) {
                let needsRenegotiation = false;

                const videoSender = this.findSenderByKind(this.publisherPc, 'video');
                if (videoSender && outgoingScreenVideoTrack) {
                    await videoSender.replaceTrack(outgoingScreenVideoTrack);
                    this.log('screenshare', 'Replaced video sender track with screen track');
                    const t = this.publisherPc.getTransceivers().find(t => t.sender === videoSender);
                    if (t && t.direction === 'recvonly') {
                        t.direction = 'sendrecv';
                        needsRenegotiation = true;
                        this.log('screenshare', 'Changed video transceiver direction to sendrecv');
                    }
                    await this.tuneScreenShareVideoSenderForPerformance(videoSender, fps, 'screenshare-sender');
                    await this.applyBitrate(this.currentConstraints.bitrateKbps);
                } else if (!videoSender) {
                    this.log('screenshare', 'No sender found for video track');
                }

                const audioSender = this.findSenderByKind(this.publisherPc, 'audio');
                if (audioSender) {
                    await audioSender.replaceTrack(outgoingMainAudioTrack);
                    if (usingSeparateScreenAudioPublisher) {
                        if (outgoingMainAudioTrack) {
                            this.log('screenshare', 'Replaced main audio sender track with microphone (screen audio on separate feed)');
                        } else {
                            this.log('screenshare', 'No outgoing microphone track available for main publisher');
                        }
                    } else if (outgoingMainAudioTrack && outgoingMainMicTrack && outgoingMainAudioTrack.id !== outgoingMainMicTrack.id) {
                        this.log('screenshare', 'Replaced main audio sender track with mixed mic+screen fallback');
                    } else if (outgoingMainAudioTrack) {
                        this.log('screenshare', 'Replaced main audio sender track with microphone fallback');
                    } else {
                        this.log('screenshare', 'No outgoing audio track available for main publisher');
                    }

                    const t = this.publisherPc.getTransceivers().find(t => t.sender === audioSender);
                    if (t && t.direction === 'recvonly' && outgoingMainAudioTrack) {
                        t.direction = 'sendrecv';
                        needsRenegotiation = true;
                        this.log('screenshare', 'Changed audio transceiver direction to sendrecv');
                    }
                } else {
                    this.log('screenshare', 'No sender found for audio track');
                }

                const audioChanged = await this.enforceSinglePublisherAudioSender(outgoingMainAudioTrack);
                needsRenegotiation = needsRenegotiation || audioChanged;

                // Janus needs a new SDP offer when transceiver direction changes
                if (needsRenegotiation) {
                    await this.renegotiatePublisher('screenshare');
                }

                await this.applyBitrate(this.currentConstraints.bitrateKbps);
                await this.requestPublisherKeyframe('screenshare-start');
                this.scheduleScreenShareBitrateWarmup();
                this.startPublisherVideoPerformanceGuard();
            }

            this.notifyDotNet('OnScreenShareChanged', true);
            this.reportScreenShareTarget(targetLabel, displaySurface);
            // Frame dimensions settle slightly after capture starts; re-report so the
            // overlay resolves the right monitor.
            setTimeout(() => {
                if (this.isScreenSharing && this.screenStream === stream) {
                    this.reportScreenShareTarget(targetLabel, displaySurface);
                }
            }, 900);
        } catch (err) {
            this.stopPublisherVideoPerformanceGuard();
            this.clearScreenShareBitrateWarmupTimers();
            await this.cleanupScreenShareAudioMixTrack();
            await this.stopScreenAudioPublisher('screenshare-start-error');
            await this.stopWindowsScreenAudioFallback(true);
            this.setSystemScreenShareAppPlaybackSuppressed(false);
            this.notifyDotNet('OnError', 'Screen share failed: ' + err.message);
        }
    },

    async stopScreenShare() {
        if (this.musicBotActive) {
            await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: true });
        }

        this.activeScreenShareTarget = null;
        this.stopPublisherVideoPerformanceGuard();
        this.clearScreenShareBitrateWarmupTimers();
        await this.stopWindowsScreenAudioFallback(true);
        await this.cleanupScreenShareAudioMixTrack();
        await this.stopScreenAudioPublisher('screenshare-stop');
        this.setSystemScreenShareAppPlaybackSuppressed(false);

        if (this.screenStream) {
            this.screenStream.onaddtrack = null;
            this.screenStream.getTracks().forEach(t => t.stop());
            this.screenStream = null;
        }
        this.isScreenSharing = false;

        const localVideo = document.getElementById('localVideo');
        if (localVideo && this.localStream) {
            localVideo.srcObject = this.localStream;
        }
        this.updateLocalFeedAvatarFallback();

        // Restore local tracks in publisher PC
        let needsRenegotiation = false;
        if (this.publisherPc) {
            const hasLocalVideo = this.localStream && this.localStream.getVideoTracks().length > 0;
            const hasLocalAudio = this.localStream && this.localStream.getAudioTracks().length > 0;

            if (this.localStream) {
                this.localStream.getTracks().forEach(track => {
                    const s = this.findSenderByKind(this.publisherPc, track.kind);
                    if (s) s.replaceTrack(track);
                });

                const restoredVideoSender = this.findSenderByKind(this.publisherPc, 'video');
                await this.tuneScreenShareVideoSenderForPerformance(
                    restoredVideoSender,
                    parseInt(this.currentConstraints.fps, 10) || 30,
                    'screenshare-stop',
                    { scaleResolutionDownBy: 1 }
                );
            }

            // If no local video, set the video transceiver back to recvonly
            if (!hasLocalVideo) {
                const vt = this.publisherPc.getTransceivers().find(t =>
                    t.receiver?.track?.kind === 'video' || t.mid === '1'
                );
                if (vt && vt.direction === 'sendrecv') {
                    await vt.sender.replaceTrack(null);
                    vt.direction = 'recvonly';
                    needsRenegotiation = true;
                    this.log('screenshare', 'No local camera — switched video transceiver back to recvonly');
                }
            }
            // Same for audio if no local audio
            if (!hasLocalAudio) {
                const at = this.publisherPc.getTransceivers().find(t =>
                    t.receiver?.track?.kind === 'audio' || t.mid === '0'
                );
                if (at && at.direction === 'sendrecv') {
                    await at.sender.replaceTrack(null);
                    at.direction = 'recvonly';
                    needsRenegotiation = true;
                    this.log('screenshare', 'No local audio — switched audio transceiver back to recvonly');
                }
            }

            const restoredLocalAudioTrack = hasLocalAudio ? this.localStream.getAudioTracks()[0] : null;
            const audioChanged = await this.enforceSinglePublisherAudioSender(restoredLocalAudioTrack);
            needsRenegotiation = needsRenegotiation || audioChanged;

            if (needsRenegotiation) {
                await this.renegotiatePublisher('screenshare-stop');
            }
        }

        this.notifyDotNet('OnScreenShareChanged', false);
    },

    // ─── Push-to-talk ─────────────────────────────────────────────────────────

    /**
     * Enable or disable push-to-talk mode.
     * When enabled, the mic is muted by default and only unmuted while
     * the configured key is held down.
     */
    setPushToTalkSoundEnabled(enabled) {
        this.pttSoundEnabled = enabled !== false;
        this.log('ptt', `setPushToTalkSoundEnabled enabled=${this.pttSoundEnabled}`);
    },

    setPushToTalk(enabled, key) {
        this.log('ptt', `setPushToTalk enabled=${enabled}, key=${key}`);

        // Remove old listeners
        this.removePttListeners();

        this.pttEnabled = enabled;
        this.pttKey = key || 'Space';
        this.pttBinding = this.parsePttBinding(this.pttKey);
        this.pttKeyDown = false;
        this.pttPressedCodes.clear();
        this.notifyHostPttConfig();

        if (enabled) {
            // Mute immediately when entering PTT mode
            this.applyPttPressedState(false, false);

            // Install global keyboard listeners
            this._pttKeyDownHandler = (e) => this.handlePttKeyDown(e);
            this._pttKeyUpHandler = (e) => this.handlePttKeyUp(e);
            this._pttBlurHandler = () => this.handlePttFocusLost();
            this._pttVisibilityHandler = () => this.handlePttVisibilityChanged();
            document.addEventListener('keydown', this._pttKeyDownHandler);
            document.addEventListener('keyup', this._pttKeyUpHandler);
            window.addEventListener('blur', this._pttBlurHandler);
            document.addEventListener('visibilitychange', this._pttVisibilityHandler);

            this.log('ptt', `PTT active — hold "${this.pttKey}" to talk`);
        } else {
            // Exiting PTT mode — keep current manual mute state.
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
            }
            this.notifyDotNet('OnMuteChanged', this.isMuted);
            this.log('ptt', 'PTT disabled — manual mute state retained');
        }
    },

    handlePttKeyDown(e) {
        if (!this.pttEnabled) return;
        this.pttPressedCodes.add(e.code);
        if (this.matchesPttCombo() && !this.pttKeyDown) {
            e.preventDefault();
            this.applyPttPressedState(true);
        }
    },

    handlePttKeyUp(e) {
        if (!this.pttEnabled) return;
        this.pttPressedCodes.delete(e.code);
        if (this.pttKeyDown && !this.matchesPttCombo()) {
            e.preventDefault();
            this.applyPttPressedState(false);
        }
    },

    playPttTone(isPressed) {
        if (!this.pttSoundEnabled) return;

        const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextCtor) return;

        if (!this.audioContext) {
            try {
                this.audioContext = new AudioContextCtor();
            } catch {
                return;
            }
        }

        const ctx = this.audioContext;
        if (ctx.state === 'suspended') {
            ctx.resume().catch(() => { });
        }

        try {
            const now = ctx.currentTime + 0.005;
            const holdTone = !!isPressed;
            const duration = holdTone ? 0.10 : 0.08;

            const oscillator = ctx.createOscillator();
            const gain = ctx.createGain();

            oscillator.type = 'sine';
            oscillator.frequency.setValueAtTime(holdTone ? 880 : 620, now);

            gain.gain.setValueAtTime(0.0001, now);
            gain.gain.exponentialRampToValueAtTime(0.06, now + 0.01);
            gain.gain.exponentialRampToValueAtTime(0.0001, now + duration);

            oscillator.connect(gain);
            gain.connect(ctx.destination);

            oscillator.start(now);
            oscillator.stop(now + duration);
            oscillator.onended = () => {
                try { oscillator.disconnect(); } catch { }
                try { gain.disconnect(); } catch { }
            };
        } catch {
            // Ignore tone failures (for example, transient audio device errors).
        }
    },

    applyPttPressedState(isPressed, shouldPlayTone = true) {
        if (!this.pttEnabled) return;

        const pressed = !!isPressed;
        const alreadyApplied =
            this.pttKeyDown === pressed &&
            ((pressed && !this.isMuted) || (!pressed && this.isMuted));
        if (alreadyApplied) return;

        this.pttKeyDown = pressed;
        if (shouldPlayTone) {
            this.playPttTone(pressed);
        }

        if (pressed) {
            this.ensureAudioContextActive();
            this.isMuted = false;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = true);
            }
            this.notifyDotNet('OnMuteChanged', false);
        } else {
            this.isMuted = true;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = false);
            }
            this.notifyDotNet('OnMuteChanged', true);
        }
    },

    /**
     * Called by native Windows global keyboard hook.
     */
    nativeSetPttPressed(isPressed) {
        if (!this.pttEnabled) return;
        this.applyPttPressedState(!!isPressed, true);
    },

    /**
     * Parse a shortcut binding string like:
     *  - "Space"
     *  - "Ctrl+KeyV"
     *  - "Ctrl+Shift+KeyT"
     */
    parsePttBinding(binding) {
        const result = { ctrl: false, alt: false, shift: false, meta: false, code: '' };
        if (!binding || typeof binding !== 'string') {
            result.code = 'Space';
            return result;
        }

        const tokens = binding.split('+').map(x => x.trim()).filter(Boolean);
        for (const token of tokens) {
            const normalized = token.toLowerCase();
            if (normalized === 'ctrl' || normalized === 'control' || normalized === 'controlleft' || normalized === 'controlright') {
                result.ctrl = true;
                continue;
            }
            if (normalized === 'alt' || normalized === 'altleft' || normalized === 'altright') {
                result.alt = true;
                continue;
            }
            if (normalized === 'shift' || normalized === 'shiftleft' || normalized === 'shiftright') {
                result.shift = true;
                continue;
            }
            if (normalized === 'meta' || normalized === 'metaleft' || normalized === 'metaright' || normalized === 'cmd' || normalized === 'win') {
                result.meta = true;
                continue;
            }

            result.code = token;
        }

        if (!result.code && !result.ctrl && !result.alt && !result.shift && !result.meta) {
            result.code = 'Space';
        }
        return result;
    },

    isCtrlPressed() {
        return this.pttPressedCodes.has('ControlLeft') || this.pttPressedCodes.has('ControlRight');
    },

    isAltPressed() {
        return this.pttPressedCodes.has('AltLeft') || this.pttPressedCodes.has('AltRight');
    },

    isShiftPressed() {
        return this.pttPressedCodes.has('ShiftLeft') || this.pttPressedCodes.has('ShiftRight');
    },

    isMetaPressed() {
        return this.pttPressedCodes.has('MetaLeft') || this.pttPressedCodes.has('MetaRight');
    },

    matchesPttCombo() {
        const binding = this.pttBinding || this.parsePttBinding(this.pttKey || 'Space');

        if (binding.ctrl && !this.isCtrlPressed()) return false;
        if (binding.alt && !this.isAltPressed()) return false;
        if (binding.shift && !this.isShiftPressed()) return false;
        if (binding.meta && !this.isMetaPressed()) return false;

        if (binding.code) {
            return this.pttPressedCodes.has(binding.code);
        }

        // Modifier-only combination.
        return binding.ctrl || binding.alt || binding.shift || binding.meta;
    },

    removePttListeners() {
        if (this._pttKeyDownHandler) {
            document.removeEventListener('keydown', this._pttKeyDownHandler);
            this._pttKeyDownHandler = null;
        }
        if (this._pttKeyUpHandler) {
            document.removeEventListener('keyup', this._pttKeyUpHandler);
            this._pttKeyUpHandler = null;
        }
        if (this._pttBlurHandler) {
            window.removeEventListener('blur', this._pttBlurHandler);
            this._pttBlurHandler = null;
        }
        if (this._pttVisibilityHandler) {
            document.removeEventListener('visibilitychange', this._pttVisibilityHandler);
            this._pttVisibilityHandler = null;
        }
    },

    handlePttFocusLost() {
        // When app/window loses focus, keyup may never arrive.
        // Force reset to safe muted state and clear pressed-key tracking.
        if (!this.pttEnabled) return;
        this.pttPressedCodes.clear();
        if (this.pttKeyDown || !this.isMuted) {
            this.applyPttPressedState(false, false);
        }
    },

    handlePttVisibilityChanged() {
        if (document.hidden) {
            this.handlePttFocusLost();
        }
    },

    // ─── Stop everything ────────────────────────────────────────────────────────

    stop(reason) {
        const stopReason = typeof reason === 'string' && reason.trim() ? reason.trim() : 'unspecified';
        this.log('stop', `Stopping WebRTC manager (reason=${stopReason})`);
        this.polling = false;
        if (this.pollAbort) {
            this.pollAbort.abort();
            this.pollAbort = null;
        }
        this.stopKeepalive();
        this.stopParticipantCheck();
        this.stopRemoteFeedHealthWatchdog();
        this.resetStreamInfoState();
        this.stopPublisherVideoPerformanceGuard();
        this.stopPublisherHealthWatchdog();
        this.clearScreenShareBitrateWarmupTimers();
        this.lastParticipantCheckAtMs = 0;
        this.lastRemoteFeedHealthRunAtMs = 0;
        this.lastStreamInfoRefreshAtMs = 0;
        this.remoteFeedHealthCursor = 0;
        this.selectedSpotlightStreamKey = null;
        this.controlOverlayHideTimers = new WeakMap();
        const videoGrid = document.getElementById('videoGrid');
        if (videoGrid) videoGrid.classList.remove('spotlight-mode');
        this.removePttListeners();
        this.pttEnabled = false;
        this.pttBinding = null;
        this.pttKeyDown = false;
        this.pttPressedCodes.clear();
        this.notifyHostPttConfig();
        this.releaseDtlnWasmCache();
        this.releaseRawMicTrack();
        this.flushPersistedRemoteIdentityVolumes();
        this.cancelScheduledRemoteAudioRoutingRefresh();
        this.clearAllTrickleBatches();
        this._displayParseCache = null;
        this._screenAudioScratch = null;
        this.stopWindowsScreenAudioFallback(true).catch(() => { });
        this.cleanupScreenShareAudioMixTrack().catch(() => { });
        this.stopScreenAudioPublisher('manager-stop').catch(() => { });
        this.cleanupMusicBotGraph().catch(() => { });
        this.stopMicTest().catch(() => { });
        this.musicBotActive = false;
        this.musicBotTitle = '';
        this.musicBotStreamUrl = '';
        this.setSystemScreenShareAppPlaybackSuppressed(false);
        if (document.fullscreenElement) {
            document.exitFullscreen?.().catch(() => { });
        }
        if (this._desktopFullscreenRequested) {
            this.notifyHostDesktopFullscreen('exit');
            this._desktopFullscreenRequested = false;
        }

        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
            this.localStream = null;
        }
        if (this.screenStream) {
            this.screenStream.getTracks().forEach(t => t.stop());
            this.screenStream = null;
        }

        this.stopVoiceGate();
        this._destroyDtlnWorklet();
        if (this.audioContext) {
            try { this.audioContext.close(); } catch { }
            this.audioContext = null;
            this.gainNode = null;
            this.audioDestination = null;
            this.voiceGateAnalyser = null;
        }
        this.resetRemoteSpeakingState();
        if (this.remotePlaybackAudioContext) {
            try { this.remotePlaybackAudioContext.close(); } catch { }
            this.remotePlaybackAudioContext = null;
            this.remotePlaybackAudioNodes = new WeakMap();
        }

        if (this.publisherPc) {
            this.publisherPc.close();
            this.publisherPc = null;
        }
        if (this.screenAudioPublisherPc) {
            try { this.screenAudioPublisherPc.close(); } catch { }
            this.screenAudioPublisherPc = null;
        }
        this.screenAudioPublisherHandleId = 0;
        this.screenAudioPublisherConfigured = false;
        this.screenAudioPublisherPendingCandidates = [];
        this.publisherConfigured = false;
        this.notifyPublisherReadyChanged();
        this.pendingJanusBitrateBps = 0;

        Object.keys(this.nativeScreenReceivers || {}).forEach(userId => {
            this.cleanupNativeScreenReceiver(userId, true);
        });
        this.nativeScreenReceivers = {};
        this.nativeScreenPendingCandidates = {};

        Object.values(this.feeds).forEach(f => f.pc.close());
        this.feeds = {};
        this.remoteFeedVolumes = {};
        this.remoteIdentityVolumes = {};
        this.hiddenRemoteFeeds = {};
        this.hiddenRemoteFeedMeta = {};
        this.pendingSubscribes = {};
        this.pendingSubscribeFeeds = {};

        const localVideo = document.getElementById('localVideo');
        if (localVideo) localVideo.srcObject = null;
        this.updateLocalFeedAvatarFallback();
        const localCell = document.querySelector('#videoGrid .local-cell');
        if (localCell) {
            const localPanel = localCell.querySelector('.video-info-panel');
            if (localPanel) localPanel.classList.remove('open');
            const localInfoBtn = localCell.querySelector('.video-info-btn');
            if (localInfoBtn) localInfoBtn.classList.remove('active');
        }

        // Remove all remote video cells
        this.restoreAnnotationToolbarHost();
        document.querySelectorAll('.video-cell:not(.local-cell)').forEach(el => el.remove());

        this.removeHostMessageListener();
        this.removeFullscreenSyncListener();
        this.dotNetRef = null;
    },

    // ─── Signal delivery (from C# CallStore → JS) ──────────────────────────────

    async deliverSignal(fromUserId, signalType, payload) {
        void fromUserId;
        void signalType;
        void payload;
        // For Janus SFU, direct peer signals are not used. Polling loop handles Janus events.
    },

    // ─── Device enumeration ─────────────────────────────────────────────────────

    async enumerateDevices() {
        try {
            let devices = await navigator.mediaDevices.enumerateDevices();

            // Until some media permission has been granted, browsers return the
            // device list with deviceId and label blanked out. Opening Settings
            // before ever joining a call is exactly that state, so every entry
            // arrived with an empty id - indistinguishable from "Default" - and the
            // pickers looked broken. Prime the permission once, then re-enumerate.
            const needsPermission = devices.some(d =>
                (d.kind === 'audioinput' || d.kind === 'videoinput' || d.kind === 'audiooutput')
                && !d.deviceId);

            if (needsPermission) {
                this.log('devices', 'Device ids hidden; requesting permission to reveal them');
                try {
                    const probe = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
                    probe.getTracks().forEach(t => { try { t.stop(); } catch { } });
                    devices = await navigator.mediaDevices.enumerateDevices();
                } catch (permErr) {
                    // Denied or no mic: fall through with whatever we have rather
                    // than failing the whole enumeration.
                    this.log('devices', `Permission probe failed (${permErr.name || permErr.message}); listing unlabeled devices`);
                }
            }

            const filtered = devices
                .filter(d => d.kind === 'audioinput' || d.kind === 'videoinput' || d.kind === 'audiooutput')
                .map(d => ({
                    deviceId: d.deviceId,
                    label: d.label || `${d.kind} (${(d.deviceId || 'default').slice(0, 8)})`,
                    kind: d.kind
                }));

            const counts = filtered.reduce((acc, d) => { acc[d.kind] = (acc[d.kind] || 0) + 1; return acc; }, {});
            this.log('devices', `Found ${filtered.length} devices (${JSON.stringify(counts)})`);
            this.notifyDotNet('OnDevicesEnumerated', JSON.stringify(filtered));
            return filtered;
        } catch (err) {
            this.logError('devices', 'enumerateDevices failed: ' + err.message);
            return [];
        }
    },

    /// Routes playback to a chosen output device. setSinkId is Chromium-only and
    /// rejects on elements that are not yet ready, so every call is best-effort and
    /// failures are logged rather than thrown.
    async setAudioOutputDevice(deviceId) {
        const sinkId = (deviceId || '').toString().trim();
        this.selectedSinkId = sinkId;

        const elements = [
            ...document.querySelectorAll('#videoGrid video, #videoGrid audio'),
            this.micTestAudioElement
        ].filter(Boolean);

        let applied = 0;
        for (const el of elements) {
            if (typeof el.setSinkId !== 'function') continue;
            try {
                await el.setSinkId(sinkId);
                applied++;
            } catch (err) {
                this.log('devices', `setSinkId failed on an element: ${err.message}`);
            }
        }

        this.log('devices', `Audio output routed to ${sinkId || 'system default'} (${applied} element(s))`);
        return applied;
    },

    /// Re-applies the chosen output device to an element that has just been created
    /// (new remote feed, mic-test element), so later joins honour the setting.
    applySelectedSinkTo(element) {
        const sinkId = (this.selectedSinkId || '').toString();
        if (!sinkId || !element || typeof element.setSinkId !== 'function') return;
        element.setSinkId(sinkId).catch(() => { });
    },

    // ─── Volume control ─────────────────────────────────────────────────────────

    setVolume(level) {
        this.ensureAudioContextActive();
        if (this.gainNode) {
            this.gainNode.gain.value = Math.max(0, Math.min(2, level));
            this.log('volume', `Set mic volume to ${level}`);
        }
    },

    setStreamVolume(level) {
        const clamped = Math.max(0, Math.min(2, Number(level) || 0));
        this.streamVolume = clamped;
        if (this.micTestAudioElement) {
            this.micTestAudioElement.volume = Math.max(0, Math.min(1, clamped));
        }
        this.log('volume', `Set stream volume to ${clamped}`);
        this.refreshRemoteAudioRouting();
    },

    async startMicTest(micDeviceId) {
        await this.stopMicTest();

        const normalizedMicId = (micDeviceId || '').toString().trim();
        const audioConstraint = normalizedMicId
            ? { deviceId: { exact: normalizedMicId } }
            : true;

        const stream = await navigator.mediaDevices.getUserMedia({
            audio: audioConstraint,
            video: false
        });

        const audioElement = document.createElement('audio');
        audioElement.autoplay = true;
        audioElement.playsInline = true;
        audioElement.muted = false;
        audioElement.volume = Math.max(0, Math.min(1, Number(this.streamVolume) || 1));
        audioElement.srcObject = stream;
        await audioElement.play();

        this.micTestStream = stream;
        this.micTestAudioElement = audioElement;
        this.micTestStreamIdentity = this.getMediaStreamIdentity(stream);
        this.log('audio', `Mic test started${normalizedMicId ? ` (device=${normalizedMicId})` : ''}`);
    },

    async stopMicTest() {
        if (this.micTestAudioElement) {
            try { this.micTestAudioElement.pause(); } catch { }
            try { this.micTestAudioElement.srcObject = null; } catch { }
        }
        this.micTestAudioElement = null;

        if (this.micTestStream) {
            const tracks = this.micTestStream.getTracks?.() || [];
            for (const track of tracks) {
                try { track.stop(); } catch { }
            }
        }

        this.micTestStream = null;
        this.micTestStreamIdentity = '';
    },

    // ─── Settings update ────────────────────────────────────────────────────────

    async updateSettings(settings) {
        this.log('settings', 'Applying settings:', settings);

        if (this.musicBotActive) {
            await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: true });
        }

        const bitrateKbps = this.clampPublisherBitrateKbps(
            settings.bitrateKbps ?? this.currentConstraints.bitrateKbps ?? 2000
        );
        const previousVideoCodec = this.normalizeVideoCodec(this.currentConstraints.preferredVideoCodec);
        const previousH264Override = this.normalizeH264ProfileOverrideValue(this.currentConstraints.h264ProfileOverride);
        const requestedVideoCodec = this.normalizeVideoCodec(
            settings.preferredVideoCodec ?? this.currentConstraints.preferredVideoCodec
        );
        const requestedH264Override = this.normalizeH264ProfileOverrideValue(
            settings.h264ProfileOverride ?? this.currentConstraints.h264ProfileOverride
        );
        const requestedScreenShareAudioEnabled =
            settings.screenShareAudioEnabled !== undefined
                ? settings.screenShareAudioEnabled !== false
                : this.currentConstraints.screenShareAudioEnabled !== false;
        const requestedScreenShareAutoGainEnabled =
            settings.screenShareAutoGainEnabled !== undefined
                ? settings.screenShareAutoGainEnabled !== false
                : this.currentConstraints.screenShareAutoGainEnabled !== false;
        const requestedNoiseSuppressionEnabled =
            settings.noiseSuppressionEnabled !== undefined
                ? settings.noiseSuppressionEnabled !== false
                : this.currentConstraints.noiseSuppressionEnabled !== false;
        const requestedHideLocalPreview =
            settings.hideLocalPreview !== undefined
                ? settings.hideLocalPreview === true
                : this.currentConstraints.hideLocalPreview === true;
        const requestedVoiceGateEnabled =
            settings.voiceGateEnabled !== undefined
                ? settings.voiceGateEnabled === true
                : this.currentConstraints.voiceGateEnabled === true;
        const requestedVoiceGateAuto =
            settings.voiceGateAuto !== undefined
                ? settings.voiceGateAuto !== false
                : this.currentConstraints.voiceGateAuto !== false;
        const requestedVoiceGateThreshold =
            settings.voiceGateThreshold !== undefined
                ? (parseInt(settings.voiceGateThreshold, 10) || 30)
                : (this.currentConstraints.voiceGateThreshold || 30);
        let needsPublisherStateRenegotiation = false;

        const nextConstraints = {
            micDeviceId: settings.micDeviceId || null,
            cameraDeviceId: settings.cameraDeviceId || null,
            resolution: settings.resolution || '1280x720',
            fps: settings.fps || 30,
            bitrateKbps,
            preferredVideoCodec: requestedVideoCodec,
            h264ProfileOverride: requestedH264Override,
            noiseSuppressionEnabled: requestedNoiseSuppressionEnabled,
            hideLocalPreview: requestedHideLocalPreview,
            screenShareAudioEnabled: requestedScreenShareAudioEnabled,
            screenShareAutoGainEnabled: requestedScreenShareAutoGainEnabled,
            voiceGateEnabled: requestedVoiceGateEnabled,
            voiceGateAuto: requestedVoiceGateAuto,
            voiceGateThreshold: requestedVoiceGateThreshold
        };

        // Only a device or capture-format change actually needs getUserMedia torn down and
        // re-run. Doing it unconditionally also closed the AudioContext, destroyed and rebuilt
        // the DTLN worklet, and re-enumerated devices — for a bitrate or voice-gate tweak.
        const previous = this.currentConstraints;
        const deviceChanged =
            (previous.micDeviceId || null) !== nextConstraints.micDeviceId
            || (previous.cameraDeviceId || null) !== nextConstraints.cameraDeviceId;
        const formatChanged =
            (previous.resolution || '1280x720') !== nextConstraints.resolution
            || (parseInt(previous.fps, 10) || 30) !== (parseInt(nextConstraints.fps, 10) || 30);
        const noiseSuppressionChanged =
            (previous.noiseSuppressionEnabled !== false) !== requestedNoiseSuppressionEnabled;

        const reacquiredLocalStream = deviceChanged || formatChanged || !this.localStream;

        if (reacquiredLocalStream) {
            await this.startLocalStream(nextConstraints);
        } else {
            Object.assign(this.currentConstraints, nextConstraints);
            this.normalizeCurrentConstraints('updateSettings');

            if (noiseSuppressionChanged) {
                // Inserting or removing the DTLN node is a graph change; nothing cheaper works.
                await this.setupAudioPipeline();
                // The pipeline rebuild swapped localStream's audio track, so the analyzer is now
                // bound to a dead one. Idempotent — no-ops when the stream identity is unchanged.
                this.ensureLocalSpeakingAnalyzer();
            } else {
                this.applyVoiceGateSettings();
            }

            // Mirror the DOM sync startLocalStream does after acquiring.
            this.updateLocalFeedAvatarFallback();
            this.ensureLocalFeedCellControls();
        }

        if (this.screenAudioFallbackActive) {
            this.applyWindowsScreenAudioFallbackGainMode('settings');
        }

        // Replace tracks in publisher PeerConnection
        if (this.publisherPc && this.localStream) {
            const active = (this.isScreenSharing && this.screenStream)
                ? this.screenStream : this.localStream;
            active.getTracks().forEach(track => {
                const s = this.findSenderByKind(this.publisherPc, track.kind);
                if (s) s.replaceTrack(track);
            });

            if (this.isScreenSharing) {
                const activeFps = parseInt(settings.fps ?? this.currentConstraints.fps, 10) || 30;
                const audioSender = this.findSenderByKind(this.publisherPc, 'audio');
                const micTrack = this.localStream.getAudioTracks()[0] || null;

                // The settings switch governs only the native fallback capture.
                // Browser display audio, once granted in the picker, keeps flowing
                // regardless of the switch.
                const nativeFallbackAllowed = this.currentConstraints.screenShareAudioEnabled !== false;

                // Mid-share ENABLE: routing below can only use a track that already
                // exists, and getDisplayMedia audio cannot be added without re-picking.
                // So when the toggle comes on and the share has no audio track, engage
                // the native fallback now with the share's remembered target - without
                // this the switch silently did nothing until the next share.
                if (nativeFallbackAllowed
                    && this.screenStream
                    && this.screenStream.getAudioTracks().length === 0
                    && !this.screenAudioFallbackActive) {
                    const target = this.activeScreenShareTarget || {};
                    const midShareFallbackTrack = await this.startWindowsScreenAudioFallback({
                        preferProcessIsolation: target.shareTarget !== 'system',
                        allowSystemFallback: target.shareTarget === 'system' || target.shareTarget === 'unknown',
                        targetLabel: target.targetLabel || '',
                        displaySurface: target.displaySurface || ''
                    });
                    if (midShareFallbackTrack) {
                        this.screenStream.addTrack(midShareFallbackTrack);
                        this.log('screenshare', 'Settings enable: engaged native audio fallback mid-share');
                    }
                }

                // Mid-share DISABLE: stop the native capture (and its WASAPI session).
                // Display audio from the browser is deliberately left alone.
                if (!nativeFallbackAllowed && this.screenAudioFallbackActive) {
                    await this.stopWindowsScreenAudioFallback(true);
                }

                // Ended tracks must not win the preferred-track selection.
                if (this.screenStream) {
                    for (const staleTrack of this.screenStream.getAudioTracks()) {
                        if (staleTrack.readyState === 'ended') {
                            try { this.screenStream.removeTrack(staleTrack); } catch { }
                        }
                    }
                }

                const screenAudioTrack = this.selectPreferredScreenShareAudioTrack(this.screenStream);
                let nextMainAudioTrack = micTrack;
                let separateScreenAudioReady = false;

                if (screenAudioTrack) {
                    separateScreenAudioReady = await this.ensureScreenAudioPublisher(screenAudioTrack);
                    if (!separateScreenAudioReady) {
                        nextMainAudioTrack = await this.buildScreenShareOutgoingAudioTrack(screenAudioTrack, micTrack);
                    }
                } else {
                    await this.stopScreenAudioPublisher('no-screen-audio-track');
                }

                if (audioSender) {
                    await audioSender.replaceTrack(nextMainAudioTrack);
                    await this.enforceSinglePublisherAudioSender(nextMainAudioTrack);
                    if (!separateScreenAudioReady && screenAudioTrack) {
                        const fallbackIsMixed = !!(nextMainAudioTrack && micTrack && nextMainAudioTrack.id !== micTrack.id);
                        this.log(
                            'screenshare',
                            fallbackIsMixed
                                ? 'Settings update kept mixed mic+screen fallback on main publisher'
                                : 'Settings update kept microphone fallback on main publisher'
                        );
                    }
                }

                const screenTrack = this.screenStream?.getVideoTracks?.()[0] || null;
                await this.tuneScreenShareVideoTrackForPerformance(
                    screenTrack,
                    activeFps,
                    'screenshare-settings',
                    { enforceResolutionCap: true }
                );

                const videoSender = this.findSenderByKind(this.publisherPc, 'video');
                await this.tuneScreenShareVideoSenderForPerformance(videoSender, activeFps, 'screenshare-settings');
            } else {
                const localVideoTrack = this.localStream.getVideoTracks()[0] || null;
                const videoSender = this.findSenderByKind(this.publisherPc, 'video');
                const videoTransceiver = this.publisherPc.getTransceivers().find(t => t.sender === videoSender)
                    || this.publisherPc.getTransceivers().find(t =>
                        t.receiver?.track?.kind === 'video' || t.mid === '1');

                if (this.isCameraOff || !localVideoTrack) {
                    if (videoSender) {
                        await videoSender.replaceTrack(null);
                    }
                    if (videoTransceiver && (videoTransceiver.direction === 'sendrecv' || videoTransceiver.direction === 'sendonly')) {
                        videoTransceiver.direction = 'recvonly';
                        needsPublisherStateRenegotiation = true;
                    }
                } else if (videoSender) {
                    await videoSender.replaceTrack(localVideoTrack);
                    if (videoTransceiver && videoTransceiver.direction !== 'sendrecv') {
                        videoTransceiver.direction = 'sendrecv';
                        needsPublisherStateRenegotiation = true;
                    }
                }
            }
        }

        // Apply bitrate setting
        await this.applyBitrate(bitrateKbps);
        if (this.isScreenSharing) {
            await this.requestPublisherKeyframe('screenshare-settings');
            this.scheduleScreenShareBitrateWarmup();
        }

        // Codec and transceiver-direction changes need renegotiation to take effect.
        const h264ProfileChanged = previousH264Override !== requestedH264Override;
        const needsH264ProfileRenegotiation = requestedVideoCodec === 'h264' && h264ProfileChanged;
        if (this.publisherPc && (needsPublisherStateRenegotiation || previousVideoCodec !== requestedVideoCodec || needsH264ProfileRenegotiation)) {
            if (previousVideoCodec !== requestedVideoCodec) {
                this.log('settings', `Preferred video codec changed ${previousVideoCodec} -> ${requestedVideoCodec}`);
            }
            if (needsH264ProfileRenegotiation) {
                const effective = this.resolveEffectiveH264Override();
                if (effective.profileLevelId) {
                    this.log(
                        'settings',
                        `H264 profile preference changed ${previousH264Override || 'auto'} -> ${requestedH264Override || 'auto'} (effective ${effective.profileLevelId}:${effective.packetizationMode ?? 0})`
                    );
                } else {
                    this.log('settings', `H264 profile preference changed ${previousH264Override || 'auto'} -> ${requestedH264Override || 'auto'}`);
                }
            }
            const renegotiateTag = needsPublisherStateRenegotiation && previousVideoCodec !== requestedVideoCodec
                ? 'settings-camera-codec'
                : (needsPublisherStateRenegotiation
                    ? 'settings-camera-state'
                    : (needsH264ProfileRenegotiation ? 'settings-h264-profile' : 'settings-codec'));
            await this.renegotiatePublisher(renegotiateTag);
        }

        // Re-enumerate to refresh labels (some browsers only show labels after permission).
        // Labels can only have changed if we actually went through getUserMedia again.
        if (reacquiredLocalStream) {
            await this.enumerateDevices();
        }
    },

    rebindVideoElements() {
        this.ensureVideoPauseWatchdog();
        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            if (this.isScreenSharing && this.screenStream) {
                localVideo.srcObject = this.screenStream;
            } else if (this.localStream) {
                localVideo.srcObject = this.localStream;
            }
        }
        this.updateLocalFeedAvatarFallback();
        this.ensureLocalFeedCellControls();

        for (const [feedId, feed] of Object.entries(this.feeds)) {
            if (!feed) continue;

            let stream = feed.stream || null;
            if (!stream && feed.pc) {
                const tracks = feed.pc.getReceivers()
                    .map(r => r.track)
                    .filter(t => t && t.readyState === 'live');
                if (tracks.length > 0) {
                    stream = new MediaStream(tracks);
                    feed.stream = stream;
                }
            }
            stream = this.normalizeRemotePlaybackStream(stream, feedId);
            if (stream) {
                feed.stream = stream;
            }

            const cell = this.ensureRemoteFeedCell(feedId, feed.display || '');
            if (!cell) continue;
            const vid = cell.querySelector('video');
            if (!vid) continue;
            this.configureRemoteVideoElement(vid, feedId);

            if (stream) {
                vid.srcObject = stream;
                this.ensureRemoteSpeakingAnalyzer(feedId, stream);
            } else {
                this.removeRemoteSpeakingAnalyzer(feedId);
            }
            this.applyRemoteFeedVolume(feedId, vid);
        }

        this.applySpotlightLayout();
    },

    // ─── Floating video helpers ─────────────────────────────────────────────────

    /**
     * Clone the local video stream into a floating overlay container.
     */
    moveVideoToFloat(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const floatVideo = container.querySelector('video');
        if (!floatVideo) return;

        const primaryRemote = this.getPrimaryRemoteFeedForFloat();
        if (primaryRemote?.stream) {
            const normalized = primaryRemote.isLocal
                ? primaryRemote.stream
                : this.normalizeRemotePlaybackStream(primaryRemote.stream, primaryRemote.feedId);
            floatVideo.srcObject = normalized;
            if (primaryRemote.isLocal) {
                delete floatVideo.dataset.feedId;
            } else {
                floatVideo.dataset.feedId = String(primaryRemote.feedId);
            }
            this.detachRemoteFeedElementGain(floatVideo);
            // Keep float preview silent to avoid duplicate playback sinks.
            floatVideo.muted = true;
            floatVideo.volume = 0;
            floatVideo.play?.().catch(() => { });
            this.log('float', `Bound ${primaryRemote.isLocal ? 'local spotlight' : `remote feed ${primaryRemote.feedId}`} to #${containerId}`);
            return;
        }

        const fallback = this.isScreenSharing && this.screenStream ? this.screenStream : this.localStream;
        if (fallback) {
            floatVideo.srcObject = fallback;
            delete floatVideo.dataset.feedId;
            this.detachRemoteFeedElementGain(floatVideo);
            floatVideo.muted = true;
            floatVideo.volume = 0;
            floatVideo.play?.().catch(() => { });
            this.log('float', `Bound local stream to #${containerId}`);
            return;
        }

        floatVideo.srcObject = null;
        delete floatVideo.dataset.feedId;
        this.detachRemoteFeedElementGain(floatVideo);
        floatVideo.muted = true;
        floatVideo.volume = 0;
    },

    /**
     * Clear the floating video container.
     */
    clearFloatVideo(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const floatVideo = container.querySelector('video');
        if (floatVideo) {
            this.detachRemoteFeedElementGain(floatVideo);
            floatVideo.srcObject = null;
            delete floatVideo.dataset.feedId;
            floatVideo.muted = true;
            floatVideo.volume = 0;
        }
        this.log('float', `Cleared float video in #${containerId}`);
    }
};


