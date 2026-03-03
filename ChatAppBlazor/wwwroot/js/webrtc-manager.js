'use strict';

/**
 * WebRTC Manager â€” First-class Janus SFU integration for Blazor Hybrid.
 *
 * Manages the complete Janus videoroom lifecycle:
 *   - Publisher: local media â†’ Janus â†’ SFU
 *   - Subscribers: one handle per remote feed (two-phase via polling)
 *   - Trickle ICE, keepalive, long-poll event loop
 *
 * Communication with C# via DotNetObjectReference (passed in init).
 */
window.webrtcManager = {
    // â”€â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    localStream: null,
    screenStream: null,
    isMuted: true,
    isCameraOff: true,
    isScreenSharing: false,

    // Janus session (publisher)
    janusSessionId: 0,
    janusHandleId: 0,
    janusRoomId: 0,
    myPrivateId: 0,

    // Per-feed subscriber handles: feedId â†’ { handleId, pc }
    feeds: {},
    remoteFeedVolumes: {},
    remoteIdentityVolumes: {},
    streamVolume: 1.0,
    publisherStreamHints: {},

    // Pending subscriber joins: handleId â†’ { feedId, display }
    // Tracks handles waiting for JSEP offer from Janus via polling loop.
    pendingSubscribes: {},
    pendingSubscribeTimeoutMs: 5000,

    // Publisher PeerConnection
    publisherPc: null,
    publisherConfigured: false,
    pendingJanusBitrateBps: 0,

    // Auth / server
    userId: '',
    userDisplayName: '',
    participantDirectory: {},
    serverBaseUrl: '',
    authToken: '',

    // ICE
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],

    // Polling / keepalive / participant discovery
    polling: false,
    pollAbort: null,
    keepaliveTimer: null,
    participantCheckTimer: null,
    keepaliveFailures: 0,

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
        screenShareAudioEnabled: true
    },
    minPublisherBitrateKbps: 1000,
    maxPublisherBitrateKbps: 20000,
    enforcePublisherCbr: true,
    // Applied to outgoing SDP when H264 is present/selected.
    h264ProfileLevelId: '64e029',
    h264ProfileLevelIdFallbacks: ['4de01f', '42e01f'],
    receiverAudioJitterBufferTargetMs: 60,
    receiverVideoJitterBufferTargetMs: 90,
    receiverAudioPlayoutDelayHintS: 0.06,
    receiverVideoPlayoutDelayHintS: 0.10,
    audioContext: null,
    remotePlaybackAudioContext: null,
    remotePlaybackAudioNodes: new WeakMap(),
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
    screenAudioFallbackTrack: null,
    screenAudioFallbackNextTime: 0,
    screenShareAudioMixContext: null,
    screenShareAudioMixTrack: null,

    // Stream info overlays / stats sampling
    streamInfoIntervalId: null,
    openStreamInfoPanels: {},
    streamStatsCache: {},
    selectedSpotlightStreamKey: null,
    screenShareSystemAudioSuppressed: false,
    screenShareBitrateWarmupTimers: [],
    publisherVideoPerfGuardIntervalId: null,
    publisherVideoPerfGuardState: null,
    publisherVideoPerfGuardIntervalMs: 2000,
    publisherHealthIntervalId: null,
    publisherHealthState: null,

    // â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    async tuneScreenShareVideoTrackForPerformance(videoTrack, targetFps, scope = 'screenshare', options = null) {
        if (!videoTrack) return;

        const desiredFps = Math.max(1, parseInt(targetFps, 10) || 30);
        const minFps = desiredFps >= 30 ? 30 : Math.max(5, Math.floor(desiredFps * 0.75));
        const targetResolution = this.parseResolutionSetting(this.currentConstraints?.resolution, 1920, 1080);
        const enforceResolutionCap = options?.enforceResolutionCap !== false;

        if (typeof videoTrack.applyConstraints === 'function') {
            try {
                const constraints = {
                    frameRate: { min: minFps, ideal: desiredFps, max: desiredFps }
                };
                if (enforceResolutionCap) {
                    constraints.width = { ideal: targetResolution.width, max: targetResolution.width };
                    constraints.height = { ideal: targetResolution.height, max: targetResolution.height };
                }
                await videoTrack.applyConstraints(constraints);
                this.log(
                    'screenshare',
                    `${scope}: applied video constraints ${enforceResolutionCap ? `${targetResolution.width}x${targetResolution.height} ` : ''}frameRate=${minFps}/${desiredFps}/${desiredFps}`
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

        try {
            const params = videoSender.getParameters() || {};
            if (!Array.isArray(params.encodings) || params.encodings.length === 0) {
                params.encodings = [{}];
            }

            params.degradationPreference = 'maintain-framerate';
            params.encodings[0].maxFramerate = desiredFps;
            params.encodings[0].minBitrate = minBitrateBps;
            params.encodings[0].maxBitrate = maxBitrateBps;
            params.encodings[0].priority = 'high';
            if ('networkPriority' in params.encodings[0]) {
                params.encodings[0].networkPriority = 'high';
            }
            if (Number.isFinite(requestedScale) && requestedScale >= 1) {
                params.encodings[0].scaleResolutionDownBy = requestedScale;
            } else if (!Number.isFinite(params.encodings[0].scaleResolutionDownBy) || params.encodings[0].scaleResolutionDownBy < 1) {
                params.encodings[0].scaleResolutionDownBy = 1;
            }

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

    isAndroidWebRtcEnvironment() {
        try {
            const ua = (navigator?.userAgent || '').toString().toLowerCase();
            return ua.includes('android');
        } catch {
            return false;
        }
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

        const delays = [250, 900, 2000];
        for (const delayMs of delays) {
            const timerId = setTimeout(() => {
                if (!this.isScreenSharing || !this.publisherPc) return;
                this.applyBitrate(this.currentConstraints?.bitrateKbps || 2000).catch(() => { });
                this.requestPublisherKeyframe(`screenshare-warmup-${delayMs}ms`).catch(() => { });
            }, delayMs);
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

        const senderParams = videoSender.getParameters?.() || {};
        const senderScale = Number(senderParams?.encodings?.[0]?.scaleResolutionDownBy);
        if (Number.isFinite(senderScale) && senderScale >= 1) {
            state.scaleResolutionDownBy = senderScale;
        }

        if (isEncoderLimited && hasLowFps) {
            state.lowFpsStreak += 1;
            state.recoveryStreak = 0;
        } else if (qualityLimitationReason === 'none' && Number.isFinite(measuredFps) && measuredFps >= recoveryFpsThreshold) {
            state.recoveryStreak += 1;
            state.lowFpsStreak = 0;
        } else {
            state.lowFpsStreak = 0;
            state.recoveryStreak = 0;
        }

        state.lastReason = qualityLimitationReason;
        state.lastFps = measuredFps;

        if (state.lowFpsStreak >= 3) {
            state.lowFpsStreak = 0;
            const nextScale = Math.min(2.5, Math.round((state.scaleResolutionDownBy + 0.25) * 100) / 100);
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

        if (state.recoveryStreak >= 5 && state.scaleResolutionDownBy > 1.01) {
            state.recoveryStreak = 0;
            const nextScale = Math.max(1, Math.round((state.scaleResolutionDownBy - 0.25) * 100) / 100);
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
        const startKbps = Math.max(minKbps, Math.min(maxKbps, Math.round(maxKbps * 0.9)));
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

    normalizeVideoCodec(codec) {
        const normalized = (codec || 'h264').toString().trim().toLowerCase();
        if (normalized === 'auto') return 'h264';
        return ['h264', 'vp8', 'vp9', 'h265'].includes(normalized) ? normalized : 'h264';
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
        }
        return support;
    },

    getSupportedVideoCodecs() {
        const support = this.getBrowserVideoCodecSupport();
        const ordered = ['h264', 'h265', 'vp9', 'vp8'];
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
                const mid = (s.mid || '').toString().trim();
                if (mid) entry.mid = mid;
                out.push(entry);
            }
        }

        if (out.length > 0) return out;
        if (Number.isFinite(feedNum)) return [{ feed: feedNum }];
        return [];
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
        const normalized = this.normalizeSubscribeStreams(streams, feedId);
        let hasVideo = false;
        if (Array.isArray(streams)) {
            hasVideo = streams.some(s =>
                s
                && s.type === 'video'
                && s.disabled !== true
                && s.active !== false
                && s.send !== false);
        }
        this.publisherStreamHints[key] = { streams: normalized, raw: Array.isArray(streams) ? streams : [], hasVideo };
    },

    hasIncomingVideoTrack(pc) {
        if (!pc || typeof pc.getReceivers !== 'function') return false;
        return pc.getReceivers().some(r => r?.track?.kind === 'video' && r.track.readyState === 'live');
    },

    scheduleExistingSubscriberRefresh(feedId, delayMs, reason) {
        const feed = this.feeds[feedId];
        if (!feed?.handleId) return;

        const delay = Math.max(0, parseInt(delayMs, 10) || 0);
        const now = Date.now();
        if ((feed.nextRefreshAt || 0) > now) return;
        feed.nextRefreshAt = now + delay + 400;

        setTimeout(() => {
            this.requestExistingSubscriberRefresh(feedId, reason).catch(() => { });
        }, delay);
    },

    async requestExistingSubscriberRefresh(feedId, reason) {
        const feed = this.feeds[feedId];
        if (!feed?.handleId) return;
        if (!feed.pc) return;
        if (this.hasIncomingVideoTrack(feed.pc)) return;

        const hint = this.publisherStreamHints[String(feedId)];
        if (!hint?.hasVideo) return;

        const streams = this.normalizeSubscribeStreams(hint.streams, feedId);
        this.log('subscribe', `Requesting subscriber refresh for feed ${feedId} (handle ${feed.handleId}), reason=${reason}`);

        const result = await this.apiPost(
            `/api/janus/session/${this.janusSessionId}/handle/${feed.handleId}/message-nowait`,
            {
                body: {
                    request: 'subscribe',
                    streams,
                    offer_audio: true,
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

    parsePublisherDisplay(display) {
        const fallback = { userId: '', displayName: '' };
        if (!display || typeof display !== 'string') return fallback;

        const trimmed = display.trim();
        if (!trimmed) return fallback;

        if (trimmed.startsWith('{') && trimmed.endsWith('}')) {
            try {
                const parsed = JSON.parse(trimmed);
                return {
                    userId: (parsed.userId || '').toString(),
                    displayName: (parsed.displayName || parsed.name || '').toString()
                };
            } catch {
                // Fall through to legacy parsing.
            }
        }

        // Legacy mode: plain display string was either a GUID or a raw label.
        if (/^[0-9a-fA-F-]{20,}$/.test(trimmed)) {
            return { userId: trimmed, displayName: '' };
        }

        return { userId: '', displayName: trimmed };
    },

    buildPublisherDisplay() {
        const userId = (this.userId || '').toString();
        const displayName = (this.userDisplayName || userId || 'Unknown').toString();
        return JSON.stringify({ userId, displayName });
    },

    isSelfPublisherDisplay(display) {
        const parsed = this.parsePublisherDisplay(display);
        if (parsed.userId && this.userId && this.normalizeUserId(parsed.userId) === this.normalizeUserId(this.userId)) return true;
        return display === this.userId;
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

    bindSpotlightInteraction(cell, streamKey, allowTileClick = true) {
        if (!cell) return;
        cell.dataset.streamKey = streamKey;
        cell.dataset.allowTileSpotlight = allowTileClick ? '1' : '0';
        if (cell.dataset.spotlightBound === '1') return;

        cell.addEventListener('click', (ev) => {
            if (cell.dataset.allowTileSpotlight !== '1') {
                return;
            }
            if (ev.target.closest('.video-controls') || ev.target.closest('.video-info-panel')) {
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

    ensureLocalFeedCellControls() {
        const cell = document.querySelector('#videoGrid .local-cell');
        if (!cell) return;

        // Local stream should stay in the side list by default; only the explicit
        // Center button can promote it to spotlight.
        this.bindSpotlightInteraction(cell, 'local', false);

        let controls = cell.querySelector('.video-controls');
        if (!controls) {
            controls = document.createElement('div');
            controls.className = 'video-controls';
            cell.appendChild(controls);
        }
        this.ensureSpotlightButton(controls, 'local');

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

        this.applySpotlightLayout();
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
        } catch (err) {
            this.log('stats', `getStats failed for ${streamKey}: ${err.message}`);
        }

        return snapshot;
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

            // H265 preferred: fallback to H264 only.
            if (preferred === 'h265') {
                fallbackCodecs = primaryCodecs.filter(c => isMime(c, h264Mimes) && !preferredCodecs.includes(c));
            }

            // H264 preferred: fallback to VP8 only.
            if (preferred === 'h264') {
                fallbackCodecs = primaryCodecs.filter(c => isMime(c, vp8Mimes) && !preferredCodecs.includes(c));
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
        this.updateParticipantDirectory(participantDirectory || {});
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
            throw new Error(`POST ${path} â†’ ${resp.status}: ${text}`);
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
        if (!resp.ok) throw new Error(`GET ${path} â†’ ${resp.status}`);
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
        };
        document.addEventListener('fullscreenchange', this._fullscreenChangeHandler);
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

    log(area, msg, ...extra) {
        console.log(`[WebRTC:${area}]`, msg, ...extra);
    },

    logError(area, msg, ...extra) {
        console.error(`[WebRTC:${area}]`, msg, ...extra);
        this.notifyDotNet('OnError', `${area}: ${msg}`);
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

    async markPublisherConfigured(source) {
        if (this.publisherConfigured) return;
        this.publisherConfigured = true;
        this.log('publish', `Publisher configured (${source})`);
        this.startPublisherHealthWatchdog();

        if (this.pendingJanusBitrateBps > 0) {
            const pending = this.pendingJanusBitrateBps;
            this.pendingJanusBitrateBps = 0;
            await this.sendJanusBitrateConfigure(pending);
        }
    },

    // â”€â”€â”€ Init â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    async init(config) {
        this.janusSessionId = config.janusSessionId;
        this.janusHandleId = config.janusHandleId;
        this.janusRoomId = config.janusRoomId;
        this.serverBaseUrl = config.serverBaseUrl;
        this.authToken = config.authToken;
        this.userId = config.userId;
        this.userDisplayName = config.userDisplayName || config.userId || '';
        this.updateParticipantDirectory(config.participantDirectory || {});
        this.dotNetRef = config.dotNetRef;
        this.installHostMessageListener();
        this.ensureFullscreenSyncListener();

        if (config.iceServers) {
            try { this.iceServers = JSON.parse(config.iceServers); } catch { }
        }

        this.isMuted = true;
        this.isCameraOff = true;
        this.isScreenSharing = false;
        this.pendingSubscribes = {};
        this.publisherStreamHints = {};
        this.publisherConfigured = false;
        this.pendingJanusBitrateBps = 0;
        this.stopPublisherVideoPerformanceGuard();
        this.stopPublisherHealthWatchdog();
        this.clearScreenShareBitrateWarmupTimers();

        this.log('init', `session=${config.janusSessionId}, handle=${config.janusHandleId}, room=${config.janusRoomId}`);
        this.log('init', `serverBaseUrl=${config.serverBaseUrl}`);

        // Connectivity test â€” verify the server API is reachable
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

            // Start polling FIRST â€” it will handle subscriber JSEP responses
            this.startPolling();
            this.startKeepalive();
            this.startParticipantCheck();

            // Apply initial bitrate to publisher (deferred until configured if needed)
            await this.applyBitrate(this.currentConstraints.bitrateKbps);

            // Now initiate subscribes for any existing publishers (fire-and-forget)
            if (existingPublishers.length > 0) {
                this.log('init', `Subscribing to ${existingPublishers.length} existing publisher(s)...`);
                for (const pub of existingPublishers) {
                    await this.initiateSubscribe(pub.id, pub.display, pub.streams);
                }
            }
        } catch (err) {
            this.logError('init', 'Failed to publish: ' + err.message);
        }
    },

    // â”€â”€â”€ Media â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    async startLocalStream(constraints) {
        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
        }

        // Merge with stored constraints
        if (constraints) {
            Object.assign(this.currentConstraints, constraints);
        }
        const c = this.currentConstraints;
        c.preferredVideoCodec = this.normalizeVideoCodec(c.preferredVideoCodec);
        c.screenShareAudioEnabled = c.screenShareAudioEnabled !== false;
        c.bitrateKbps = this.clampPublisherBitrateKbps(c.bitrateKbps);

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

        const audioConstraint = c.micDeviceId
            ? { deviceId: { exact: c.micDeviceId } }
            : true;

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

        if (c.micDeviceId || c.cameraDeviceId) {
            attempts.push({
                label: 'audio+video (default devices)',
                constraints: { audio: true, video: defaultVideoConstraint }
            });
        }

        attempts.push({
            label: 'audio-only',
            constraints: { audio: audioConstraint, video: false }
        });

        if (c.micDeviceId) {
            attempts.push({
                label: 'audio-only (default mic)',
                constraints: { audio: true, video: false }
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

        // Ensure the local cell has controls/info panel after each re-render or stream refresh.
        this.ensureLocalFeedCellControls();

        // Set up audio volume control via Web Audio API
        this.setupAudioPipeline();

        // Apply current mute/camera state
        this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
        this.localStream.getVideoTracks().forEach(t => t.enabled = !this.isCameraOff);
    },

    setupAudioPipeline() {
        // Clean up previous
        if (this.audioContext) {
            try { this.audioContext.close(); } catch { }
        }

        const audioTrack = this.localStream?.getAudioTracks()[0];
        if (!audioTrack) return;

        this.audioContext = new AudioContext();

        // Resume if suspended (browser autoplay policy / app backgrounding)
        if (this.audioContext.state === 'suspended') {
            this.audioContext.resume().catch(err =>
                this.log('audio', 'Failed to resume AudioContext: ' + err.message));
        }

        const source = this.audioContext.createMediaStreamSource(new MediaStream([audioTrack]));
        this.gainNode = this.audioContext.createGain();

        // Route through a MediaStreamDestination so gain control works WITHOUT
        // playing audio through the speakers (which caused echo).
        this.audioDestination = this.audioContext.createMediaStreamDestination();
        source.connect(this.gainNode);
        this.gainNode.connect(this.audioDestination);

        // Replace the raw audio track with the gain-processed track
        const processedTrack = this.audioDestination.stream.getAudioTracks()[0];
        this.localStream.removeTrack(audioTrack);
        this.localStream.addTrack(processedTrack);

        // Also replace in publisher PeerConnection if already set up
        if (this.publisherPc) {
            const audioSender = this.publisherPc.getSenders()
                .find(s => s.track?.kind === 'audio');
            if (audioSender) {
                audioSender.replaceTrack(processedTrack);
            }
        }

        this.log('audio', 'Audio pipeline set up (gain â†’ MediaStreamDestination, no echo)');
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

    async buildBaseOutgoingAudioTrack() {
        if (this.isScreenSharing) {
            return await this.buildScreenShareOutgoingAudioTrack(
                this.screenStream?.getAudioTracks?.()[0] || null,
                this.localStream?.getAudioTracks?.()[0] || null
            );
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
        const destination = context.createMediaStreamDestination();
        gain.connect(destination);
        gain.gain.value = 1.0;

        const track = destination.stream.getAudioTracks()[0];
        if (!track) {
            try { context.close(); } catch { }
            throw new Error('Failed to create fallback audio track');
        }

        this.screenAudioFallbackContext = context;
        this.screenAudioFallbackGain = gain;
        this.screenAudioFallbackDestination = destination;
        this.screenAudioFallbackTrack = track;
    },

    decodeBase64ToBytes(base64Data) {
        const raw = atob(base64Data);
        const out = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) {
            out[i] = raw.charCodeAt(i);
        }
        return out;
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

        for (let ch = 0; ch < channels; ch++) {
            const channelData = buffer.getChannelData(ch);
            for (let i = 0; i < frameCount; i++) {
                channelData[i] = interleaved[i * channels + ch];
            }
        }

        const source = this.screenAudioFallbackContext.createBufferSource();
        source.buffer = buffer;
        source.connect(this.screenAudioFallbackGain);

        const now = this.screenAudioFallbackContext.currentTime;
        const startAt = Math.max(now + 0.01, this.screenAudioFallbackNextTime || now);
        source.start(startAt);
        this.screenAudioFallbackNextTime = startAt + buffer.duration;
    },

    async stopWindowsScreenAudioFallback(notifyHost = true) {
        if (notifyHost) {
            this.notifyHostScreenAudioFallback('stop');
        }

        this.screenAudioFallbackActive = false;
        this.screenAudioFallbackNextTime = 0;

        if (this.screenStream && this.screenAudioFallbackTrack) {
            try { this.screenStream.removeTrack(this.screenAudioFallbackTrack); } catch { }
        }

        if (this.screenAudioFallbackTrack) {
            try { this.screenAudioFallbackTrack.stop(); } catch { }
        }
        this.screenAudioFallbackTrack = null;
        this.screenAudioFallbackDestination = null;
        this.screenAudioFallbackGain = null;

        if (this.screenAudioFallbackContext) {
            try { await this.screenAudioFallbackContext.close(); } catch { }
        }
        this.screenAudioFallbackContext = null;
    },

    // â”€â”€â”€ Publisher â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /**
     * Publishes own feed to Janus. Uses the sync /message endpoint because
     * the polling loop hasn't started yet (no event queue competition).
     * Returns an array of existing publishers to subscribe to.
     */
    async publishOwnFeed() {
        const pc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.publisherPc = pc;
        this.publisherConfigured = false;

        // Monitor publisher connection state â€” detect ICE/DTLS failures
        pc.oniceconnectionstatechange = () => {
            const state = pc.iceConnectionState;
            this.log('publish', `ICE connection state: ${state}`);
            if (state === 'failed') {
                this.logError('publish', 'Publisher ICE connection FAILED â€” Janus cannot receive our media');
                this.notifyDotNet('OnPublisherConnectionFailed');
            } else if (state === 'disconnected') {
                this.log('publish', 'Publisher ICE disconnected â€” may recover or fail');
            } else if (state === 'connected' || state === 'completed') {
                this.log('publish', 'Publisher ICE connected to Janus');
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
                this.log('publish', 'No audio track â€” added recvonly audio transceiver');
            }
            if (!hasVideo) {
                pc.addTransceiver('video', { direction: 'recvonly' });
                this.log('publish', 'No video track â€” added recvonly video transceiver');
            }
        } else {
            // No local media at all â€” add recvonly so Janus gets a valid SDP
            pc.addTransceiver('audio', { direction: 'recvonly' });
            pc.addTransceiver('video', { direction: 'recvonly' });
            this.log('publish', 'No local media â€” added recvonly transceivers');
        }

        // Buffer ICE candidates until after joinandconfigure completes
        const candidateBuffer = [];
        let handleConfigured = false;

        pc.onicecandidate = async ({ candidate }) => {
            if (!handleConfigured) {
                candidateBuffer.push(candidate);
                return;
            }
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/trickle`,
                    candidate
                );
            } catch (err) {
                this.logError('trickle', 'ICE trickle failed: ' + err.message);
            }
        };

        // Create offer with codec/profile fallbacks and browser-default recovery.
        const offer = await this.createPublisherOfferWithCodecRecovery(pc, 'publish');
        this.logCodecSummary(pc, 'publish-offer');
        this.log('publish', 'Created SDP offer, sending joinandconfigure...');

        // Join as publisher with SDP offer (sync â€” polling not started yet)
        let joinResult;
        const preferredJanusCodec = this.getJanusRequestedVideoCodec();
        let requestedJanusCodec = this.getJanusVideoCodecForSdp(offer.sdp, preferredJanusCodec);
        const sendJoinAndConfigure = async (localOffer, videocodec, reasonTag) => {
            const body = {
                request: 'joinandconfigure',
                room: this.janusRoomId,
                ptype: 'publisher',
                display: this.buildPublisherDisplay()
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

        // Flush buffered ICE candidates now that handle is configured
        for (const buffered of candidateBuffer) {
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/trickle`,
                    buffered
                );
            } catch { }
        }
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
                    params.encodings[0].minBitrate = minBitrateBps;
                    params.encodings[0].maxBitrate = bitrateBps;
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
            this.log(tag, 'Cannot renegotiate â€” no publisher PC or Janus session');
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
            this.log(tag, 'Configure sent â€” waiting for JSEP answer via polling loop');
        } catch (err) {
            this.logError(tag, `Renegotiation failed: ${err.message}`);
        }
    },

    // â”€â”€â”€ Subscriber (two-phase: initiate â†’ complete via polling) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /**
     * Phase 1: Attach a subscriber handle and send the join message
     * using the fire-and-forget endpoint (/message-nowait) so we do NOT
     * consume events from the Janus event queue.
     * The JSEP offer will arrive via the polling loop â†’ completeSubscribe.
     */
    async initiateSubscribe(feedId, display, streamsHint) {
        if (this.feeds[feedId]) return;
        const alreadyPending = this.findPendingSubscribeByFeed(feedId);
        if (alreadyPending) {
            const mergedStreams = this.normalizeSubscribeStreams(streamsHint, feedId);
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
        const normalizedStreams = this.normalizeSubscribeStreams(streamsHint, feedId);
        this.pendingSubscribes[subHandleId] = {
            feedId,
            display,
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
                offer_audio: true,
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
    },

    async requestSubscriberOffer(subHandleId, feedId, reason) {
        const pending = this.pendingSubscribes[subHandleId];
        if (!pending) return;
        const effectiveFeedId = pending.feedId || feedId;
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
        const streams = this.normalizeSubscribeStreams(pending.streams, effectiveFeedId);
        pending.streams = streams;

        try {
            this.log('subscribe', `Subscriber ${reason}: sending subscribe attempt ${pending.subscribeAttempts} for feed ${effectiveFeedId} (handle ${subHandleId})`);
            const result = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                {
                    body: {
                        request: 'subscribe',
                        streams,
                        offer_audio: true,
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
        this.feeds[feedId] = { handleId: subHandleId, pc: subPc, display, stream: null };

        // Monitor subscriber connection state
        subPc.oniceconnectionstatechange = () => {
            const state = subPc.iceConnectionState;
            this.log('subscribe', `Feed ${feedId} ICE state: ${state}`);
            if (state === 'failed') {
                this.logError('subscribe', `Feed ${feedId} ICE FAILED â€” cannot receive their media`);
            }
        };
        subPc.onconnectionstatechange = () => {
            this.log('subscribe', `Feed ${feedId} PC state: ${subPc.connectionState}`);
        };

        // Handle remote tracks
        subPc.ontrack = ({ streams, receiver, track }) => {
            this.log('subscribe', `ontrack fired for feed ${feedId}, streams: ${streams.length}`);
            this.tuneReceiverPlayout(receiver, track?.kind || '', `feed ${feedId}`);
            if (track?.kind === 'video') {
                this.enforceVideoReceiverPlayoutDelay(subPc, `feed ${feedId}`);
            }
            const streamForPlayback = this.normalizeRemotePlaybackStream(
                streams[0] || this.feeds[feedId]?.stream || null,
                feedId,
                track || null
            );
            const feedState = this.feeds[feedId];
            if (feedState) {
                const now = Date.now();
                feedState.lastTrackAt = now;
                if (track?.kind === 'audio') feedState.lastAudioTrackAt = now;
                if (track?.kind === 'video') feedState.lastVideoTrackAt = now;
                feedState.display = display;
                feedState.stream = streamForPlayback || feedState.stream || null;

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
            const cell = this.ensureRemoteFeedCell(feedId, display);
            if (cell) {
                const vid = cell.querySelector('video');
                this.configureRemoteVideoElement(vid, feedId);
                if (streamForPlayback) {
                    vid.srcObject = streamForPlayback;
                    const playPromise = vid.play?.();
                    if (playPromise?.catch) {
                        playPromise.catch(err => this.log('subscribe', `Remote play() blocked for feed ${feedId}: ${err.message}`));
                    }
                }
                this.applyRemoteFeedVolume(feedId, vid);
            }
            this.refreshRemoteAudioRouting();
            this.refreshFloatContainerIfPresent();
        };

        // Trickle ICE for subscriber
        subPc.onicecandidate = async ({ candidate }) => {
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/trickle`,
                    candidate
                );
            } catch { }
        };

        // Set Janus's offer and create answer
        await subPc.setRemoteDescription(new RTCSessionDescription(jsep));
        this.tunePeerReceiversPlayout(subPc, `feed ${feedId}`);
        const answer = await subPc.createAnswer();
        await subPc.setLocalDescription(answer);
        this.logCodecSummary(subPc, `subscribe-feed-${feedId}`);
        const remoteVideoCodec = this.getCodecFromSdp(jsep?.sdp || '', 'video');
        const hint = this.publisherStreamHints[String(feedId)];
        if (remoteVideoCodec === 'none' && hint?.hasVideo) {
            this.log('subscribe', `Feed ${feedId} attached audio-only but publisher hints include video; scheduling subscriber refresh`);
            this.scheduleExistingSubscriberRefresh(feedId, 1500, 'attached-audio-only');
        }

        // Send "start" with answer â€” fire-and-forget (no event competition)
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
        const removedIdentityKey = this.getFeedIdentityKey(feedId);
        const feed = this.feeds[feedId];
        if (feed) {
            feed.pc.close();
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
        const el = document.getElementById('remote-' + feedId);
        if (el) {
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
            if (remainingIdentityFeeds.length === 0) {
                delete this.remoteIdentityVolumes[removedIdentityKey];
            } else {
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

    ensureRemoteFeedCell(feedId, display) {
        const videoGrid = document.getElementById('videoGrid');
        if (!videoGrid) return null;
        const streamKey = `remote-${feedId}`;

        let cell = document.getElementById('remote-' + feedId);
        if (cell) {
            cell.dataset.feedId = String(feedId);
            cell.dataset.display = (display || '').toString();
            this.bindSpotlightInteraction(cell, streamKey, true);
            const existingVideo = cell.querySelector('video');
            this.configureRemoteVideoElement(existingVideo, feedId);
            const label = cell.querySelector('.video-label');
            if (label) {
                label.textContent = this.getRemoteFeedDisplayName(feedId, display);
            }
            const controls = cell.querySelector('.video-controls');
            this.ensureSpotlightButton(controls, streamKey);
            this.ensureRemoteFeedVolumeControl(cell, feedId);
            this.refreshIdentityVolumeControls(this.getFeedIdentityKey(feedId));
            this.applySpotlightLayout();
            return cell;
        }

        cell = document.createElement('div');
        cell.className = 'video-cell';
        cell.id = 'remote-' + feedId;
        cell.dataset.feedId = String(feedId);
        cell.dataset.display = (display || '').toString();
        this.bindSpotlightInteraction(cell, streamKey, true);

        const vid = document.createElement('video');
        this.configureRemoteVideoElement(vid, feedId);
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
        this.ensureRemoteFeedVolumeControl(cell, feedId);
        videoGrid.appendChild(cell);

        const initialVolume = this.getFeedVolumePercent(feedId);
        this.setRemoteFeedVolume(feedId, initialVolume);
        this.refreshIdentityVolumeControls(this.getFeedIdentityKey(feedId));
        this.applySpotlightLayout();
        return cell;
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

    ensureRemoteFeedVolumeControl(cell, feedId) {
        if (!cell) return;

        let volumePanel = cell.querySelector('.remote-volume-panel');
        if (!volumePanel) {
            volumePanel = document.createElement('div');
            volumePanel.className = 'remote-volume-panel';

            const row = document.createElement('div');
            row.className = 'remote-volume-row';

            const caption = document.createElement('span');
            caption.className = 'remote-volume-caption';
            caption.textContent = 'Vol';

            const slider = document.createElement('input');
            slider.type = 'range';
            slider.min = '0';
            slider.max = '200';
            slider.step = '1';
            slider.value = '100';
            slider.className = 'video-volume-slider video-volume-slider-main';
            slider.addEventListener('input', () => {
                this.setRemoteFeedVolume(feedId, slider.value);
            });

            const value = document.createElement('span');
            value.className = 'video-volume-label video-volume-label-main';
            value.textContent = '100%';

            // Prevent tile click/double-click handlers from stealing interaction.
            const stopPropagation = (ev) => ev.stopPropagation();
            volumePanel.addEventListener('click', stopPropagation);
            volumePanel.addEventListener('dblclick', stopPropagation);
            volumePanel.addEventListener('mousedown', stopPropagation);
            volumePanel.addEventListener('pointerdown', stopPropagation);

            row.append(caption, slider, value);
            volumePanel.append(row);
            cell.appendChild(volumePanel);
        }

        const identityKey = this.getFeedIdentityKey(feedId);
        const currentVolume = identityKey
            ? this.getIdentityVolumeState(identityKey).mic
            : (this.remoteFeedVolumes[String(feedId)] ?? 100);

        const slider = volumePanel.querySelector('.video-volume-slider-main');
        const label = volumePanel.querySelector('.video-volume-label-main');
        if (slider) slider.value = String(currentVolume);
        if (label) label.textContent = `${currentVolume}%`;
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

    getFeedIdentityKey(feedId) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return '';
        const feed = this.feeds[key];
        if (!feed) return '';

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

    feedHasLiveTrack(feed, kind) {
        if (!feed) return false;

        const liveFromStream = kind === 'audio'
            ? feed.stream?.getAudioTracks?.().some(t => t && t.readyState === 'live')
            : feed.stream?.getVideoTracks?.().some(t => t && t.readyState === 'live');
        if (liveFromStream) return true;

        const receivers = feed.pc?.getReceivers?.() || [];
        return receivers.some(r => r?.track?.kind === kind && r.track.readyState === 'live');
    },

    listIdentityFeeds(identityKey, requireLiveAudio = false) {
        if (!identityKey) return [];
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

        if (feed.roleHint === 'mic' || feed.roleHint === 'stream') {
            return feed.roleHint;
        }

        const identityKey = this.getFeedIdentityKey(key);
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
        return this.getIdentityVolumeState(identityKey).mic;
    },

    identityHasStreamAudioFeed(identityKey) {
        if (!identityKey) return false;
        const identityAudioFeeds = this.listIdentityFeeds(identityKey, true);
        if (identityAudioFeeds.length <= 1) return false;
        return identityAudioFeeds.some(feedId => this.getFeedRole(feedId) === 'stream');
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

    syncVolumeAcrossIdentity(feedId, volumePercent) {
        const key = this.normalizeFeedId(feedId);
        if (!key) return;

        const identityKey = this.getFeedIdentityKey(key);
        if (!identityKey) return;

        const volumeState = this.getIdentityVolumeState(identityKey);
        volumeState.mic = volumePercent;
        volumeState.stream = volumePercent;
        this.remoteIdentityVolumes[identityKey] = volumeState;

        for (const [otherFeedId] of Object.entries(this.feeds)) {
            const normalizedOther = String(otherFeedId);
            if (this.getFeedIdentityKey(normalizedOther) !== identityKey) continue;

            this.remoteFeedVolumes[normalizedOther] = volumePercent;
        }

        for (const [otherFeedId] of Object.entries(this.feeds)) {
            const normalizedOther = String(otherFeedId);
            if (this.getFeedIdentityKey(normalizedOther) !== identityKey) continue;

            const otherCell = document.getElementById('remote-' + normalizedOther);
            if (!otherCell) continue;
            this.ensureRemoteFeedVolumeControl(otherCell, normalizedOther);

            const otherVideo = otherCell.querySelector('video');
            if (otherVideo) {
                this.applyRemoteFeedVolumeToElement(otherVideo, normalizedOther, this.getFeedVolumePercent(normalizedOther));
            }
        }
    },

    refreshRemoteAudioRouting() {
        document.querySelectorAll('.video-cell[id^="remote-"] video')
            .forEach(vid => {
                const feedId = this.resolveFeedIdFromVideoElement(vid);
                if (!feedId) return;
                const volumePercent = this.getFeedVolumePercent(feedId);
                this.applyRemoteFeedVolumeToElement(vid, feedId, volumePercent);
            });
    },

    isSilentFloatPreviewVideo(videoElement) {
        return !!videoElement && videoElement.id === 'floatLocalVideo';
    },

    applyRemoteFeedVolumeToElement(videoElement, feedId, volumePercent) {
        if (!videoElement) return;
        if (this.isSilentFloatPreviewVideo(videoElement)) {
            videoElement.muted = true;
            videoElement.volume = 0;
            this.setRemoteFeedElementGain(videoElement, 0);
            return;
        }

        const normalizedFeedId = this.normalizeFeedId(feedId);
        if (normalizedFeedId && this.isFeedAudioSuppressedByDuplicateRouting(normalizedFeedId)) {
            videoElement.muted = true;
            videoElement.volume = 0;
            this.setRemoteFeedElementGain(videoElement, 0);
            return;
        }

        const clampedPercent = Math.max(0, Math.min(200, parseInt(volumePercent, 10) || 0));
        const linearGain = clampedPercent / 100;
        const streamGain = Math.max(0, Math.min(2, Number(this.streamVolume) || 0));
        const effectiveGain = Math.max(0, Math.min(2, linearGain * streamGain));

        if (this.screenShareSystemAudioSuppressed && this.isScreenSharing) {
            videoElement.muted = true;
            videoElement.volume = 0;
            this.setRemoteFeedElementGain(videoElement, 0);
            return;
        }

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

    getPrimaryRemoteFeedForFloat() {
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

            if (stream) {
                return { feedId, stream };
            }
        }

        return null;
    },

    refreshFloatContainerIfPresent() {
        const container = document.getElementById('floatVideoContainer');
        if (!container) return;
        this.moveVideoToFloat('floatVideoContainer');
    },

    // â”€â”€â”€ Event polling loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    async startPolling() {
        this.polling = true;
        this.log('poll', 'Polling loop started');

        while (this.polling) {
            try {
                this.pollAbort = new AbortController();

                const event = await this.apiGet(
                    `/api/janus/session/${this.janusSessionId}/poll`,
                    this.pollAbort.signal
                );
                if (!event || !this.polling) break;

                const type = event.janus;

                // Log FULL raw event for every non-keepalive (critical for debugging)
                if (type !== 'keepalive') {
                    const raw = JSON.stringify(event);
                    this.log('poll', `RAW EVENT (${raw.length} chars): ${raw.substring(0, 600)}`);
                }

                if (type === 'event') {
                    const sender = event.sender;
                    const data = event?.plugindata?.data;

                    // â”€â”€ Check for pending subscriber JSEP response â”€â”€
                    // When we fire-and-forget a subscriber join, the JSEP offer
                    // arrives here with the subscriber handle as the sender.
                    this.cleanupStalePendingSubscribes();
                    const pendingKeys = Object.keys(this.pendingSubscribes);
                    if (pendingKeys.length > 0) {
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
                        if (typeof data.private_id === 'number' && data.private_id > 0 && data.private_id !== this.myPrivateId) {
                            this.myPrivateId = data.private_id;
                            this.log('publish', `Updated private_id=${this.myPrivateId}`);
                        }
                        // New or updated remote publishers
                        if (Array.isArray(data.publishers) && data.publishers.length > 0) {
                            this.log('poll', `Publisher(s) event: ${data.publishers.length}`,
                                data.publishers.map(p => ({ id: p.id, display: p.display })));

                            for (const pub of data.publishers) {
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
                                    const hasVideo = this.hasIncomingVideoTrack(subscribedFeed?.pc);
                                    if (hint?.hasVideo && !hasVideo) {
                                        this.log('poll', `Publisher ${pub.id} reports video but subscriber is audio-only; requesting refresh on existing handle`);
                                        this.scheduleExistingSubscriberRefresh(pub.id, 250, 'publisher-event-audio-only');
                                        continue;
                                    }
                                }
                                if (this.feeds[pub.id]) {
                                    // Already subscribed â€” publisher updated their streams
                                    // (e.g. started screen share, enabled video).
                                    // Re-subscribe to pick up the new media.
                                    this.log('poll', `Publisher ${pub.id} updated streams â€” re-subscribing`);
                                    this.removeFeed(pub.id);
                                    await this.initiateSubscribe(pub.id, pub.display, pub.streams);
                                } else {
                                    await this.initiateSubscribe(pub.id, pub.display, pub.streams);
                                }
                            }
                            continue;
                        }

                        // listparticipants response (fallback publisher discovery)
                        if (data.videoroom === 'participants' && Array.isArray(data.participants)) {
                            this.log('poll', `listparticipants: ${data.participants.length} participant(s) in room`);
                            for (const p of data.participants) {
                                // Skip ourselves
                                if (p.id == this.janusHandleId || this.isSelfPublisherDisplay(p.display)) continue;
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

                this.log('poll', `Poll error (retrying in 1s): ${errMsg}`);
                await this.sleep(1000);
            }
        }
        this.log('poll', 'Polling loop ended');
    },

    // â”€â”€â”€ Keepalive â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    startKeepalive() {
        this.keepaliveFailures = 0;
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
                    this.log('keepalive', 'Janus session lost (3 consecutive keepalive failures)');
                    this.notifyDotNet('OnJanusSessionLost');
                    this.keepaliveFailures = 0; // Reset so we don't fire again
                }
            }
        }, 30_000);
    },

    stopKeepalive() {
        if (this.keepaliveTimer) {
            clearInterval(this.keepaliveTimer);
            this.keepaliveTimer = null;
        }
    },

    // â”€â”€â”€ Participant discovery (fallback for missing "new publisher" events) â”€â”€â”€â”€

    /**
     * Queries Janus for the current room participants via a dedicated server
     * endpoint that uses its own temporary session (no event queue competition).
     * Subscribes to any publishers we haven't seen yet.
     * This is the primary publisher-discovery mechanism â€” it doesn't rely on
     * Janus "event" type notifications which don't arrive reliably via long-poll.
     */
    async checkForNewPublishers() {
        if (!this.polling || !this.janusRoomId) return;
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
                // Skip already-subscribed feeds
                if (this.feeds[p.id]) {
                    this.log('participants', `  skip already subscribed: ${p.id}`);
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
        // First check after 2 seconds, then every 5 seconds
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

    // â”€â”€â”€ Controls â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    toggleMute() {
        // In PTT mode, mute button forces mute but keeps PTT enabled.
        if (this.pttEnabled) {
            this.log('ptt', 'Mute button pressed during PTT â€” forcing mute, PTT stays enabled');
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

    toggleCamera() {
        this.isCameraOff = !this.isCameraOff;
        if (this.localStream) {
            this.localStream.getVideoTracks().forEach(t => t.enabled = !this.isCameraOff);
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
                frameRate: { ideal: fps, max: fps }
            };
            const captureScreenAudio = this.currentConstraints.screenShareAudioEnabled !== false;
            this.log('screenshare', `Screen-share audio capture ${captureScreenAudio ? 'enabled' : 'disabled'} by settings`);

            let stream;
            if (captureScreenAudio) {
                // Try with system audio capture first, fall back to video-only
                // (audio capture is not supported in all environments / screen types)
                try {
                    stream = await navigator.mediaDevices.getDisplayMedia({
                        video: videoConstraints,
                        audio: true
                    });
                } catch (audioErr) {
                    // If user cancelled the picker, don't retry
                    if (audioErr.name === 'NotAllowedError' || audioErr.name === 'AbortError') {
                        throw audioErr;
                    }
                    this.log('screenshare', `Audio capture unavailable (${audioErr.message}), sharing video-only`);
                    stream = await navigator.mediaDevices.getDisplayMedia({
                        video: videoConstraints,
                        audio: false
                    });
                }
            } else {
                stream = await navigator.mediaDevices.getDisplayMedia({
                    video: videoConstraints,
                    audio: false
                });
            }

            const sharedVideoTrack = stream.getVideoTracks()[0] || null;
            this.applyVideoTrackContentHint(sharedVideoTrack, 'screenshare');
            const videoSettings = sharedVideoTrack?.getSettings?.() || {};
            const displaySurface = (videoSettings.displaySurface || '').toString();
            const trackLabel = (sharedVideoTrack?.label || '').toString();
            const streamIdHint = (stream?.id || '').toString();
            const deviceIdHint = (videoSettings.deviceId || '').toString();
            const targetLabel = this.buildScreenShareTargetLabel(trackLabel, streamIdHint, deviceIdHint);
            const shareTarget = this.classifyScreenShareTarget(displaySurface, `${targetLabel} ${trackLabel}`);
            const isSystemShare = shareTarget === 'system';
            const treatAsWindowShare = shareTarget !== 'system';
            await this.tuneScreenShareVideoTrackForPerformance(
                sharedVideoTrack,
                fps,
                'screenshare-capture',
                { enforceResolutionCap: !isSystemShare }
            );

            // WebView2 often returns no display-audio track for window/screen picks.
            // On Windows desktop, request native loopback fallback via host bridge.
            if (captureScreenAudio && stream.getAudioTracks().length === 0) {
                const preferProcessIsolation = treatAsWindowShare;
                const allowSystemFallback = isSystemShare;
                if (!allowSystemFallback) {
                    const reason = shareTarget === 'unknown'
                        ? 'share target classification uncertain'
                        : 'window capture detected';
                    this.log('screenshare', `${reason}; system audio fallback is disabled`);
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
                const [primaryAudioTrack, ...extraAudioTracks] = screenAudioTracks;
                for (const track of extraAudioTracks) {
                    try { stream.removeTrack(track); } catch { }
                    try { track.stop(); } catch { }
                }
                this.log(
                    'screenshare',
                    `Detected ${screenAudioTracks.length} screen audio tracks; using only ${primaryAudioTrack?.id || 'primary'}`
                );
            }

            const hasLiveShareAudioTrack = stream.getAudioTracks().some(t => t && t.readyState === 'live');
            const shouldSuppressAppPlayback = captureScreenAudio && isSystemShare && hasLiveShareAudioTrack;
            this.setSystemScreenShareAppPlaybackSuppressed(shouldSuppressAppPlayback);
            if (!shouldSuppressAppPlayback && captureScreenAudio && isSystemShare) {
                this.log('screenshare', 'System share has no active share-audio track; keeping in-app playback enabled');
            }

            this.screenStream = stream;
            this.isScreenSharing = true;

            const outgoingScreenVideoTrack = this.screenStream.getVideoTracks()[0] || null;
            const outgoingScreenAudioTrack = await this.buildScreenShareOutgoingAudioTrack(
                this.screenStream.getAudioTracks()[0] || null,
                this.localStream?.getAudioTracks?.()[0] || null
            );

            const localVideo = document.getElementById('localVideo');
            if (localVideo) localVideo.srcObject = this.screenStream;

            this.screenStream.getVideoTracks()[0].onended = () => this.stopScreenShare();

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
                    await audioSender.replaceTrack(outgoingScreenAudioTrack);
                    if (outgoingScreenAudioTrack) {
                        const usingMixedTrack = this.screenShareAudioMixTrack && outgoingScreenAudioTrack.id === this.screenShareAudioMixTrack.id;
                        this.log('screenshare', usingMixedTrack
                            ? 'Replaced audio sender track with mixed mic+screen track'
                            : 'Replaced audio sender track for screen share');
                    } else {
                        this.log('screenshare', 'No outgoing screen-share audio track available (audio sender cleared)');
                    }

                    const t = this.publisherPc.getTransceivers().find(t => t.sender === audioSender);
                    if (t && t.direction === 'recvonly' && outgoingScreenAudioTrack) {
                        t.direction = 'sendrecv';
                        needsRenegotiation = true;
                        this.log('screenshare', 'Changed audio transceiver direction to sendrecv');
                    }
                } else {
                    this.log('screenshare', 'No sender found for audio track');
                }

                const audioChanged = await this.enforceSinglePublisherAudioSender(outgoingScreenAudioTrack);
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
        } catch (err) {
            this.stopPublisherVideoPerformanceGuard();
            this.clearScreenShareBitrateWarmupTimers();
            await this.cleanupScreenShareAudioMixTrack();
            await this.stopWindowsScreenAudioFallback(true);
            this.setSystemScreenShareAppPlaybackSuppressed(false);
            this.notifyDotNet('OnError', 'Screen share failed: ' + err.message);
        }
    },

    async stopScreenShare() {
        if (this.musicBotActive) {
            await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: true });
        }

        this.stopPublisherVideoPerformanceGuard();
        this.clearScreenShareBitrateWarmupTimers();
        await this.stopWindowsScreenAudioFallback(true);
        await this.cleanupScreenShareAudioMixTrack();
        this.setSystemScreenShareAppPlaybackSuppressed(false);

        if (this.screenStream) {
            this.screenStream.getTracks().forEach(t => t.stop());
            this.screenStream = null;
        }
        this.isScreenSharing = false;

        const localVideo = document.getElementById('localVideo');
        if (localVideo && this.localStream) {
            localVideo.srcObject = this.localStream;
        }

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
                    this.log('screenshare', 'No local camera â€” switched video transceiver back to recvonly');
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
                    this.log('screenshare', 'No local audio â€” switched audio transceiver back to recvonly');
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

    // â”€â”€â”€ Push-to-talk â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

            this.log('ptt', `PTT active â€” hold "${this.pttKey}" to talk`);
        } else {
            // Exiting PTT mode â€” keep current manual mute state.
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
            }
            this.notifyDotNet('OnMuteChanged', this.isMuted);
            this.log('ptt', 'PTT disabled â€” manual mute state retained');
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

    // â”€â”€â”€ Stop everything â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    stop() {
        this.polling = false;
        if (this.pollAbort) {
            this.pollAbort.abort();
            this.pollAbort = null;
        }
        this.stopKeepalive();
        this.stopParticipantCheck();
        this.resetStreamInfoState();
        this.stopPublisherVideoPerformanceGuard();
        this.stopPublisherHealthWatchdog();
        this.clearScreenShareBitrateWarmupTimers();
        this.selectedSpotlightStreamKey = null;
        const videoGrid = document.getElementById('videoGrid');
        if (videoGrid) videoGrid.classList.remove('spotlight-mode');
        this.removePttListeners();
        this.pttEnabled = false;
        this.pttBinding = null;
        this.pttKeyDown = false;
        this.pttPressedCodes.clear();
        this.notifyHostPttConfig();
        this.stopWindowsScreenAudioFallback(true).catch(() => { });
        this.cleanupScreenShareAudioMixTrack().catch(() => { });
        this.cleanupMusicBotGraph().catch(() => { });
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

        if (this.audioContext) {
            try { this.audioContext.close(); } catch { }
            this.audioContext = null;
            this.gainNode = null;
            this.audioDestination = null;
        }
        if (this.remotePlaybackAudioContext) {
            try { this.remotePlaybackAudioContext.close(); } catch { }
            this.remotePlaybackAudioContext = null;
            this.remotePlaybackAudioNodes = new WeakMap();
        }

        if (this.publisherPc) {
            this.publisherPc.close();
            this.publisherPc = null;
        }
        this.publisherConfigured = false;
        this.pendingJanusBitrateBps = 0;

        Object.values(this.feeds).forEach(f => f.pc.close());
        this.feeds = {};
        this.remoteFeedVolumes = {};
        this.remoteIdentityVolumes = {};
        this.pendingSubscribes = {};

        const localVideo = document.getElementById('localVideo');
        if (localVideo) localVideo.srcObject = null;
        const localCell = document.querySelector('#videoGrid .local-cell');
        if (localCell) {
            const localPanel = localCell.querySelector('.video-info-panel');
            if (localPanel) localPanel.classList.remove('open');
            const localInfoBtn = localCell.querySelector('.video-info-btn');
            if (localInfoBtn) localInfoBtn.classList.remove('active');
        }

        // Remove all remote video cells
        document.querySelectorAll('.video-cell:not(.local-cell)').forEach(el => el.remove());

        this.removeHostMessageListener();
        this.removeFullscreenSyncListener();
        this.dotNetRef = null;
    },

    // â”€â”€â”€ Signal delivery (from C# CallStore â†’ JS) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    deliverSignal(fromUserId, signalType, payload) {
        // For Janus SFU, signals are handled through the polling loop,
        // not direct peer-to-peer. This is a no-op for the Janus flow.
    },

    // â”€â”€â”€ Device enumeration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    async enumerateDevices() {
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            const filtered = devices
                .filter(d => d.kind === 'audioinput' || d.kind === 'videoinput')
                .map(d => ({
                    deviceId: d.deviceId,
                    label: d.label || `${d.kind} (${d.deviceId.slice(0, 8)})`,
                    kind: d.kind
                }));
            this.log('devices', `Found ${filtered.length} devices`);
            this.notifyDotNet('OnDevicesEnumerated', JSON.stringify(filtered));
        } catch (err) {
            this.logError('devices', 'enumerateDevices failed: ' + err.message);
        }
    },

    // â”€â”€â”€ Volume control â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
        this.log('volume', `Set stream volume to ${clamped}`);
        this.refreshRemoteAudioRouting();
    },

    // â”€â”€â”€ Settings update â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    async updateSettings(settings) {
        this.log('settings', 'Applying settings:', settings);

        if (this.musicBotActive) {
            await this.stopMusicBotPlaybackInternal({ restorePublisherTrack: false, notifyState: true });
        }

        const bitrateKbps = this.clampPublisherBitrateKbps(
            settings.bitrateKbps ?? this.currentConstraints.bitrateKbps ?? 2000
        );
        const previousVideoCodec = this.normalizeVideoCodec(this.currentConstraints.preferredVideoCodec);
        const requestedVideoCodec = this.normalizeVideoCodec(
            settings.preferredVideoCodec ?? this.currentConstraints.preferredVideoCodec
        );
        const requestedScreenShareAudioEnabled =
            settings.screenShareAudioEnabled !== undefined
                ? settings.screenShareAudioEnabled !== false
                : this.currentConstraints.screenShareAudioEnabled !== false;

        // Re-acquire local stream with new device constraints
        await this.startLocalStream({
            micDeviceId: settings.micDeviceId || null,
            cameraDeviceId: settings.cameraDeviceId || null,
            resolution: settings.resolution || '1280x720',
            fps: settings.fps || 30,
            bitrateKbps,
            preferredVideoCodec: requestedVideoCodec,
            screenShareAudioEnabled: requestedScreenShareAudioEnabled
        });

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
                if (audioSender) {
                    const mixedOrPreferredTrack = await this.buildScreenShareOutgoingAudioTrack(
                        this.screenStream?.getAudioTracks?.()[0] || null,
                        this.localStream.getAudioTracks()[0] || null
                    );
                    await audioSender.replaceTrack(mixedOrPreferredTrack);
                    await this.enforceSinglePublisherAudioSender(mixedOrPreferredTrack);
                }

                const screenTrack = this.screenStream?.getVideoTracks?.()[0] || null;
                const settingsSnapshot = screenTrack?.getSettings?.() || {};
                const displaySurface = (settingsSnapshot.displaySurface || '').toString();
                const shareTarget = this.classifyScreenShareTarget(displaySurface, (screenTrack?.label || '').toString());
                await this.tuneScreenShareVideoTrackForPerformance(
                    screenTrack,
                    activeFps,
                    'screenshare-settings',
                    { enforceResolutionCap: shareTarget !== 'system' }
                );

                const videoSender = this.findSenderByKind(this.publisherPc, 'video');
                await this.tuneScreenShareVideoSenderForPerformance(videoSender, activeFps, 'screenshare-settings');
            }
        }

        // Apply bitrate setting
        await this.applyBitrate(bitrateKbps);
        if (this.isScreenSharing) {
            await this.requestPublisherKeyframe('screenshare-settings');
            this.scheduleScreenShareBitrateWarmup();
        }

        // Codec preference changes need a renegotiation to take effect.
        if (this.publisherPc && previousVideoCodec !== requestedVideoCodec) {
            this.log('settings', `Preferred video codec changed ${previousVideoCodec} -> ${requestedVideoCodec}`);
            await this.renegotiatePublisher('settings-codec');
        }

        // Re-enumerate to refresh labels (some browsers only show labels after permission)
        await this.enumerateDevices();
    },

    rebindVideoElements() {
        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            if (this.isScreenSharing && this.screenStream) {
                localVideo.srcObject = this.screenStream;
            } else if (this.localStream) {
                localVideo.srcObject = this.localStream;
            }
        }
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
            }
            this.applyRemoteFeedVolume(feedId, vid);
        }

        this.applySpotlightLayout();
    },

    // â”€â”€â”€ Floating video helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
            const normalized = this.normalizeRemotePlaybackStream(primaryRemote.stream, primaryRemote.feedId);
            floatVideo.srcObject = normalized;
            floatVideo.dataset.feedId = String(primaryRemote.feedId);
            this.detachRemoteFeedElementGain(floatVideo);
            // Keep float preview silent to avoid duplicate playback sinks.
            floatVideo.muted = true;
            floatVideo.volume = 0;
            floatVideo.play?.().catch(() => { });
            this.log('float', `Bound remote feed ${primaryRemote.feedId} to #${containerId}`);
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


