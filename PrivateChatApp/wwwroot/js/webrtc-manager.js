'use strict';

/**
 * WebRTC Manager — First-class Janus SFU integration for Blazor Hybrid.
 *
 * Manages the complete Janus videoroom lifecycle:
 *   - Publisher: local media → Janus → SFU
 *   - Subscribers: one handle per remote feed
 *   - Trickle ICE, keepalive, long-poll event loop
 *
 * Communication with C# via DotNetObjectReference (passed in init).
 */
window.webrtcManager = {
    // ─── State ──────────────────────────────────────────────────────────────────

    localStream: null,
    screenStream: null,
    isMuted: false,
    isCameraOff: false,
    isScreenSharing: false,

    // Janus session (publisher)
    janusSessionId: 0,
    janusHandleId: 0,
    janusRoomId: 0,
    myPrivateId: 0,

    // Per-feed subscriber handles: feedId → { handleId, pc }
    feeds: {},

    // Publisher PeerConnection
    publisherPc: null,

    // Auth / server
    userId: '',
    serverBaseUrl: '',
    authToken: '',

    // ICE
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],

    // Polling / keepalive
    polling: false,
    keepaliveTimer: null,

    // C# interop reference
    dotNetRef: null,

    // ─── Helpers ────────────────────────────────────────────────────────────────

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
        return resp.json();
    },

    async apiGet(path) {
        const resp = await fetch(this.serverBaseUrl + path, {
            headers: { 'Authorization': 'Bearer ' + this.authToken }
        });
        if (!resp.ok) throw new Error(`GET ${path} → ${resp.status}`);
        return resp.json();
    },

    notifyDotNet(method, ...args) {
        if (this.dotNetRef) {
            this.dotNetRef.invokeMethodAsync(method, ...args).catch(() => { });
        }
    },

    sleep(ms) {
        return new Promise(r => setTimeout(r, ms));
    },

    // ─── Init ───────────────────────────────────────────────────────────────────

    async init(config) {
        this.janusSessionId = config.janusSessionId;
        this.janusHandleId = config.janusHandleId;
        this.janusRoomId = config.janusRoomId;
        this.serverBaseUrl = config.serverBaseUrl;
        this.authToken = config.authToken;
        this.userId = config.userId;
        this.dotNetRef = config.dotNetRef;

        if (config.iceServers) {
            try { this.iceServers = JSON.parse(config.iceServers); } catch { }
        }

        this.isMuted = false;
        this.isCameraOff = false;
        this.isScreenSharing = false;

        try {
            await this.startLocalStream();
            await this.publishOwnFeed();
            this.startPolling();
            this.startKeepalive();
        } catch (err) {
            this.notifyDotNet('OnError', 'Init failed: ' + err.message);
        }
    },

    // ─── Media ──────────────────────────────────────────────────────────────────

    async startLocalStream() {
        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
        }

        const videoConstraint = {
            width: { ideal: 1280 },
            height: { ideal: 720 },
            frameRate: { ideal: 30 }
        };

        this.localStream = await navigator.mediaDevices.getUserMedia({
            audio: true,
            video: videoConstraint
        });

        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            localVideo.srcObject = this.localStream;
        }

        // Apply current mute/camera state
        this.localStream.getAudioTracks().forEach(t => t.enabled = !this.isMuted);
        this.localStream.getVideoTracks().forEach(t => t.enabled = !this.isCameraOff);
    },

    // ─── Publisher ──────────────────────────────────────────────────────────────

    async publishOwnFeed() {
        const pc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.publisherPc = pc;

        // Add local tracks
        const stream = this.localStream;
        if (stream) stream.getTracks().forEach(track => pc.addTrack(track, stream));

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
            } catch { }
        };

        // Create offer
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        // Join as publisher with SDP offer
        let joinResult;
        try {
            joinResult = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message`,
                {
                    body: {
                        request: 'joinandconfigure',
                        room: this.janusRoomId,
                        ptype: 'publisher',
                        display: this.userId
                    },
                    jsep: { type: offer.type, sdp: offer.sdp }
                }
            );
        } catch (err) {
            this.notifyDotNet('OnError', 'Failed to join room: ' + err.message);
            return;
        }

        // Mark handle as configured so future ICE candidates send immediately
        handleConfigured = true;

        // Apply answer SDP from Janus FIRST (before subscribing feeds)
        if (joinResult?.jsep) {
            await pc.setRemoteDescription(new RTCSessionDescription(joinResult.jsep));
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

        // Extract private_id and subscribe to existing publishers
        const pluginData = joinResult?.plugindata?.data;
        if (pluginData) {
            if (pluginData.private_id) this.myPrivateId = pluginData.private_id;
            if (Array.isArray(pluginData.publishers)) {
                for (const pub of pluginData.publishers) {
                    await this.subscribeFeed(pub.id, pub.display);
                }
            }
        }
    },

    // ─── Subscriber (one handle per remote publisher) ───────────────────────────

    async subscribeFeed(feedId, display) {
        if (this.feeds[feedId]) return;

        // Attach a dedicated handle for this subscriber
        let subHandleId;
        try {
            const result = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/attach`, {});
            subHandleId = result?.handleId;
            if (!subHandleId) throw new Error('No handleId returned');
        } catch (err) {
            console.error('Failed to attach subscriber handle:', err);
            return;
        }

        // Join as subscriber
        let subResult;
        try {
            subResult = await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message`,
                {
                    body: {
                        request: 'join',
                        room: this.janusRoomId,
                        ptype: 'subscriber',
                        feed: feedId,
                        private_id: this.myPrivateId
                    }
                }
            );
        } catch (err) {
            console.error('Subscriber join failed:', err);
            return;
        }

        if (!subResult?.jsep) {
            console.warn('No JSEP in subscriber join response — feed', feedId);
            return;
        }

        const subPc = new RTCPeerConnection({ iceServers: this.iceServers });
        this.feeds[feedId] = { handleId: subHandleId, pc: subPc };

        // Handle remote tracks
        subPc.ontrack = ({ streams }) => {
            const videoGrid = document.getElementById('videoGrid');
            if (!videoGrid) return;

            let cell = document.getElementById('remote-' + feedId);
            if (!cell) {
                cell = document.createElement('div');
                cell.className = 'video-cell';
                cell.id = 'remote-' + feedId;
                const vid = document.createElement('video');
                vid.autoplay = vid.playsInline = true;
                const lbl = document.createElement('span');
                lbl.className = 'video-label';
                lbl.textContent = display || ('User ' + String(feedId).slice(0, 8));
                cell.append(vid, lbl);
                videoGrid.appendChild(cell);
            }
            const vid = cell.querySelector('video');
            if (streams[0]) vid.srcObject = streams[0];
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
        await subPc.setRemoteDescription(new RTCSessionDescription(subResult.jsep));
        const answer = await subPc.createAnswer();
        await subPc.setLocalDescription(answer);

        // Send "start" with answer
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message`,
                {
                    body: { request: 'start', room: this.janusRoomId },
                    jsep: { type: answer.type, sdp: answer.sdp }
                }
            );
        } catch (err) {
            console.error('Subscriber start failed:', err);
        }
    },

    removeFeed(feedId) {
        const feed = this.feeds[feedId];
        if (feed) {
            feed.pc.close();
            delete this.feeds[feedId];
        }
        const el = document.getElementById('remote-' + feedId);
        if (el) el.remove();
    },

    // ─── Event polling loop ─────────────────────────────────────────────────────

    async startPolling() {
        this.polling = true;

        while (this.polling) {
            try {
                const event = await this.apiGet(
                    `/api/janus/session/${this.janusSessionId}/poll`);
                if (!event || !this.polling) break;

                const type = event.janus;
                if (type === 'event') {
                    const data = event?.plugindata?.data;
                    if (data) {
                        // New remote publishers
                        if (Array.isArray(data.publishers)) {
                            for (const pub of data.publishers) {
                                await this.subscribeFeed(pub.id, pub.display);
                            }
                        }
                        // Publisher left or unpublished
                        if (data.leaving) this.removeFeed(data.leaving);
                        if (data.unpublished && data.unpublished !== 'ok') {
                            this.removeFeed(data.unpublished);
                        }
                    }

                    // Re-negotiation JSEP on publisher
                    if (event.jsep && this.publisherPc) {
                        await this.publisherPc.setRemoteDescription(
                            new RTCSessionDescription(event.jsep)
                        );
                    }
                }
            } catch {
                if (!this.polling) break;
                await this.sleep(1000);
            }
        }
    },

    // ─── Keepalive ──────────────────────────────────────────────────────────────

    startKeepalive() {
        this.keepaliveTimer = setInterval(async () => {
            if (!this.polling) return;
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/keepalive`, {});
            } catch { }
        }, 30_000);
    },

    stopKeepalive() {
        if (this.keepaliveTimer) {
            clearInterval(this.keepaliveTimer);
            this.keepaliveTimer = null;
        }
    },

    // ─── Controls ───────────────────────────────────────────────────────────────

    toggleMute() {
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

    async startScreenShare() {
        try {
            this.screenStream = await navigator.mediaDevices.getDisplayMedia({
                video: { cursor: 'always' },
                audio: true
            });
            this.isScreenSharing = true;

            const localVideo = document.getElementById('localVideo');
            if (localVideo) localVideo.srcObject = this.screenStream;

            this.screenStream.getVideoTracks()[0].onended = () => this.stopScreenShare();

            // Replace tracks in publisher PC
            if (this.publisherPc) {
                const senders = this.publisherPc.getSenders();
                this.screenStream.getTracks().forEach(track => {
                    const s = senders.find(x => x.track?.kind === track.kind);
                    if (s) s.replaceTrack(track);
                });
            }

            this.notifyDotNet('OnScreenShareChanged', true);
        } catch (err) {
            this.notifyDotNet('OnError', 'Screen share failed: ' + err.message);
        }
    },

    stopScreenShare() {
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
        if (this.publisherPc && this.localStream) {
            const senders = this.publisherPc.getSenders();
            this.localStream.getTracks().forEach(track => {
                const s = senders.find(x => x.track?.kind === track.kind);
                if (s) s.replaceTrack(track);
            });
        }

        this.notifyDotNet('OnScreenShareChanged', false);
    },

    // ─── Stop everything ────────────────────────────────────────────────────────

    stop() {
        this.polling = false;
        this.stopKeepalive();

        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
            this.localStream = null;
        }
        if (this.screenStream) {
            this.screenStream.getTracks().forEach(t => t.stop());
            this.screenStream = null;
        }

        if (this.publisherPc) {
            this.publisherPc.close();
            this.publisherPc = null;
        }

        Object.values(this.feeds).forEach(f => f.pc.close());
        this.feeds = {};

        const localVideo = document.getElementById('localVideo');
        if (localVideo) localVideo.srcObject = null;

        // Remove all remote video cells
        document.querySelectorAll('.video-cell:not(.local-cell)').forEach(el => el.remove());

        this.dotNetRef = null;
    },

    // ─── Signal delivery (from C# CallStore → JS) ──────────────────────────────

    deliverSignal(fromUserId, signalType, payload) {
        // For Janus SFU, signals are handled through the polling loop,
        // not direct peer-to-peer. This is a no-op for the Janus flow
        // but kept for potential hybrid P2P fallback.
        console.log('Signal received from', fromUserId, signalType);
    },

    // ─── Settings update ────────────────────────────────────────────────────────

    async updateSettings(settings) {
        // Re-acquire local stream with new device constraints
        await this.startLocalStream();

        if (this.publisherPc && this.localStream) {
            const senders = this.publisherPc.getSenders();
            const active = (this.isScreenSharing && this.screenStream)
                ? this.screenStream : this.localStream;
            active.getTracks().forEach(track => {
                const s = senders.find(x => x.track?.kind === track.kind);
                if (s) s.replaceTrack(track);
            });
        }
    }
};
