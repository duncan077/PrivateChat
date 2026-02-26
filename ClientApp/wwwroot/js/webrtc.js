window.webrtc = {
    localStream: null,
    peerConnections: {},
    iceServers: [],
    janusUrl: '',

    init: function (janusUrl, iceServersJson) {
        this.janusUrl = janusUrl;
        try {
            this.iceServers = JSON.parse(iceServersJson);
        } catch {
            this.iceServers = [{ urls: 'stun:stun.l.google.com:19302' }];
        }
    },

    startLocalStream: async function (audio, video) {
        try {
            this.localStream = await navigator.mediaDevices.getUserMedia({ audio: audio, video: video });
            const localVideo = document.getElementById('localVideo');
            if (localVideo) {
                localVideo.srcObject = this.localStream;
            }
        } catch (err) {
            console.error('Failed to get local stream:', err);
        }
    },

    createPeerConnection: function (remoteUserId) {
        const config = { iceServers: this.iceServers };
        const pc = new RTCPeerConnection(config);

        if (this.localStream) {
            this.localStream.getTracks().forEach(track => {
                pc.addTrack(track, this.localStream);
            });
        }

        pc.ontrack = (event) => {
            const remoteVideo = document.getElementById('remoteVideo-' + remoteUserId);
            if (remoteVideo && event.streams[0]) {
                remoteVideo.srcObject = event.streams[0];
            }
        };

        pc.onicecandidate = (event) => {
            if (event.candidate) {
                DotNet.invokeMethodAsync('ClientApp', 'OnIceCandidate', remoteUserId, JSON.stringify(event.candidate));
            }
        };

        this.peerConnections[remoteUserId] = pc;
        return pc;
    },

    handleSignal: async function (fromUserId, signalType, payload) {
        let pc = this.peerConnections[fromUserId];
        if (!pc) {
            pc = this.createPeerConnection(fromUserId);
        }

        try {
            if (signalType === 'offer') {
                await pc.setRemoteDescription(new RTCSessionDescription(JSON.parse(payload)));
                const answer = await pc.createAnswer();
                await pc.setLocalDescription(answer);
                DotNet.invokeMethodAsync('ClientApp', 'OnSignalReady', fromUserId, 'answer', JSON.stringify(answer));
            } else if (signalType === 'answer') {
                await pc.setRemoteDescription(new RTCSessionDescription(JSON.parse(payload)));
            } else if (signalType === 'candidate') {
                await pc.addIceCandidate(new RTCIceCandidate(JSON.parse(payload)));
            }
        } catch (err) {
            console.error('WebRTC signal error:', err);
        }
    },

    createOffer: async function (remoteUserId) {
        let pc = this.peerConnections[remoteUserId];
        if (!pc) {
            pc = this.createPeerConnection(remoteUserId);
        }

        try {
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            return JSON.stringify(offer);
        } catch (err) {
            console.error('Create offer error:', err);
            return null;
        }
    },

    toggleMute: function (muted) {
        if (this.localStream) {
            this.localStream.getAudioTracks().forEach(track => {
                track.enabled = !muted;
            });
        }
    },

    toggleCamera: function (off) {
        if (this.localStream) {
            this.localStream.getVideoTracks().forEach(track => {
                track.enabled = !off;
            });
        }
    },

    stop: function () {
        if (this.localStream) {
            this.localStream.getTracks().forEach(track => track.stop());
            this.localStream = null;
        }

        Object.values(this.peerConnections).forEach(pc => pc.close());
        this.peerConnections = {};

        const localVideo = document.getElementById('localVideo');
        if (localVideo) {
            localVideo.srcObject = null;
        }
    }
};
