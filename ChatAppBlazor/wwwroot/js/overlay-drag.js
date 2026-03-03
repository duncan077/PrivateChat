window.overlayDrag = window.overlayDrag || (() => {
    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    function initFloatingCallDrag(overlayId, handleId) {
        if (!overlayId || !handleId) return;

        const overlay = document.getElementById(overlayId);
        const handle = document.getElementById(handleId);
        if (!overlay || !handle) return;
        if (handle.dataset.overlayDragBound === "1") return;
        handle.dataset.overlayDragBound = "1";
        handle.style.cursor = "grab";

        let dragging = false;
        let activePointerId = null;
        let startX = 0;
        let startY = 0;
        let startLeft = 0;
        let startTop = 0;

        function measure() {
            const rect = overlay.getBoundingClientRect();
            return {
                width: rect.width,
                height: rect.height
            };
        }

        function setPosition(left, top) {
            const size = measure();
            const maxLeft = Math.max(8, window.innerWidth - size.width - 8);
            const maxTop = Math.max(8, window.innerHeight - size.height - 8);
            const nextLeft = clamp(left, 8, maxLeft);
            const nextTop = clamp(top, 8, maxTop);

            overlay.style.left = `${nextLeft}px`;
            overlay.style.top = `${nextTop}px`;
            overlay.style.right = "auto";
            overlay.style.bottom = "auto";
        }

        function pointerMove(event) {
            if (!dragging) return;
            const deltaX = event.clientX - startX;
            const deltaY = event.clientY - startY;
            setPosition(startLeft + deltaX, startTop + deltaY);
        }

        function pointerUp(event) {
            if (!dragging) return;
            if (activePointerId !== null && event && event.pointerId !== activePointerId) return;
            dragging = false;
            activePointerId = null;
            overlay.classList.remove("dragging");
            handle.style.cursor = "grab";
            window.removeEventListener("pointermove", pointerMove);
            window.removeEventListener("pointerup", pointerUp);
            window.removeEventListener("pointercancel", pointerUp);
        }

        handle.addEventListener("pointerdown", (event) => {
            if (event.pointerType !== "touch" && event.button !== 0) return;

            const ignored = event.target.closest("button,.mud-icon-button");
            if (ignored) return;

            const rect = overlay.getBoundingClientRect();
            dragging = true;
            startX = event.clientX;
            startY = event.clientY;
            startLeft = rect.left;
            startTop = rect.top;
            activePointerId = event.pointerId;
            overlay.classList.add("dragging");
            handle.style.cursor = "grabbing";

            window.addEventListener("pointermove", pointerMove);
            window.addEventListener("pointerup", pointerUp);
            window.addEventListener("pointercancel", pointerUp);
            try {
                handle.setPointerCapture(event.pointerId);
            } catch {
                // Ignore browsers that don't support pointer capture.
            }
            event.preventDefault();
        });

        window.addEventListener("resize", () => {
            if (!overlay.style.left || !overlay.style.top) return;
            const left = parseFloat(overlay.style.left || "0");
            const top = parseFloat(overlay.style.top || "0");
            setPosition(left, top);
        });
    }

    return {
        initFloatingCallDrag
    };
})();
