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
        isFollowing: function (container) {
            return getState(container)?.autoStick ?? true;
        },
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

        // Records the current scroll extent so prepended history can be compensated
        // for. Prepending grows scrollHeight above the viewport, which would otherwise
        // shove the reader's position down by exactly the amount that was added.
        captureBeforePrepend: function (container) {
            const state = ensureState(container, null);
            if (!state) return;
            state.prependAnchor = {
                scrollHeight: container.scrollHeight,
                scrollTop: container.scrollTop
            };
        },

        restoreAfterPrepend: function (container) {
            const state = getState(container);
            if (!state || !state.prependAnchor) return;

            const delta = container.scrollHeight - state.prependAnchor.scrollHeight;
            container.scrollTop = state.prependAnchor.scrollTop + delta;
            state.prependAnchor = null;
            // Loading history is never a reason to start following the tail again.
            state.autoStick = false;
            setButtonVisibility(state);
        },

        scrollToMessage: function (container, elementId, highlight) {
            const state = ensureState(container, null);
            if (!state || !container) return false;

            // Jumping to an old message means the reader is no longer at the tail.
            state.autoStick = false;
            const target = document.getElementById(elementId);
            if (!target) return window.chatVirtualList?.jumpToMessage(container, elementId) ?? false;
            target.scrollIntoView({ block: 'center', behavior: 'smooth' });

            if (highlight === true) {
                target.classList.remove('chat-msg-flash');
                // Force a reflow so re-adding the class restarts the animation when the
                // same message is targeted twice in a row.
                void target.offsetWidth;
                target.classList.add('chat-msg-flash');
                setTimeout(() => target.classList.remove('chat-msg-flash'), 1600);
            }

            setButtonVisibility(state);
            return true;
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
