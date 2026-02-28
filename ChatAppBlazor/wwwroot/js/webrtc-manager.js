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

    // Pending subscriber joins: handleId → { feedId, display }
    // Tracks handles waiting for JSEP offer from Janus via polling loop.
    pendingSubscribes: {},

    // Publisher PeerConnection
    publisherPc: null,

    // Auth / server
    userId: '',
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
    currentConstraints: { micDeviceId: null, cameraDeviceId: null, resolution: '1280x720', fps: 30, bitrateKbps: 2000 },
    audioContext: null,
    gainNode: null,

    // Push-to-talk
    pttEnabled: false,
    pttKey: 'Space',
    pttKeyDown: false,
    _pttKeyDownHandler: null,
    _pttKeyUpHandler: null,

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
        this.pendingSubscribes = {};

        this.log('init', `session=${config.janusSessionId}, handle=${config.janusHandleId}, room=${config.janusRoomId}`);
        this.log('init', `serverBaseUrl=${config.serverBaseUrl}`);

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

        try {
            // Publish uses the sync /message endpoint (polling hasn't started, no competition)
            const existingPublishers = await this.publishOwnFeed();

            // Apply initial bitrate to publisher
            await this.applyBitrate(this.currentConstraints.bitrateKbps);

            // Start polling FIRST — it will handle subscriber JSEP responses
            this.startPolling();
            this.startKeepalive();
            this.startParticipantCheck();

            // Now initiate subscribes for any existing publishers (fire-and-forget)
            if (existingPublishers.length > 0) {
                this.log('init', `Subscribing to ${existingPublishers.length} existing publisher(s)...`);
                for (const pub of existingPublishers) {
                    await this.initiateSubscribe(pub.id, pub.display);
                }
            }
        } catch (err) {
            this.logError('init', 'Failed to publish: ' + err.message);
        }
    },

    // ─── Media ──────────────────────────────────────────────────────────────────

    async startLocalStream(constraints) {
        if (this.localStream) {
            this.localStream.getTracks().forEach(t => t.stop());
        }

        // Merge with stored constraints
        if (constraints) {
            Object.assign(this.currentConstraints, constraints);
        }
        const c = this.currentConstraints;

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

        this.localStream = await navigator.mediaDevices.getUserMedia({
            audio: audioConstraint,
            video: videoConstraint
        });

        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            localVideo.srcObject = this.localStream;
        }

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

        this.log('audio', 'Audio pipeline set up (gain → MediaStreamDestination, no echo)');
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

    // ─── Publisher ──────────────────────────────────────────────────────────────

    /**
     * Publishes own feed to Janus. Uses the sync /message endpoint because
     * the polling loop hasn't started yet (no event queue competition).
     * Returns an array of existing publishers to subscribe to.
     */
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
            } catch (err) {
                this.logError('trickle', 'ICE trickle failed: ' + err.message);
            }
        };

        // Create offer
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        this.log('publish', 'Created SDP offer, sending joinandconfigure...');

        // Join as publisher with SDP offer (sync — polling not started yet)
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
            this.log('publish', 'joinandconfigure response received', joinResult?.janus);
        } catch (err) {
            this.logError('publish', 'Failed to join room: ' + err.message);
            return [];
        }

        // Mark handle as configured so future ICE candidates send immediately
        handleConfigured = true;

        // Apply answer SDP from Janus
        if (joinResult?.jsep) {
            await pc.setRemoteDescription(new RTCSessionDescription(joinResult.jsep));
            this.log('publish', 'Set remote SDP answer');
        } else {
            this.logError('publish', 'No JSEP answer from Janus in joinandconfigure response');
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
        const bitrateBps = bitrateKbps * 1000;

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
                    params.encodings[0].maxBitrate = bitrateBps;
                    await sender.setParameters(params);
                    this.log('bitrate', `Set sender maxBitrate to ${bitrateKbps} kbps`);
                } catch (err) {
                    this.log('bitrate', `setParameters failed: ${err.message}`);
                }
            }
        }

        // 2. Tell Janus SFU the desired bitrate (fire-and-forget)
        if (this.janusSessionId && this.janusHandleId) {
            try {
                await this.apiPost(
                    `/api/janus/session/${this.janusSessionId}/handle/${this.janusHandleId}/message-nowait`,
                    { body: { request: 'configure', bitrate: bitrateBps } }
                );
                this.log('bitrate', `Sent Janus configure bitrate=${bitrateBps} bps`);
            } catch (err) {
                this.log('bitrate', `Janus configure failed: ${err.message}`);
            }
        }
    },

    // ─── Subscriber (two-phase: initiate → complete via polling) ────────────────

    /**
     * Phase 1: Attach a subscriber handle and send the join message
     * using the fire-and-forget endpoint (/message-nowait) so we do NOT
     * consume events from the Janus event queue.
     * The JSEP offer will arrive via the polling loop → completeSubscribe.
     */
    async initiateSubscribe(feedId, display) {
        if (this.feeds[feedId]) return;
        this.log('subscribe', `Initiating subscribe to feed ${feedId} (${display || 'unknown'})`);

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
        this.pendingSubscribes[subHandleId] = { feedId, display };

        // Send join message via fire-and-forget (no event queue competition!)
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
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
            this.log('subscribe', `Sent subscriber join for feed ${feedId} (handle ${subHandleId}), awaiting JSEP via poll...`);
        } catch (err) {
            delete this.pendingSubscribes[subHandleId];
            this.logError('subscribe', `Subscriber join send failed for feed ${feedId}: ${err.message}`);
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
        this.feeds[feedId] = { handleId: subHandleId, pc: subPc };

        // Handle remote tracks
        subPc.ontrack = ({ streams }) => {
            this.log('subscribe', `ontrack fired for feed ${feedId}, streams: ${streams.length}`);
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
        await subPc.setRemoteDescription(new RTCSessionDescription(jsep));
        const answer = await subPc.createAnswer();
        await subPc.setLocalDescription(answer);

        // Send "start" with answer — fire-and-forget (no event competition)
        try {
            await this.apiPost(
                `/api/janus/session/${this.janusSessionId}/handle/${subHandleId}/message-nowait`,
                {
                    body: { request: 'start', room: this.janusRoomId },
                    jsep: { type: answer.type, sdp: answer.sdp }
                }
            );
            this.log('subscribe', `Subscriber started for feed ${feedId}`);
        } catch (err) {
            this.logError('subscribe', `Subscriber start failed for feed ${feedId}: ${err.message}`);
        }
    },

    removeFeed(feedId) {
        const feed = this.feeds[feedId];
        if (feed) {
            feed.pc.close();
            delete this.pendingSubscribes[feed.handleId];
            delete this.feeds[feedId];
        }
        const el = document.getElementById('remote-' + feedId);
        if (el) el.remove();
        this.log('subscribe', `Removed feed ${feedId}`);
    },

    // ─── Event polling loop ─────────────────────────────────────────────────────

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

                    // ── Check for pending subscriber JSEP response ──
                    // When we fire-and-forget a subscriber join, the JSEP offer
                    // arrives here with the subscriber handle as the sender.
                    const pendingKeys = Object.keys(this.pendingSubscribes);
                    if (pendingKeys.length > 0) {
                        this.log('poll', `Pending subscribes: [${pendingKeys.join(',')}], event sender: ${sender}`);
                    }

                    if (sender && this.pendingSubscribes[sender] && event.jsep) {
                        this.log('poll', `Matched subscriber JSEP for handle ${sender}`);
                        await this.completeSubscribe(sender, event.jsep);
                        continue;
                    }

                    const data = event?.plugindata?.data;
                    if (data) {
                        // New remote publishers joining the room
                        if (Array.isArray(data.publishers) && data.publishers.length > 0) {
                            this.log('poll', `New publisher(s) detected: ${data.publishers.length}`,
                                data.publishers.map(p => ({ id: p.id, display: p.display })));

                            for (const pub of data.publishers) {
                                await this.initiateSubscribe(pub.id, pub.display);
                            }
                            continue;
                        }

                        // listparticipants response (fallback publisher discovery)
                        if (data.videoroom === 'participants' && Array.isArray(data.participants)) {
                            this.log('poll', `listparticipants: ${data.participants.length} participant(s) in room`);
                            for (const p of data.participants) {
                                // Skip ourselves
                                if (p.id == this.janusHandleId) continue;
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
                        this.log('poll', 'Re-negotiation JSEP received for publisher');
                        await this.publisherPc.setRemoteDescription(
                            new RTCSessionDescription(event.jsep)
                        );
                    }
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

    // ─── Keepalive ──────────────────────────────────────────────────────────────

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
        try {
            const result = await this.apiGet(`/api/janus/room/${this.janusRoomId}/participants`);
            const data = result?.plugindata?.data;
            const participants = data?.participants;
            if (!Array.isArray(participants)) {
                this.log('participants', 'No participants array in response', result);
                return;
            }

            for (const p of participants) {
                // Skip ourselves (by handle ID or by display/userId for stale sessions)
                if (p.id == this.janusHandleId) continue;
                if (p.display && p.display === this.userId) continue;
                // Skip already-subscribed feeds
                if (this.feeds[p.id]) continue;
                // Skip feeds we're already subscribing to
                const alreadyPending = Object.values(this.pendingSubscribes)
                    .some(ps => ps.feedId === p.id);
                if (alreadyPending) continue;

                if (p.publisher) {
                    this.log('participants', `Discovered new publisher: feed ${p.id} (${p.display || 'unknown'})`);
                    await this.initiateSubscribe(p.id, p.display);
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

    // ─── Controls ───────────────────────────────────────────────────────────────

    toggleMute() {
        // If PTT is active, clicking mute button disables PTT mode
        if (this.pttEnabled) {
            this.log('ptt', 'Mute button pressed during PTT — disabling PTT');
            this.setPushToTalk(false, this.pttKey);
            this.notifyDotNet('OnPushToTalkChanged', false);
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

    async startScreenShare() {
        try {
            const fps = parseInt(this.currentConstraints.fps) || 30;
            const videoConstraints = {
                cursor: 'always',
                frameRate: { ideal: fps, max: fps }
            };

            // Try with system audio capture first, fall back to video-only
            // (audio capture is not supported in all environments / screen types)
            let stream;
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

            this.screenStream = stream;
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

    // ─── Push-to-talk ─────────────────────────────────────────────────────────

    /**
     * Enable or disable push-to-talk mode.
     * When enabled, the mic is muted by default and only unmuted while
     * the configured key is held down.
     */
    setPushToTalk(enabled, key) {
        this.log('ptt', `setPushToTalk enabled=${enabled}, key=${key}`);

        // Remove old listeners
        this.removePttListeners();

        this.pttEnabled = enabled;
        this.pttKey = key || 'Space';
        this.pttKeyDown = false;

        if (enabled) {
            // Mute immediately when entering PTT mode
            this.isMuted = true;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = false);
            }
            this.notifyDotNet('OnMuteChanged', true);

            // Install global keyboard listeners
            this._pttKeyDownHandler = (e) => this.handlePttKeyDown(e);
            this._pttKeyUpHandler = (e) => this.handlePttKeyUp(e);
            document.addEventListener('keydown', this._pttKeyDownHandler);
            document.addEventListener('keyup', this._pttKeyUpHandler);

            this.log('ptt', `PTT active — hold "${this.pttKey}" to talk`);
        } else {
            // Exiting PTT mode — unmute
            this.isMuted = false;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = true);
            }
            this.notifyDotNet('OnMuteChanged', false);
            this.log('ptt', 'PTT disabled — mic open');
        }
    },

    handlePttKeyDown(e) {
        if (!this.pttEnabled) return;
        if (this.matchesPttKey(e) && !this.pttKeyDown) {
            e.preventDefault();
            this.pttKeyDown = true;
            // Unmute while key is held
            this.ensureAudioContextActive();
            this.isMuted = false;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = true);
            }
            this.notifyDotNet('OnMuteChanged', false);
        }
    },

    handlePttKeyUp(e) {
        if (!this.pttEnabled) return;
        if (this.matchesPttKey(e) && this.pttKeyDown) {
            e.preventDefault();
            this.pttKeyDown = false;
            // Re-mute when key is released
            this.isMuted = true;
            if (this.localStream) {
                this.localStream.getAudioTracks().forEach(t => t.enabled = false);
            }
            this.notifyDotNet('OnMuteChanged', true);
        }
    },

    /**
     * Match keyboard event to the configured PTT key.
     * Supports: 'Space', 'KeyV', 'KeyB', 'CapsLock', 'ControlLeft', etc.
     * Uses event.code for layout-independent matching.
     */
    matchesPttKey(e) {
        return e.code === this.pttKey;
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
    },

    // ─── Stop everything ────────────────────────────────────────────────────────

    stop() {
        this.polling = false;
        if (this.pollAbort) {
            this.pollAbort.abort();
            this.pollAbort = null;
        }
        this.stopKeepalive();
        this.stopParticipantCheck();
        this.removePttListeners();
        this.pttEnabled = false;
        this.pttKeyDown = false;

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

        if (this.publisherPc) {
            this.publisherPc.close();
            this.publisherPc = null;
        }

        Object.values(this.feeds).forEach(f => f.pc.close());
        this.feeds = {};
        this.pendingSubscribes = {};

        const localVideo = document.getElementById('localVideo');
        if (localVideo) localVideo.srcObject = null;

        // Remove all remote video cells
        document.querySelectorAll('.video-cell:not(.local-cell)').forEach(el => el.remove());

        this.dotNetRef = null;
    },

    // ─── Signal delivery (from C# CallStore → JS) ──────────────────────────────

    deliverSignal(fromUserId, signalType, payload) {
        // For Janus SFU, signals are handled through the polling loop,
        // not direct peer-to-peer. This is a no-op for the Janus flow.
    },

    // ─── Device enumeration ─────────────────────────────────────────────────────

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

    // ─── Volume control ─────────────────────────────────────────────────────────

    setVolume(level) {
        this.ensureAudioContextActive();
        if (this.gainNode) {
            this.gainNode.gain.value = Math.max(0, Math.min(2, level));
            this.log('volume', `Set volume to ${level}`);
        }
    },

    // ─── Settings update ────────────────────────────────────────────────────────

    async updateSettings(settings) {
        this.log('settings', 'Applying settings:', settings);

        const bitrateKbps = settings.bitrateKbps || 2000;

        // Re-acquire local stream with new device constraints
        await this.startLocalStream({
            micDeviceId: settings.micDeviceId || null,
            cameraDeviceId: settings.cameraDeviceId || null,
            resolution: settings.resolution || '1280x720',
            fps: settings.fps || 30,
            bitrateKbps
        });

        // Replace tracks in publisher PeerConnection
        if (this.publisherPc && this.localStream) {
            const senders = this.publisherPc.getSenders();
            const active = (this.isScreenSharing && this.screenStream)
                ? this.screenStream : this.localStream;
            active.getTracks().forEach(track => {
                const s = senders.find(x => x.track?.kind === track.kind);
                if (s) s.replaceTrack(track);
            });
        }

        // Apply bitrate setting
        await this.applyBitrate(bitrateKbps);

        // Re-enumerate to refresh labels (some browsers only show labels after permission)
        await this.enumerateDevices();
    },

    // ─── Floating video helpers ─────────────────────────────────────────────────

    /**
     * Clone the local video stream into a floating overlay container.
     */
    moveVideoToFloat(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const floatVideo = container.querySelector('video');
        if (floatVideo && this.localStream) {
            floatVideo.srcObject = this.localStream;
            this.log('float', `Cloned local stream to #${containerId}`);
        }
    },

    /**
     * Clear the floating video container.
     */
    clearFloatVideo(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const floatVideo = container.querySelector('video');
        if (floatVideo) {
            floatVideo.srcObject = null;
        }
        this.log('float', `Cleared float video in #${containerId}`);
    }
};
