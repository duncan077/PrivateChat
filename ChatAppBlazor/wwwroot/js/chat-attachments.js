'use strict';

window.chatAttachmentPaste = (() => {
    let pasteHandler = null;
    let targetInputId = '';
    let targetDropZoneId = '';
    let dropHandler = null;
    let dragOverHandler = null;
    let dragEnterHandler = null;

    const collectClipboardFiles = (clipboardData) => {
        const files = [];
        if (!clipboardData?.items) return files;

        for (const item of clipboardData.items) {
            if (!item || item.kind !== 'file') continue;
            const file = item.getAsFile?.();
            if (file) {
                files.push(file);
            }
        }

        return files;
    };

    const applyFilesToInput = (inputEl, files) => {
        if (!inputEl || !files || files.length === 0) return false;
        if (typeof DataTransfer !== 'function') return false;

        const dt = new DataTransfer();
        for (const file of files) {
            dt.items.add(file);
        }

        inputEl.files = dt.files;
        inputEl.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    };

    const getDropFiles = (event) => {
        const transfer = event?.dataTransfer;
        if (!transfer) return [];
        if (!transfer.files || transfer.files.length === 0) return [];
        return Array.from(transfer.files).filter(Boolean);
    };

    const onPaste = (event) => {
        const inputEl = document.getElementById(targetInputId);
        if (!inputEl || inputEl.disabled) return;

        const files = collectClipboardFiles(event.clipboardData);
        if (files.length === 0) return;

        const attached = applyFilesToInput(inputEl, files);
        if (attached) {
            event.preventDefault();
            event.stopPropagation();
        }
    };

    const onDrop = (event) => {
        const inputEl = document.getElementById(targetInputId);
        if (!inputEl || inputEl.disabled) return;

        const files = getDropFiles(event);
        if (files.length === 0) return;

        const attached = applyFilesToInput(inputEl, files);
        if (attached) {
            event.preventDefault();
        }
    };

    const onDragOver = (event) => {
        const transfer = event?.dataTransfer;
        if (!transfer) return;
        transfer.dropEffect = 'copy';
        event.preventDefault();
    };

    const onDragEnter = (event) => {
        const transfer = event?.dataTransfer;
        if (!transfer) return;
        event.preventDefault();
    };

    const attachDropListeners = () => {
        const dropZone = document.getElementById(targetDropZoneId);
        if (!dropZone) return;

        dropHandler = onDrop;
        dragOverHandler = onDragOver;
        dragEnterHandler = onDragEnter;

        dropZone.addEventListener('drop', dropHandler, true);
        dropZone.addEventListener('dragover', dragOverHandler, true);
        dropZone.addEventListener('dragenter', dragEnterHandler, true);
    };

    const detachDropListeners = () => {
        if (!targetDropZoneId) return;
        const dropZone = document.getElementById(targetDropZoneId);
        if (!dropZone) return;

        if (dropHandler) {
            dropZone.removeEventListener('drop', dropHandler, true);
        }
        if (dragOverHandler) {
            dropZone.removeEventListener('dragover', dragOverHandler, true);
        }
        if (dragEnterHandler) {
            dropZone.removeEventListener('dragenter', dragEnterHandler, true);
        }
    };

    return {
        init(inputId, dropZoneId) {
            const normalizedInputId = (inputId || '').toString().trim();
            if (!normalizedInputId) return;
            const normalizedDropZoneId = (dropZoneId || '').toString().trim();

            this.dispose();
            targetInputId = normalizedInputId;
            targetDropZoneId = normalizedDropZoneId;
            pasteHandler = onPaste;
            document.addEventListener('paste', pasteHandler, true);
            attachDropListeners();
        },
        dispose() {
            if (pasteHandler) {
                document.removeEventListener('paste', pasteHandler, true);
            }
            detachDropListeners();
            pasteHandler = null;
            dropHandler = null;
            dragOverHandler = null;
            dragEnterHandler = null;
            targetInputId = '';
            targetDropZoneId = '';
        }
    };
})();
