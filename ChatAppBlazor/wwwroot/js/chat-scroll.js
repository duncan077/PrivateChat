(function () {
    'use strict';

    const states = new WeakMap();

    function toNumber(value, fallback) {
        const n = Number(value);
        return Number.isFinite(n) ? n : fallback;
    }

    function distanceFromBottom(container) {
        if (!container) return 0;
        return Math.max(0, container.scrollHeight - container.clientHeight - container.scrollTop);
    }

    function isNearBottom(container, snapPx) {
        return distanceFromBottom(container) <= snapPx;
    }

    function setButtonVisibility(state) {
        if (!state || !state.button) return;
        const shouldShow = !state.autoStick && distanceFromBottom(state.container) > state.buttonShowPx;
        state.button.classList.toggle('visible', shouldShow);
    }

    function getState(container) {
        if (!container) return null;
        return states.get(container) || null;
    }

    function createState(container, button, snapPx, buttonShowPx) {
        if (!container) return null;

        const state = {
            container,
            button: button || null,
            snapPx: Math.max(4, toNumber(snapPx, 20)),
            buttonShowPx: Math.max(20, toNumber(buttonShowPx, 180)),
            autoStick: true,
            onScroll: null
        };

        state.onScroll = () => {
            state.autoStick = isNearBottom(container, state.snapPx);
            setButtonVisibility(state);
        };

        container.addEventListener('scroll', state.onScroll, { passive: true });
        states.set(container, state);
        return state;
    }

    function ensureState(container, button, snapPx, buttonShowPx) {
        let state = getState(container);
        if (!state) {
            state = createState(container, button, snapPx, buttonShowPx);
        } else {
            if (button) state.button = button;
            if (snapPx !== undefined) state.snapPx = Math.max(4, toNumber(snapPx, state.snapPx));
            if (buttonShowPx !== undefined) state.buttonShowPx = Math.max(20, toNumber(buttonShowPx, state.buttonShowPx));
        }

        if (!state) return null;
        if (isNearBottom(container, state.snapPx)) {
            state.autoStick = true;
        }
        setButtonVisibility(state);
        return state;
    }

    window.chatScrollManager = {
        init: function (container, button, snapPx, buttonShowPx) {
            ensureState(container, button, snapPx, buttonShowPx);
        },

        onMessagesChanged: function (container, forceToBottom) {
            const state = ensureState(container, null);
            if (!state) return;

            if (forceToBottom === true) {
                state.autoStick = true;
            }

            if (state.autoStick) {
                container.scrollTop = container.scrollHeight;
            }

            setButtonVisibility(state);
        },

        jumpToLatest: function (container) {
            const state = ensureState(container, null);
            if (!state) return;

            state.autoStick = true;
            container.scrollTop = container.scrollHeight;
            setButtonVisibility(state);
        },

        dispose: function (container) {
            const state = getState(container);
            if (!state) return;

            try {
                state.container.removeEventListener('scroll', state.onScroll);
            } catch {
                // Ignore cleanup errors.
            }
            states.delete(container);
        }
    };
})();
