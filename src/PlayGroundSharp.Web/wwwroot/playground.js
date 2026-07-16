export function attachEditor(editor) {
    const onKeyDown = event => {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
        }
        if (editor.dataset.completionOpen === "true" &&
            ["Tab", "ArrowDown", "ArrowUp", "Escape"].includes(event.key)) {
            event.preventDefault();
        }
    };
    editor.addEventListener("keydown", onKeyDown);
    return {
        dispose: () => editor.removeEventListener("keydown", onKeyDown)
    };
}

export function getCaret(editor) {
    return editor.selectionStart ?? editor.value.length;
}

export function setCompletionState(editor, isOpen) {
    editor.dataset.completionOpen = isOpen ? "true" : "false";
}

export function applyCompletion(editor, start, end, text) {
    const value = editor.value.slice(0, start) + text + editor.value.slice(end);
    const caret = start + text.length;
    editor.value = value;
    editor.setSelectionRange(caret, caret);
    editor.focus();
    return { value, caret };
}

export function attachFileDropZone(dropZone) {
    const input = dropZone.querySelector('input[type="file"]');
    const prevent = event => {
        event.preventDefault();
        dropZone.classList.add("dragging");
    };
    const leave = event => {
        event.preventDefault();
        dropZone.classList.remove("dragging");
    };
    const drop = event => {
        event.preventDefault();
        dropZone.classList.remove("dragging");
        if (event.dataTransfer.files.length > 0) {
            input.files = event.dataTransfer.files;
            input.dispatchEvent(new Event("change", { bubbles: true }));
        }
    };
    dropZone.addEventListener("dragenter", prevent);
    dropZone.addEventListener("dragover", prevent);
    dropZone.addEventListener("dragleave", leave);
    dropZone.addEventListener("drop", drop);
    return {
        dispose: () => {
            dropZone.removeEventListener("dragenter", prevent);
            dropZone.removeEventListener("dragover", prevent);
            dropZone.removeEventListener("dragleave", leave);
            dropZone.removeEventListener("drop", drop);
        }
    };
}

export function focusEditor(editor) {
    editor.focus();
    editor.setSelectionRange(editor.value.length, editor.value.length);
}

export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}

export function downloadText(fileName, text) {
    const blob = new Blob([text], { type: "application/json;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
}
