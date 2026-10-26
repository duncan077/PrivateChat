(function () {
    'use strict';
    const states = new WeakMap();
    const estimate = 96;
    const overscan = 600;

    function offset(state) {
        return state.root.getBoundingClientRect().top - state.container.getBoundingClientRect().top + state.container.scrollTop;
    }

    function indexAt(prefix, y) {
        let low = 0, high = prefix.length - 1;
        while (low < high) {
            const mid = Math.floor((low + high + 1) / 2);
            if (prefix[mid] <= y) low = mid;
            else high = mid - 1;
        }
        return Math.min(low, Math.max(0, prefix.length - 2));
    }

    function rememberAnchor(state) {
        if (!state.keys.length) { state.anchor = null; return; }
        const y = state.container.scrollTop - offset(state);
        const index = indexAt(state.prefix, Math.max(0, y));
        state.anchor = { key: state.keys[index], delta: y - state.prefix[index] };
    }

    function schedule(state) {
        if (state.disposed || state.frame) return;
        state.frame = requestAnimationFrame(() => { state.frame = 0; layout(state); });
    }

    function layout(state) {
        if (state.disposed || !state.root.isConnected) return;
        const stick = window.chatScrollManager?.isFollowing(state.container) !== false;
        const anchor = state.anchor;
        const rows = Array.from(state.root.querySelectorAll(':scope > [data-virtual-key]'));
        for (const row of rows) {
            state.heights.set(row.dataset.virtualKey, Math.max(1, row.getBoundingClientRect().height));
        }
        state.prefix = [0];
        for (const key of state.keys) state.prefix.push(state.prefix.at(-1) + (state.heights.get(key) || estimate));
        const first = rows.length ? state.indices.get(rows[0].dataset.virtualKey) : 0;
        const last = rows.length ? state.indices.get(rows.at(-1).dataset.virtualKey) + 1 : 0;
        state.root.querySelector('[data-virtual-spacer="before"]').style.height = `${state.prefix[first] || 0}px`;
        state.root.querySelector('[data-virtual-spacer="after"]').style.height = `${Math.max(0, state.prefix.at(-1) - (state.prefix[last] || 0))}px`;

        if (stick) {
            state.container.scrollTop = state.container.scrollHeight;
        } else if (anchor && state.indices.has(anchor.key)) {
            state.container.scrollTop = offset(state) + state.prefix[state.indices.get(anchor.key)] + anchor.delta;
        }
        rememberAnchor(state);

        const y = Math.max(0, state.container.scrollTop - offset(state));
        const start = indexAt(state.prefix, Math.max(0, y - overscan));
        const end = Math.min(state.keys.length, start + 160,
            indexAt(state.prefix, y + state.container.clientHeight + overscan) + 1);
        if ((first !== start || last !== end) && !state.pending) {
            state.pending = true;
            state.dotnet.invokeMethodAsync('SetWindow', start, end, state.revision)
                .catch(() => { /* Navigation can dispose the component during a callback. */ })
                .finally(() => { state.pending = false; if (!state.disposed) schedule(state); });
        }
    }

    function create(root, dotnet) {
        const container = root.closest('.chat-messages-area');
        const state = { root, dotnet, container, keys: [], indices: new Map(), heights: new Map(),
            prefix: [0], anchor: null, frame: 0, pending: false, disposed: false, revision: 0 };
        state.onScroll = () => { rememberAnchor(state); schedule(state); };
        state.resize = new ResizeObserver(entries => {
            if (entries.some(entry => entry.target === container && state.width !== container.clientWidth)) {
                state.width = container.clientWidth;
                state.heights.clear();
            }
            schedule(state);
        });
        state.width = container.clientWidth;
        state.resize.observe(container);
        container.addEventListener('scroll', state.onScroll, { passive: true });
        states.set(root, state);
        return state;
    }

    window.chatVirtualList = {
        sync(root, dotnet, keys, revision) {
            const state = states.get(root) || create(root, dotnet);
            state.revision = revision;
            if (keys) {
                state.keys = keys;
                state.indices = new Map(keys.map((key, index) => [key, index]));
                for (const key of state.heights.keys()) if (!state.indices.has(key)) state.heights.delete(key);
            }
            state.resize.disconnect();
            state.resize.observe(state.container);
            for (const row of root.querySelectorAll(':scope > [data-virtual-key]')) state.resize.observe(row);
            // Content entering through a window change does not rerender the parent page.
            window.chatCode?.highlightNew(root);
            layout(state);
        },
        jumpToMessage(container, elementId) {
            const root = container.querySelector('.chat-virtual-list');
            const state = root && states.get(root);
            const key = elementId.replace(/^msg-/, '');
            if (!state || !state.indices.has(key)) return false;
            const index = state.indices.get(key);
            state.container.scrollTop = offset(state) + state.prefix[index];
            rememberAnchor(state);
            schedule(state);
            return true;
        },
        dispose(root) {
            const state = states.get(root);
            if (!state) return;
            state.disposed = true;
            cancelAnimationFrame(state.frame);
            state.resize.disconnect();
            state.container.removeEventListener('scroll', state.onScroll);
            states.delete(root);
        }
    };
})();
