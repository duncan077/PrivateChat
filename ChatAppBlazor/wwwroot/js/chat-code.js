// Applies highlight.js to code blocks rendered since the last pass. Blocks are
// stamped so re-renders of the message list never double-highlight.
window.chatCode = (function () {
    function highlightNew(container) {
        if (!window.hljs || !container || !container.querySelectorAll) return;
        const blocks = container.querySelectorAll('pre code:not([data-hl])');
        for (const block of blocks) {
            block.dataset.hl = '1';
            try {
                window.hljs.highlightElement(block);
            } catch (e) {
                // A bad language tag must not break the render loop.
            }
        }
    }

    return { highlightNew: highlightNew };
})();
