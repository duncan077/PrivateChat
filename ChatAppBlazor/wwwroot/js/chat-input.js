// Composer key handling and markdown insertion.
//
// Enter: with a multiline textarea, plain Enter would insert a newline before
// Blazor's async keydown handler runs, so the send would carry a trailing break
// and the box would flicker. preventDefault() stops only the native insertion;
// the event still propagates to Blazor, which owns send / mention-popup
// behavior. Shift+Enter falls through untouched.
//
// Insertion: the toolbar (and Ctrl+B/I/E/K shortcuts) splice markers around the
// current selection, then dispatch a bubbling 'input' event so Blazor's
// Immediate binding sees the new value — without that event the C# side would
// keep the stale text and the next render would wipe the insertion.
window.chatInput = (function () {

    function getTextarea(hostId) {
        const host = document.getElementById(hostId);
        return host ? host.querySelector('textarea') : null;
    }

    function commit(textarea, value, selStart, selEnd) {
        textarea.value = value;
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        textarea.focus();
        textarea.setSelectionRange(selStart, selEnd);
    }

    // Wraps the selection in before/after markers. An empty selection gets the
    // placeholder, left selected so typing replaces it. Wrapping the exact same
    // markers twice unwraps instead, so the buttons toggle.
    function wrapSelection(hostId, before, after, placeholder) {
        const textarea = getTextarea(hostId);
        if (!textarea) return;

        const value = textarea.value;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        const selected = value.slice(start, end);

        const alreadyWrapped =
            value.slice(start - before.length, start) === before &&
            value.slice(end, end + after.length) === after;

        if (alreadyWrapped) {
            const next = value.slice(0, start - before.length) + selected + value.slice(end + after.length);
            commit(textarea, next, start - before.length, end - before.length);
            return;
        }

        const body = selected.length > 0 ? selected : placeholder;
        const next = value.slice(0, start) + before + body + after + value.slice(end);
        commit(textarea, next, start + before.length, start + before.length + body.length);
    }

    // Prefixes every line the selection touches (quote, list). If every touched
    // line already carries the prefix, it is removed instead — a toggle.
    function prefixLines(hostId, prefix) {
        const textarea = getTextarea(hostId);
        if (!textarea) return;

        const value = textarea.value;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;

        const lineStart = value.lastIndexOf('\n', start - 1) + 1;
        let lineEnd = value.indexOf('\n', end);
        if (lineEnd === -1) lineEnd = value.length;

        const block = value.slice(lineStart, lineEnd);
        const lines = block.split('\n');
        const allPrefixed = lines.every(l => l.startsWith(prefix) || l.trim().length === 0);

        const nextBlock = lines
            .map(l => {
                if (l.trim().length === 0) return l;
                return allPrefixed ? l.slice(prefix.length) : prefix + l;
            })
            .join('\n');

        const next = value.slice(0, lineStart) + nextBlock + value.slice(lineEnd);
        const delta = nextBlock.length - block.length;
        commit(textarea, next, lineStart, lineEnd + delta);
    }

    // Fenced code block on its own lines, so the fence never glues onto text
    // before or after the cursor.
    function insertCodeBlock(hostId) {
        const textarea = getTextarea(hostId);
        if (!textarea) return;

        const value = textarea.value;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        const selected = value.slice(start, end);
        const body = selected.length > 0 ? selected : 'code';

        const needsLeadingBreak = start > 0 && value[start - 1] !== '\n';
        const needsTrailingBreak = end < value.length && value[end] !== '\n';

        const before = (needsLeadingBreak ? '\n' : '') + '```\n';
        const after = '\n```' + (needsTrailingBreak ? '\n' : '');

        const next = value.slice(0, start) + before + body + after + value.slice(end);
        commit(textarea, next, start + before.length, start + before.length + body.length);
    }

    function init(hostId) {
        const host = document.getElementById(hostId);
        if (!host) return;
        const textarea = host.querySelector('textarea');
        // The dataset flag dies with the element, so re-running init after Blazor
        // recreates the composer re-hooks the new textarea exactly once.
        if (!textarea || textarea.dataset.chatInput) return;
        textarea.dataset.chatInput = '1';
        textarea.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                return;
            }

            if (!(e.ctrlKey || e.metaKey)) return;

            const key = e.key.toLowerCase();
            if (key === 'b') {
                e.preventDefault();
                wrapSelection(hostId, '**', '**', 'bold');
            } else if (key === 'i') {
                e.preventDefault();
                wrapSelection(hostId, '*', '*', 'italic');
            } else if (key === 'e') {
                e.preventDefault();
                wrapSelection(hostId, '`', '`', 'code');
            } else if (key === 'k') {
                e.preventDefault();
                wrapSelection(hostId, '[', '](url)', 'text');
            } else if (key === 'x' && e.shiftKey) {
                e.preventDefault();
                wrapSelection(hostId, '~~', '~~', 'strikethrough');
            }
        });
    }

    return {
        init: init,
        wrapSelection: wrapSelection,
        prefixLines: prefixLines,
        insertCodeBlock: insertCodeBlock
    };
})();
