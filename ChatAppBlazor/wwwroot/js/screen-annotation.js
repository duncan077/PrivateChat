// Screen-share annotation input.
//
// Viewers draw on the video cell showing a shared screen. Pointer positions are
// converted to coordinates normalized against the *captured frame* (0..1 on both
// axes) — not against the element — so they land in the same place for everyone
// regardless of window size, display scaling, or letterboxing.
//
// Several people can share at once, and each share is annotated independently, so
// this module keeps one canvas ("surface") per sharer rather than one for the call.
// Coordinates only mean something relative to a single captured frame, which is why
// every stroke carries the sharer whose screen it was drawn on.
//
// Authoritative rendering happens on each sharer's own machine: a native overlay
// paints the ink onto their real desktop, which their capture picks up. The local
// canvases exist for two reasons:
//   1. Immediate feedback — the network + encode round trip is far too slow to be
//      the only thing the person drawing sees.
//   2. Fallback — with single-window capture (or a sharer on a platform without a
//      desktop overlay) the strokes never come back through the video, so that
//      share's canvas keeps rendering everyone's ink instead of fading it out.

window.screenAnnotation = {
    dotNetRef: null,
    localUserId: '',

    /// This user picked up the pen. One switch for every surface, matching the
    /// single tool set in the toolbar.
    canDraw: false,

    tool: 'pen',
    colorHex: '#ff3b30',
    widthScale: 1,

    // Stroke width as a fraction of the captured surface height, per tool.
    baseWidthFraction: 0.0045,
    highlighterWidthFraction: 0.022,

    /// sharerId -> surface. A surface is one share's canvas plus its stroke model:
    /// { sharerId, captureKind, localRenderOnly, autoFadeMs, cell, video, canvas,
    ///   ctx, resizeObserver, handlers, strokes[], rafHandle, fadeWakeupTimer }
    _surfaces: new Map(),

    _mountTimer: null,
    _mountAttempts: 0,
    _healthTimer: null,

    // One stroke at a time, on whichever surface the pointer went down on.
    _activeStroke: null,
    _activeSurface: null,
    _pendingPoints: [],
    _flushTimer: null,
    _strokeSeq: 0,

    FLUSH_INTERVAL_MS: 45,
    ECHO_FADE_MS: 900,
    ECHO_FADE_DURATION_MS: 450,
    AUTO_FADE_DURATION_MS: 700,
    MOUNT_RETRY_LIMIT: 40,

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    init(dotNetRef, localUserId) {
        this.dotNetRef = dotNetRef;
        this.localUserId = this.normalizeId(localUserId);
    },

    /// Called by CallStore whenever any share's annotation permission or capture
    /// details change. This is the complete set of annotatable shares, not a delta:
    /// a sharer missing from the list has their surface torn down.
    setState(payload) {
        const state = this.parsePayload(payload);
        if (!state) return;

        this.canDraw = state.canDraw === true;

        const incoming = Array.isArray(state.sessions) ? state.sessions : [];
        const seen = new Set();

        for (const entry of incoming) {
            const sharerId = this.normalizeId(entry && entry.sharerUserId);
            if (!sharerId) continue;
            seen.add(sharerId);
            this.upsertSurface(sharerId, entry);
        }

        for (const sharerId of Array.from(this._surfaces.keys())) {
            if (!seen.has(sharerId)) {
                this.dropSurface(sharerId);
            }
        }

        if (this._surfaces.size === 0) {
            this.stopHealthCheck();
            this.stopMountTimer();
            return;
        }

        for (const surface of this._surfaces.values()) {
            this.syncDrawableClass(surface);
        }

        this.scheduleMount();
        this.startHealthCheck();
    },

    parsePayload(payload) {
        if (typeof payload === 'string') {
            try {
                return JSON.parse(payload);
            } catch {
                return null;
            }
        }
        return payload || null;
    },

    upsertSurface(sharerId, entry) {
        let surface = this._surfaces.get(sharerId);
        if (!surface) {
            surface = {
                sharerId,
                captureKind: '',
                localRenderOnly: false,
                autoFadeMs: 5000,
                cell: null,
                video: null,
                canvas: null,
                ctx: null,
                resizeObserver: null,
                handlers: null,
                strokes: [],
                rafHandle: null,
                fadeWakeupTimer: null
            };
            this._surfaces.set(sharerId, surface);
            // A new share needs its own run of mount attempts, however long the
            // existing ones have been waiting.
            this._mountAttempts = 0;
        }

        surface.captureKind = entry.captureKind || '';
        surface.localRenderOnly = entry.needsLocalRendering === true;

        const fadeSeconds = Number.isFinite(entry.autoFadeSeconds) ? entry.autoFadeSeconds : 5;
        const nextAutoFadeMs = Math.max(0, fadeSeconds) * 1000;
        if (nextAutoFadeMs !== surface.autoFadeMs) {
            surface.autoFadeMs = nextAutoFadeMs;
            if (nextAutoFadeMs === 0) {
                // Pinning restarts the clock so mid-fade strokes come back to full
                // opacity rather than disappearing a moment later.
                const now = performance.now();
                for (const stroke of surface.strokes) {
                    stroke.completedAt = now;
                }
            }
            this.requestRedraw(surface);
        }

        return surface;
    },

    dropSurface(sharerId) {
        const surface = this._surfaces.get(sharerId);
        if (!surface) return;

        // unmount() cancels any stroke still being drawn here.
        this.unmount(surface);
        surface.strokes = [];
        this._surfaces.delete(sharerId);
    },

    syncDrawableClass(surface) {
        if (!surface.cell) return;
        surface.cell.classList.toggle('annotation-drawable', this.canDraw);
        if (!this.canDraw && this._activeSurface === surface) {
            this.cancelActiveStroke();
        }
    },

    /// The video grid rebuilds cells as feeds come and go, which silently drops the
    /// canvases. Re-attach whenever that happens.
    startHealthCheck() {
        if (this._healthTimer) return;
        this._healthTimer = setInterval(() => {
            if (this._surfaces.size === 0) {
                this.stopHealthCheck();
                return;
            }

            let needsMount = false;
            for (const surface of this._surfaces.values()) {
                if (!surface.canvas || !surface.canvas.isConnected || !surface.cell?.isConnected) {
                    // Keep the strokes: the share is still live, only its cell moved.
                    this.unmount(surface);
                    needsMount = true;
                }
            }

            if (needsMount) {
                this._mountAttempts = 0;
                this.scheduleMount();
            }
        }, 1500);
    },

    stopHealthCheck() {
        if (this._healthTimer) {
            clearInterval(this._healthTimer);
            this._healthTimer = null;
        }
    },

    setTool(tool) {
        const allowed = ['pen', 'highlighter', 'arrow', 'rect', 'ellipse'];
        this.tool = allowed.includes(tool) ? tool : 'pen';
    },

    setColor(colorHex) {
        if (typeof colorHex === 'string' && /^#[0-9a-fA-F]{6}$/.test(colorHex.trim())) {
            this.colorHex = colorHex.trim();
        }
    },

    setWidthScale(scale) {
        const parsed = parseFloat(scale);
        this.widthScale = Number.isFinite(parsed) ? Math.min(Math.max(parsed, 0.25), 4) : 1;
    },

    /// Applies tool, colour and width in one interop hop. Dragging the width slider used to
    /// cost three awaited round trips per step.
    setToolConfig(tool, colorHex, scale) {
        this.setTool(tool);
        this.setColor(colorHex);
        this.setWidthScale(scale);
    },

    // ─── Canvas mounting ───────────────────────────────────────────────────────

    stopMountTimer() {
        if (this._mountTimer) {
            clearTimeout(this._mountTimer);
            this._mountTimer = null;
        }
    },

    scheduleMount() {
        this.stopMountTimer();

        // A share's feed cell may not exist yet when its permission arrives, and with
        // several shares they can appear at different times - so retry until every
        // known surface has a canvas.
        const tryMount = () => {
            this._mountTimer = null;
            if (this._surfaces.size === 0) return;

            let pending = 0;
            for (const surface of this._surfaces.values()) {
                if (surface.canvas?.isConnected) continue;
                if (!this.mount(surface)) pending++;
            }

            if (pending === 0) {
                this._mountAttempts = 0;
                return;
            }

            this._mountAttempts++;
            if (this._mountAttempts <= this.MOUNT_RETRY_LIMIT) {
                this._mountTimer = setTimeout(tryMount, 300);
            }
        };

        tryMount();
    },

    mount(surface) {
        const manager = window.webrtcManager;
        if (!manager || typeof manager.findAnnotationTargetCell !== 'function') {
            return false;
        }

        const cell = manager.findAnnotationTargetCell(surface.sharerId);
        const video = cell?.querySelector('video') || null;
        if (!cell || !video) {
            return false;
        }

        if (surface.cell === cell && surface.canvas?.isConnected) {
            return true;
        }

        // Two shares must never resolve to the same cell; if the grid is mid-rebuild
        // and they briefly do, leave the existing owner alone rather than stacking
        // canvases on one video.
        for (const other of this._surfaces.values()) {
            if (other !== surface && other.cell === cell && other.canvas?.isConnected) {
                return false;
            }
        }

        this.unmount(surface);

        const canvas = document.createElement('canvas');
        canvas.className = 'annotation-canvas';
        // Inline as well as in CSS: if this ever loses the stylesheet race the
        // first touch becomes a scroll, Android fires pointercancel, and drawing
        // "randomly" fails.
        canvas.style.touchAction = 'none';
        cell.appendChild(canvas);

        // Handlers are bound per surface so a pointer event carries its share with
        // it - there is no ambiguity about which frame to normalize against.
        const handlers = {
            pointerDown: ev => this.handlePointerDown(surface, ev),
            pointerMove: ev => this.handlePointerMove(surface, ev),
            pointerUp: ev => this.handlePointerUp(surface, ev),
            stopEvent: ev => ev.stopPropagation(),
            // Belt and braces for Android WebView: touch-action SHOULD stop the
            // browser from claiming the gesture, but a non-passive preventDefault on
            // the raw touch events is the hard guarantee that a stroke is never
            // converted into a scroll/zoom mid-draw (which surfaces as pointercancel
            // and a stroke that ends after a few pixels). Pointer events still fire -
            // they are not the "default action" of a touch.
            blockNativeTouch: ev => {
                if (!this.canDraw) return;
                ev.preventDefault();
                // And keep it off the cell. The cell reveals its overlay controls on
                // touchstart, so on Android - where touch is the only input - every
                // stroke summoned the very toolbar it was being drawn under. The
                // pointer handlers below already stop theirs; touch was the one event
                // still bubbling out.
                ev.stopPropagation();
            }
        };

        canvas.addEventListener('touchstart', handlers.blockNativeTouch, { passive: false });
        canvas.addEventListener('touchmove', handlers.blockNativeTouch, { passive: false });
        canvas.addEventListener('pointerdown', handlers.pointerDown);
        canvas.addEventListener('pointermove', handlers.pointerMove);
        canvas.addEventListener('pointerup', handlers.pointerUp);
        canvas.addEventListener('pointercancel', handlers.pointerUp);
        canvas.addEventListener('pointerleave', handlers.pointerUp);
        // Stop the cell's spotlight/fullscreen handlers from firing while drawing.
        canvas.addEventListener('click', handlers.stopEvent);
        canvas.addEventListener('dblclick', handlers.stopEvent);

        surface.cell = cell;
        surface.video = video;
        surface.canvas = canvas;
        surface.ctx = canvas.getContext('2d');
        surface.handlers = handlers;

        cell.classList.add('annotation-active');
        this.syncDrawableClass(surface);

        if (typeof ResizeObserver !== 'undefined') {
            surface.resizeObserver = new ResizeObserver(() => this.resizeCanvas(surface));
            surface.resizeObserver.observe(cell);
        }

        this.resizeCanvas(surface);
        this.requestRedraw(surface);
        return true;
    },

    unmount(surface) {
        // A stroke in progress on this surface can never receive its pointerup once
        // the canvas is gone, and a stuck _activeStroke would block every later
        // stroke on every surface. The grid rebuilding cells mid-draw is normal.
        if (this._activeSurface === surface) {
            this.cancelActiveStroke();
        }

        if (surface.rafHandle) {
            cancelAnimationFrame(surface.rafHandle);
            surface.rafHandle = null;
        }

        if (surface.fadeWakeupTimer) {
            clearTimeout(surface.fadeWakeupTimer);
            surface.fadeWakeupTimer = null;
        }

        if (surface.resizeObserver) {
            try { surface.resizeObserver.disconnect(); } catch { }
            surface.resizeObserver = null;
        }

        const canvas = surface.canvas;
        const handlers = surface.handlers;
        if (canvas && handlers) {
            canvas.removeEventListener('touchstart', handlers.blockNativeTouch);
            canvas.removeEventListener('touchmove', handlers.blockNativeTouch);
            canvas.removeEventListener('pointerdown', handlers.pointerDown);
            canvas.removeEventListener('pointermove', handlers.pointerMove);
            canvas.removeEventListener('pointerup', handlers.pointerUp);
            canvas.removeEventListener('pointercancel', handlers.pointerUp);
            canvas.removeEventListener('pointerleave', handlers.pointerUp);
            canvas.removeEventListener('click', handlers.stopEvent);
            canvas.removeEventListener('dblclick', handlers.stopEvent);
        }
        if (canvas) {
            canvas.remove();
        }

        if (surface.cell) {
            surface.cell.classList.remove('annotation-active', 'annotation-drawable');
        }

        surface.cell = null;
        surface.video = null;
        surface.canvas = null;
        surface.ctx = null;
        surface.handlers = null;
    },

    resizeCanvas(surface) {
        const canvas = surface.canvas;
        const cell = surface.cell;
        if (!canvas || !cell) return;

        const rect = cell.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const width = Math.max(1, Math.round(rect.width * dpr));
        const height = Math.max(1, Math.round(rect.height * dpr));

        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
        }

        this.requestRedraw(surface);
    },

    // ─── Coordinate mapping ────────────────────────────────────────────────────

    /// Rectangle (in canvas pixels) that the video frame actually occupies inside
    /// the cell, accounting for object-fit letterboxing.
    getContentRect(surface) {
        const canvas = surface.canvas;
        const video = surface.video;
        if (!canvas || !video) return null;

        const frameWidth = video.videoWidth || 0;
        const frameHeight = video.videoHeight || 0;
        const boxWidth = canvas.width;
        const boxHeight = canvas.height;

        if (frameWidth <= 0 || frameHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) {
            return null;
        }

        let fit = 'contain';
        try {
            fit = window.getComputedStyle(video).objectFit || 'contain';
        } catch { }

        if (fit === 'fill') {
            return { left: 0, top: 0, width: boxWidth, height: boxHeight };
        }

        const scale = fit === 'cover'
            ? Math.max(boxWidth / frameWidth, boxHeight / frameHeight)
            : Math.min(boxWidth / frameWidth, boxHeight / frameHeight);

        const width = frameWidth * scale;
        const height = frameHeight * scale;
        return {
            left: (boxWidth - width) / 2,
            top: (boxHeight - height) / 2,
            width,
            height
        };
    },

    /// Pointer event → normalized frame coordinates, or null when outside the frame.
    toNormalized(surface, event) {
        const canvas = surface.canvas;
        const content = this.getContentRect(surface);
        if (!canvas || !content) return null;

        const rect = canvas.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return null;

        const x = ((event.clientX - rect.left) / rect.width) * canvas.width;
        const y = ((event.clientY - rect.top) / rect.height) * canvas.height;

        const nx = (x - content.left) / content.width;
        const ny = (y - content.top) / content.height;

        if (!Number.isFinite(nx) || !Number.isFinite(ny)) return null;
        return { x: Math.min(Math.max(nx, 0), 1), y: Math.min(Math.max(ny, 0), 1) };
    },

    // ─── Pointer handling ──────────────────────────────────────────────────────

    handlePointerDown(surface, event) {
        if (!this.canDraw) return;
        if (event.button !== 0 && event.pointerType === 'mouse') return;
        // A second finger landing mid-stroke must not hijack (and end) the
        // stroke the first finger is drawing.
        if (!event.isPrimary && event.pointerType !== 'mouse') return;
        // Drawing on a second share while a stroke is still live would leave the
        // first one dangling; finish it first.
        if (this._activeStroke) return;

        const point = this.toNormalized(surface, event);
        if (!point) return;

        event.preventDefault();
        event.stopPropagation();
        try { event.target.setPointerCapture(event.pointerId); } catch { }

        this._strokeSeq++;
        const stroke = {
            id: `${Date.now().toString(36)}-${this._strokeSeq.toString(36)}`,
            seq: this._strokeSeq,
            owner: this.localUserId,
            tool: this.tool,
            color: this.getStrokeColor(),
            widthFraction: this.getWidthFraction(),
            points: [point.x, point.y],
            pointerId: event.pointerId,
            fadeAt: 0,
            completedAt: 0
        };

        this._activeStroke = stroke;
        this._activeSurface = surface;
        surface.strokes.push(stroke);
        this._pendingPoints = [];

        this.send(surface, {
            kind: 'begin',
            strokeId: stroke.id,
            tool: stroke.tool,
            color: stroke.color,
            width: stroke.widthFraction,
            points: [point.x, point.y]
        });

        this.setOverlayControlsSuppressed(surface, true);
        this.startFlushTimer();
        this.requestRedraw(surface);
    },

    handlePointerMove(surface, event) {
        const stroke = this._activeStroke;
        if (!stroke || this._activeSurface !== surface || stroke.pointerId !== event.pointerId) return;

        const point = this.toNormalized(surface, event);
        if (!point) return;

        event.preventDefault();
        event.stopPropagation();

        if (this.isShapeTool(stroke.tool)) {
            // Shapes are anchor + current point.
            stroke.points.length = 2;
            stroke.points.push(point.x, point.y);
            this._pendingPoints = [point.x, point.y];
        } else {
            const lastX = stroke.points[stroke.points.length - 2];
            const lastY = stroke.points[stroke.points.length - 1];
            // Skip sub-pixel jitter to keep the wire payload small.
            if (Math.abs(point.x - lastX) < 0.0008 && Math.abs(point.y - lastY) < 0.0008) {
                return;
            }

            stroke.points.push(point.x, point.y);
            this._pendingPoints.push(point.x, point.y);
        }

        this.requestRedraw(surface);
    },

    handlePointerUp(surface, event) {
        const stroke = this._activeStroke;
        if (!stroke || this._activeSurface !== surface || stroke.pointerId !== event.pointerId) return;

        event.preventDefault();
        event.stopPropagation();
        try { event.target.releasePointerCapture(event.pointerId); } catch { }

        this.stopFlushTimer();

        const tail = stroke.points.slice(-2);
        this.send(surface, {
            kind: 'end',
            strokeId: stroke.id,
            tool: stroke.tool,
            color: stroke.color,
            width: stroke.widthFraction,
            points: this.isShapeTool(stroke.tool) ? tail : this._pendingPoints.slice()
        });

        this._pendingPoints = [];
        this._activeStroke = null;
        this._activeSurface = null;
        // Auto-fade counts from completion, matching the sharer's overlay.
        stroke.completedAt = performance.now();

        // When that sharer's overlay feeds strokes back through their video, retire
        // the local echo once the round trip has had time to complete.
        if (!surface.localRenderOnly) {
            stroke.fadeAt = performance.now() + this.ECHO_FADE_MS;
        }

        this.setOverlayControlsSuppressed(surface, false);
        this.requestRedraw(surface);
    },

    cancelActiveStroke() {
        this.stopFlushTimer();
        const surface = this._activeSurface;
        if (this._activeStroke && surface) {
            const index = surface.strokes.indexOf(this._activeStroke);
            if (index >= 0) {
                surface.strokes.splice(index, 1);
            }
            // Abandoning a stroke has to give the controls back too, or a cancelled
            // gesture would leave them suppressed with no way to ask for them again.
            this.setOverlayControlsSuppressed(surface, false);
            this.requestRedraw(surface);
        }
        this._activeStroke = null;
        this._activeSurface = null;
        this._pendingPoints = [];
    },

    startFlushTimer() {
        this.stopFlushTimer();
        this._flushTimer = setInterval(() => this.flushPendingPoints(), this.FLUSH_INTERVAL_MS);
    },

    stopFlushTimer() {
        if (this._flushTimer) {
            clearInterval(this._flushTimer);
            this._flushTimer = null;
        }
    },

    flushPendingPoints() {
        const stroke = this._activeStroke;
        const surface = this._activeSurface;
        if (!stroke || !surface || this._pendingPoints.length === 0) return;

        const points = this._pendingPoints;
        this._pendingPoints = [];

        this.send(surface, {
            kind: 'points',
            strokeId: stroke.id,
            tool: stroke.tool,
            color: stroke.color,
            width: stroke.widthFraction,
            points
        });
    },

    /// True while a stroke is being drawn. The video grid consults this so that
    /// drawing is not mistaken for the kind of interaction that reveals the controls.
    isDrawing() {
        return !!this._activeStroke;
    },

    /// Hides the cell's overlay controls - the action bar and, in fullscreen, this
    /// toolbar - for the duration of a stroke, then hands them back.
    ///
    /// In fullscreen the toolbar sits over the bottom of the picture, so drawing near
    /// the lower edge means drawing underneath it. Restoring on release rather than
    /// leaving it hidden matters: the canvas covers the whole cell there, so a hidden
    /// toolbar would have nothing left to tap to bring it back.
    setOverlayControlsSuppressed(surface, suppressed) {
        const manager = window.webrtcManager;
        const cell = surface?.cell;
        if (!manager || !cell) return;

        try {
            if (suppressed) {
                manager.hideCellOverlayControls?.(cell);
            } else {
                manager.showCellOverlayControls?.(cell, true);
            }
        } catch { }
    },

    isShapeTool(tool) {
        return tool === 'arrow' || tool === 'rect' || tool === 'ellipse';
    },

    getWidthFraction() {
        const base = this.tool === 'highlighter' ? this.highlighterWidthFraction : this.baseWidthFraction;
        return Math.min(Math.max(base * this.widthScale, 0.0008), 0.08);
    },

    getStrokeColor() {
        const alpha = this.tool === 'highlighter' ? 0.38 : 1;
        return this.hexToArgb(this.colorHex, alpha);
    },

    hexToArgb(hex, alpha) {
        const value = parseInt(hex.replace('#', ''), 16) || 0;
        const a = Math.round(Math.min(Math.max(alpha, 0), 1) * 255);
        return ((a << 24) >>> 0) + (value & 0xffffff);
    },

    argbToCss(argb) {
        const value = argb >>> 0;
        const a = ((value >>> 24) & 0xff) / 255;
        const r = (value >>> 16) & 0xff;
        const g = (value >>> 8) & 0xff;
        const b = value & 0xff;
        return `rgba(${r}, ${g}, ${b}, ${a.toFixed(3)})`;
    },

    normalizeId(value) {
        return (value || '').toString().trim().toLowerCase();
    },

    // ─── Outgoing ──────────────────────────────────────────────────────────────

    send(surface, payload) {
        if (!this.dotNetRef) return;
        payload.targetSharerUserId = surface.sharerId;
        try {
            this.dotNetRef.invokeMethodAsync('OnAnnotationDrawn', JSON.stringify(payload));
        } catch {
            // Bridge torn down mid-stroke.
        }
    },

    /// The surface holding this user's most recent stroke - what the toolbar's
    /// single Undo button acts on when several shares are annotatable.
    findLastDrawnSurface() {
        let best = null;
        let bestSeq = -1;

        for (const surface of this._surfaces.values()) {
            for (const stroke of surface.strokes) {
                if (stroke.owner !== this.localUserId) continue;
                const seq = stroke.seq || 0;
                if (seq > bestSeq) {
                    bestSeq = seq;
                    best = surface;
                }
            }
        }

        return best;
    },

    /// Toolbar actions. The local model is updated optimistically; the hub echo is
    /// ignored for our own id so there is no double-apply.
    undo() {
        const surface = this.findLastDrawnSurface();
        if (!surface) return;

        let bestIndex = -1;
        let bestSeq = -1;
        for (let i = 0; i < surface.strokes.length; i++) {
            const stroke = surface.strokes[i];
            if (stroke.owner !== this.localUserId) continue;
            const seq = stroke.seq || 0;
            if (seq > bestSeq) {
                bestSeq = seq;
                bestIndex = i;
            }
        }

        if (bestIndex < 0) return;
        surface.strokes.splice(bestIndex, 1);
        this.requestRedraw(surface);
        this.send(surface, { kind: 'undo', strokeId: '', tool: this.tool, color: 0, width: 0, points: [] });
    },

    /// "Erase everything I drew" means everywhere, not just the last screen touched.
    clearMine() {
        for (const surface of this._surfaces.values()) {
            const before = surface.strokes.length;
            surface.strokes = surface.strokes.filter(stroke => stroke.owner !== this.localUserId);
            if (surface.strokes.length !== before) {
                this.requestRedraw(surface);
            }
            this.send(surface, { kind: 'clear', strokeId: '', tool: this.tool, color: 0, width: 0, points: [] });
        }
    },

    /// Presenter action, scoped to this user's own share - the server rejects it
    /// for anyone else's.
    clearAll() {
        const surface = this._surfaces.get(this.localUserId);
        if (!surface) return;

        surface.strokes = [];
        this.requestRedraw(surface);
        this.send(surface, { kind: 'clearAll', strokeId: '', tool: this.tool, color: 0, width: 0, points: [] });
    },

    // ─── Incoming (local fallback rendering) ───────────────────────────────────

    /// Applies a remote participant's annotation to the canvas of the share it was
    /// drawn on. Only used when strokes will not come back through that video.
    applyRemote(fromUserId, payloadJson) {
        const owner = this.normalizeId(fromUserId);
        if (owner === this.localUserId) {
            return;
        }

        let payload;
        try {
            payload = JSON.parse(payloadJson);
        } catch {
            return;
        }

        const surface = this._surfaces.get(this.normalizeId(payload.targetSharerUserId));
        if (!surface) {
            return;
        }

        if (!surface.localRenderOnly) {
            // That sharer's desktop overlay is the source of truth for drawing, but a
            // presenter-issued "erase everything" must still drop our local echoes.
            if (payload.kind === 'clearAll') {
                surface.strokes = [];
                this.requestRedraw(surface);
            }
            return;
        }

        const strokeKey = `${owner}:${payload.strokeId || ''}`;

        switch (payload.kind) {
            case 'begin': {
                surface.strokes.push({
                    id: strokeKey,
                    seq: 0,
                    owner,
                    tool: payload.tool || 'pen',
                    color: payload.color >>> 0,
                    widthFraction: payload.width || this.baseWidthFraction,
                    points: Array.isArray(payload.points) ? payload.points.slice() : [],
                    fadeAt: 0,
                    completedAt: 0
                });
                break;
            }

            case 'points':
            case 'end': {
                let stroke = surface.strokes.find(s => s.id === strokeKey);
                if (!stroke) {
                    stroke = {
                        id: strokeKey,
                        seq: 0,
                        owner,
                        tool: payload.tool || 'pen',
                        color: payload.color >>> 0,
                        widthFraction: payload.width || this.baseWidthFraction,
                        points: [],
                        fadeAt: 0,
                        completedAt: 0
                    };
                    surface.strokes.push(stroke);
                }

                const points = Array.isArray(payload.points) ? payload.points : [];
                if (this.isShapeTool(stroke.tool)) {
                    if (points.length >= 2) {
                        stroke.points.length = Math.min(stroke.points.length, 2);
                        stroke.points.push(points[points.length - 2], points[points.length - 1]);
                    }
                } else {
                    stroke.points.push(...points);
                }

                if (payload.kind === 'end') {
                    stroke.completedAt = performance.now();
                }
                break;
            }

            case 'undo': {
                for (let i = surface.strokes.length - 1; i >= 0; i--) {
                    if (surface.strokes[i].owner === owner) {
                        surface.strokes.splice(i, 1);
                        break;
                    }
                }
                break;
            }

            case 'clear': {
                surface.strokes = surface.strokes.filter(stroke => stroke.owner !== owner);
                break;
            }

            case 'clearAll': {
                surface.strokes = [];
                break;
            }

            default:
                return;
        }

        this.requestRedraw(surface);
    },

    // ─── Rendering ─────────────────────────────────────────────────────────────

    requestRedraw(surface) {
        if (!surface || surface.rafHandle) return;
        surface.rafHandle = requestAnimationFrame(() => {
            surface.rafHandle = null;
            this.draw(surface);
        });
    },

    draw(surface) {
        const canvas = surface.canvas;
        const ctx = surface.ctx;
        if (!canvas || !ctx) return;

        ctx.clearRect(0, 0, canvas.width, canvas.height);

        const content = this.getContentRect(surface);
        if (!content) return;

        const now = performance.now();
        let needsAnotherFrame = false;

        for (let i = surface.strokes.length - 1; i >= 0; i--) {
            const stroke = surface.strokes[i];
            let opacity = 1;

            // Local echo of your own in-flight stroke, retired once the sharer's
            // overlay has had time to render it back through the video.
            if (stroke.fadeAt > 0) {
                const elapsed = now - stroke.fadeAt;
                if (elapsed >= this.ECHO_FADE_DURATION_MS) {
                    surface.strokes.splice(i, 1);
                    continue;
                }
                if (elapsed >= 0) {
                    opacity = 1 - (elapsed / this.ECHO_FADE_DURATION_MS);
                }
                needsAnotherFrame = true;
            }

            // This share's auto-fade, matching its sharer's overlay.
            if (surface.autoFadeMs > 0 && stroke.completedAt > 0) {
                const age = now - stroke.completedAt - surface.autoFadeMs;
                if (age >= this.AUTO_FADE_DURATION_MS) {
                    surface.strokes.splice(i, 1);
                    continue;
                }
                if (age >= 0) {
                    opacity = Math.min(opacity, 1 - (age / this.AUTO_FADE_DURATION_MS));
                }
                needsAnotherFrame = true;
            }

            this.drawStroke(ctx, stroke, content, opacity);
        }

        if (needsAnotherFrame) {
            this.requestRedraw(surface);
            return;
        }

        // Nothing is animating right now, but strokes sitting out their delay will
        // need a frame the moment it expires. Wake up once, then, rather than
        // holding a rAF loop open for the whole idle period.
        this.scheduleFadeWakeup(surface, now);
    },

    scheduleFadeWakeup(surface, now) {
        if (surface.fadeWakeupTimer) {
            clearTimeout(surface.fadeWakeupTimer);
            surface.fadeWakeupTimer = null;
        }

        if (surface.autoFadeMs <= 0) return;

        let soonest = Infinity;
        for (const stroke of surface.strokes) {
            if (!stroke.completedAt) continue;
            const startsAt = stroke.completedAt + surface.autoFadeMs;
            if (startsAt > now) {
                soonest = Math.min(soonest, startsAt - now);
            }
        }

        if (soonest === Infinity) return;

        surface.fadeWakeupTimer = setTimeout(() => {
            surface.fadeWakeupTimer = null;
            this.requestRedraw(surface);
        }, Math.max(16, soonest));
    },

    drawStroke(ctx, stroke, content, opacity) {
        const points = stroke.points;
        if (!points || points.length < 2) return;

        const lineWidth = Math.max(1, stroke.widthFraction * content.height);

        ctx.save();
        ctx.globalAlpha = opacity;
        ctx.strokeStyle = this.argbToCss(stroke.color);
        ctx.lineWidth = lineWidth;
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';

        const toX = nx => content.left + (nx * content.width);
        const toY = ny => content.top + (ny * content.height);

        if (stroke.tool === 'rect' && points.length >= 4) {
            const x0 = toX(points[0]);
            const y0 = toY(points[1]);
            const x1 = toX(points[points.length - 2]);
            const y1 = toY(points[points.length - 1]);
            ctx.strokeRect(Math.min(x0, x1), Math.min(y0, y1), Math.abs(x1 - x0), Math.abs(y1 - y0));
            ctx.restore();
            return;
        }

        if (stroke.tool === 'ellipse' && points.length >= 4) {
            const x0 = toX(points[0]);
            const y0 = toY(points[1]);
            const x1 = toX(points[points.length - 2]);
            const y1 = toY(points[points.length - 1]);
            ctx.beginPath();
            ctx.ellipse(
                (x0 + x1) / 2,
                (y0 + y1) / 2,
                Math.max(Math.abs(x1 - x0) / 2, 0.5),
                Math.max(Math.abs(y1 - y0) / 2, 0.5),
                0,
                0,
                Math.PI * 2);
            ctx.stroke();
            ctx.restore();
            return;
        }

        if (stroke.tool === 'arrow' && points.length >= 4) {
            const x0 = toX(points[0]);
            const y0 = toY(points[1]);
            const x1 = toX(points[points.length - 2]);
            const y1 = toY(points[points.length - 1]);

            ctx.beginPath();
            ctx.moveTo(x0, y0);
            ctx.lineTo(x1, y1);
            ctx.stroke();

            const dx = x1 - x0;
            const dy = y1 - y0;
            const length = Math.sqrt((dx * dx) + (dy * dy));
            if (length > 0.5) {
                const headLength = Math.min(Math.max(length * 0.28, lineWidth * 3.5), lineWidth * 12);
                const angle = Math.atan2(dy, dx);
                const spread = 0.5;

                ctx.beginPath();
                ctx.moveTo(x1, y1);
                ctx.lineTo(x1 - (headLength * Math.cos(angle - spread)), y1 - (headLength * Math.sin(angle - spread)));
                ctx.moveTo(x1, y1);
                ctx.lineTo(x1 - (headLength * Math.cos(angle + spread)), y1 - (headLength * Math.sin(angle + spread)));
                ctx.stroke();
            }

            ctx.restore();
            return;
        }

        ctx.beginPath();
        ctx.moveTo(toX(points[0]), toY(points[1]));
        if (points.length === 2) {
            // A tap renders as a dot.
            ctx.lineTo(toX(points[0]) + 0.01, toY(points[1]));
        } else {
            for (let i = 2; i + 1 < points.length; i += 2) {
                ctx.lineTo(toX(points[i]), toY(points[i + 1]));
            }
        }
        ctx.stroke();
        ctx.restore();
    }
};
