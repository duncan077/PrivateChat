window.roomParticipantDrag = window.roomParticipantDrag || (() => {
    let dotNetRef = null;
    let pointerDragState = null;
    let listenersBound = false;
    const dragThresholdPx = 3;

    function findClosest(element, selector) {
        if (!element || typeof element.closest !== "function") return null;
        return element.closest(selector);
    }

    function invokeDotNet(methodName, ...args) {
        if (!dotNetRef || typeof dotNetRef.invokeMethodAsync !== "function") {
            return Promise.resolve();
        }
        return dotNetRef.invokeMethodAsync(methodName, ...args).catch(() => { });
    }

    function createDragGhost(sourceElement, clientX, clientY) {
        if (!sourceElement) return null;

        const rect = sourceElement.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return null;

        const ghost = sourceElement.cloneNode(true);
        ghost.classList.add("room-participant-drag-ghost");
        ghost.style.width = `${Math.max(80, rect.width)}px`;
        ghost.style.height = `${Math.max(26, rect.height)}px`;
        ghost.style.left = "0";
        ghost.style.top = "0";
        ghost.style.transform = "translate3d(-9999px, -9999px, 0)";
        document.body.appendChild(ghost);

        const offsetX = Math.max(8, Math.min(rect.width - 8, clientX - rect.left));
        const offsetY = Math.max(8, Math.min(rect.height - 8, clientY - rect.top));
        return {
            element: ghost,
            offsetX,
            offsetY
        };
    }

    function updateDragGhostPosition(state, clientX, clientY) {
        if (!state?.ghost?.element) return;

        const left = clientX - state.ghost.offsetX;
        const top = clientY - state.ghost.offsetY;
        state.ghost.element.style.transform = `translate3d(${left}px, ${top}px, 0)`;
    }

    function removeDragGhost(state) {
        if (!state?.ghost?.element) return;
        try {
            state.ghost.element.remove();
        } catch { }
        state.ghost = null;
    }

    function clearManualDropHover() {
        if (pointerDragState?.activeDropTarget) {
            pointerDragState.activeDropTarget.classList.remove("room-participant-manual-drop-hover");
            pointerDragState.activeDropTarget = null;
        }
    }

    function setManualDropHover(target) {
        if (!pointerDragState) return;
        const nextTarget = target || null;
        const current = pointerDragState.activeDropTarget || null;
        if (current === nextTarget) return;

        if (current) {
            current.classList.remove("room-participant-manual-drop-hover");
        }
        if (nextTarget) {
            nextTarget.classList.add("room-participant-manual-drop-hover");
        }
        pointerDragState.activeDropTarget = nextTarget;
    }

    function handleDragStart(event) {
        const dragSource = findClosest(event.target, "[data-room-drag-user]");
        if (!dragSource) return;

        const dataTransfer = event.dataTransfer;
        if (!dataTransfer) return;

        const sourceRoomId = dragSource.getAttribute("data-room-source-room") || "";
        const userId = dragSource.getAttribute("data-room-drag-user") || "";
        const payload = `${sourceRoomId}|${userId}`;

        try {
            dataTransfer.setData("text/plain", payload || "participant");
        } catch { }

        try {
            dataTransfer.effectAllowed = "move";
        } catch { }
    }

    function handleDragOver(event) {
        const dropTarget = findClosest(event.target, "[data-room-drop-target='1']");
        if (!dropTarget || !event.dataTransfer) return;

        try {
            event.dataTransfer.dropEffect = "move";
        } catch { }
    }

    function handlePointerDown(event) {
        if (!dotNetRef) return;
        if (event.pointerType !== "mouse" || event.button !== 0) return;

        const dragSource = findClosest(event.target, "[data-room-drag-user][draggable='true']");
        if (!dragSource) return;

        const sourceRoomId = dragSource.getAttribute("data-room-source-room") || "";
        const userId = dragSource.getAttribute("data-room-drag-user") || "";
        if (!sourceRoomId || !userId) return;

        pointerDragState = {
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            sourceElement: dragSource,
            sourceRoomId,
            userId,
            started: false,
            startInvokePromise: invokeDotNet("OnRoomParticipantDragStart", sourceRoomId, userId),
            activeDropTarget: null,
            ghost: null
        };

        dragSource.classList.add("room-participant-manual-drag-armed");
        document.body.classList.add("room-participant-manual-dragging");
        try {
            if (typeof dragSource.setPointerCapture === "function") {
                dragSource.setPointerCapture(event.pointerId);
            }
        } catch { }

        event.preventDefault();
    }

    function handlePointerMove(event) {
        if (!pointerDragState || event.pointerId !== pointerDragState.pointerId) return;

        const dx = event.clientX - pointerDragState.startX;
        const dy = event.clientY - pointerDragState.startY;
        if (!pointerDragState.started && Math.hypot(dx, dy) < dragThresholdPx) {
            return;
        }

        if (!pointerDragState.started) {
            pointerDragState.started = true;
            pointerDragState.sourceElement?.classList.remove("room-participant-manual-drag-armed");
            pointerDragState.sourceElement?.classList.add("room-participant-manual-drag-source");
            pointerDragState.ghost = createDragGhost(pointerDragState.sourceElement, event.clientX, event.clientY);
        }

        updateDragGhostPosition(pointerDragState, event.clientX, event.clientY);
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const targetRoom = findClosest(hit, "[data-room-drop-target='1'][data-room-id]");
        setManualDropHover(targetRoom);
        event.preventDefault();
    }

    async function finishPointerDrag(event) {
        if (!pointerDragState || event.pointerId !== pointerDragState.pointerId) return;
        const state = pointerDragState;
        pointerDragState = null;
        document.body.classList.remove("room-participant-manual-dragging");
        state.sourceElement?.classList.remove("room-participant-manual-drag-armed");
        state.sourceElement?.classList.remove("room-participant-manual-drag-source");
        removeDragGhost(state);
        if (state.activeDropTarget) {
            state.activeDropTarget.classList.remove("room-participant-manual-drop-hover");
        }

        await state.startInvokePromise;
        if (!state.started) {
            await invokeDotNet("OnRoomParticipantDragEnd");
            return;
        }

        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const targetRoom = findClosest(hit, "[data-room-drop-target='1'][data-room-id]");
        const targetRoomId = targetRoom?.getAttribute("data-room-id") || "";
        if (targetRoomId) {
            await invokeDotNet("OnRoomParticipantDrop", targetRoomId);
        } else {
            await invokeDotNet("OnRoomParticipantDragEnd");
        }
    }

    function cancelPointerDrag(event) {
        if (!pointerDragState || event.pointerId !== pointerDragState.pointerId) return;
        pointerDragState.sourceElement?.classList.remove("room-participant-manual-drag-armed");
        pointerDragState.sourceElement?.classList.remove("room-participant-manual-drag-source");
        removeDragGhost(pointerDragState);
        clearManualDropHover();
        pointerDragState = null;
        document.body.classList.remove("room-participant-manual-dragging");
        invokeDotNet("OnRoomParticipantDragEnd");
    }

    function bindListeners() {
        if (listenersBound) return;
        listenersBound = true;
        document.addEventListener("dragstart", handleDragStart, true);
        document.addEventListener("dragover", handleDragOver, true);
        document.addEventListener("pointerdown", handlePointerDown, true);
        document.addEventListener("pointermove", handlePointerMove, true);
        document.addEventListener("pointerup", finishPointerDrag, true);
        document.addEventListener("pointercancel", cancelPointerDrag, true);
    }

    function unbindListeners() {
        if (!listenersBound) return;
        listenersBound = false;
        document.removeEventListener("dragstart", handleDragStart, true);
        document.removeEventListener("dragover", handleDragOver, true);
        document.removeEventListener("pointerdown", handlePointerDown, true);
        document.removeEventListener("pointermove", handlePointerMove, true);
        document.removeEventListener("pointerup", finishPointerDrag, true);
        document.removeEventListener("pointercancel", cancelPointerDrag, true);
        pointerDragState?.sourceElement?.classList.remove("room-participant-manual-drag-armed");
        pointerDragState?.sourceElement?.classList.remove("room-participant-manual-drag-source");
        removeDragGhost(pointerDragState);
        clearManualDropHover();
        pointerDragState = null;
        document.body.classList.remove("room-participant-manual-dragging");
    }

    return {
        bindBlazor(ref) {
            dotNetRef = ref || null;
            bindListeners();
        },
        unbindBlazor() {
            dotNetRef = null;
            unbindListeners();
        }
    };
})();
