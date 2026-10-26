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

        // Any pointer ending the gesture ends the drag.
        //
        // This used to return early unless the pointerId matched the one that started
        // it, which left `dragging` true whenever it did not - a second finger lifting,
        // or the pointercancel Android raises under its own id. The window listeners
        // then stayed attached for the life of the overlay, so every pointermove
        // ANYWHERE on the page dragged the overlay under the user's finger, and at
        // z-index 9999 it intercepted whatever they were reaching for. The symptom is
        // that the entire UI goes dead and only the call window still responds.
        function pointerUp() {
            if (!dragging) return;
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

            // Never stack two drags; if one was somehow left open, close it first.
            pointerUp();

            const rect = overlay.getBoundingClientRect();

            // Pin the current width before setPosition switches to left/top anchoring.
            //
            // On narrow screens the stylesheet stretches this overlay between `left` and
            // `right` with `width: auto`, so clearing `right` leaves the width to
            // shrink-to-fit its contents. Once a call carries video those contents are a
            // <video> with intrinsic dimensions, and the box resolves far wider than the
            // panel you can see - overflow:hidden keeps it looking correct while it
            // covers the page and swallows every tap. That is why this only ever
            // happened with a video stream.
            overlay.style.width = `${rect.width}px`;

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

        // Losing capture without a pointerup - which Android does on its own when a
        // gesture is taken over - has to end the drag too, or it strands exactly as above.
        handle.addEventListener("lostpointercapture", pointerUp);

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
