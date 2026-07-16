export function attachEditor(editor) {
    const onKeyDown = event => {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
        }
    };
    editor.addEventListener("keydown", onKeyDown);
    return {
        dispose: () => editor.removeEventListener("keydown", onKeyDown)
    };
}

export function focusEditor(editor) {
    editor.focus();
    editor.setSelectionRange(editor.value.length, editor.value.length);
}

export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}
