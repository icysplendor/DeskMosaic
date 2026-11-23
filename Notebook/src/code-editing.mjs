// IR events target the outer editable PRE in WebKit, even when the caret is
// inside a code block. Locate the source from the selection, never the target.
export function selectedCode(root) {
  const selection = getSelection();
  if (!selection?.rangeCount) return null;
  const range = selection.getRangeAt(0);
  const bodyAt = (node) => {
    const element =
      node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    return (
      element?.closest(
        "pre.vditor-ir__marker--pre > code, .vditor-ir__preview > code",
      ) ||
      (element?.matches("pre.vditor-ir__marker--pre")
        ? element.querySelector(":scope > code")
        : null)
    );
  };
  const body = bodyAt(range.startContainer) || bodyAt(range.endContainer);
  if (!body || !root.contains(body)) return null;
  const local = range.cloneRange();
  // WebKit can put a boundary on the enclosing PRE instead of its CODE.
  // Accept only the immediate body edges, never a selection across blocks.
  for (const [side, node, offset] of [
    ["Start", range.startContainer, range.startOffset],
    ["End", range.endContainer, range.endOffset],
  ]) {
    if (body.contains(node)) continue;
    const index = [...body.parentNode.childNodes].indexOf(body);
    if (node !== body.parentNode || offset !== index + (side === "End" ? 1 : 0))
      return null;
    local[`set${side}`](body, side === "Start" ? 0 : body.childNodes.length);
  }
  const block = body.closest('[data-type="code-block"]');
  const source = block?.querySelector("pre.vditor-ir__marker--pre > code");
  if (!source) return null;
  let normalized = local;
  if (body !== source) {
    const prefix = document.createRange();
    prefix.selectNodeContents(body);
    prefix.setEnd(local.startContainer, local.startOffset);
    const start = prefix.toString().length;
    prefix.setEnd(local.endContainer, local.endOffset);
    const end = prefix.toString().length;
    const point = (offset) => {
      const walker = document.createTreeWalker(source, NodeFilter.SHOW_TEXT);
      while (walker.nextNode()) {
        if (offset <= walker.currentNode.length)
          return [walker.currentNode, offset];
        offset -= walker.currentNode.length;
      }
      return [source, source.childNodes.length];
    };
    normalized = document.createRange();
    normalized.setStart(...point(start));
    normalized.setEnd(...point(end));
  }
  return { source, range: normalized, selection, block };
}

export function setCodeText(source, text) {
  source.textContent = text + "\n";
  const block = source.closest('[data-type="code-block"]');
  const open = block?.querySelector(
    ':scope > [data-type="code-block-open-marker"]',
  );
  const close = block?.querySelector(
    ':scope > [data-type="code-block-close-marker"]',
  );
  if (open && close) {
    const marker = open.textContent.startsWith("~") ? "~" : "`";
    const runs = text.match(marker === "~" ? /~+/g : /`+/g) || [];
    const length = runs.reduce(
      (size, run) => Math.max(size, run.length + 1),
      Math.max(3, open.textContent.length),
    );
    // Pasted Markdown fences must stay literal code after save/reopen.
    open.textContent = close.textContent = marker.repeat(length);
  }
  const preview = block?.querySelector(".vditor-ir__preview > code");
  if (preview) preview.textContent = source.textContent;
}

export function installCodeEditing(editor, { enabled, before, after }) {
  const host = document.getElementById("editor");
  const current = (event) =>
    enabled() &&
    !host.inert &&
    !document.body.inert &&
    (event.target === document ||
      event.target === document.body ||
      host.contains(event.target)) &&
    !event.target?.closest?.("input,textarea,select,[role=textbox]") &&
    editor.vditor.currentMode === "ir"
      ? selectedCode(editor.vditor.ir.element)
      : null;
  const activate = (state) => {
    state.block.classList.add("vditor-ir__node--expand");
    editor.vditor.ir.element.focus({ preventScroll: true });
  };
  const stop = (event) => {
    event.preventDefault();
    event.stopImmediatePropagation();
  };
  const replace = (state, text) => {
    before();
    activate(state);
    const { source, range, selection } = state;
    const prefix = document.createRange();
    prefix.selectNodeContents(source);
    prefix.setEnd(range.startContainer, range.startOffset);
    const start = prefix.toString().length;
    prefix.setEnd(range.endContainer, range.endOffset);
    const end = prefix.toString().length;
    const value = source.textContent;
    // Keep a structural LF for Lute, outside the editable code content.
    const content = value.replace(/\n$/, "");
    const at = Math.min(start, content.length);
    const next =
      content.slice(0, at) +
      text +
      content.slice(Math.min(end, content.length));
    setCodeText(source, next);
    const caret = document.createRange();
    caret.setStart(source.firstChild, at + text.length);
    caret.collapse(true);
    selection.removeAllRanges();
    selection.addRange(caret);
    after();
  };
  document.addEventListener(
    "paste",
    (event) => {
      const state = current(event);
      if (!state || !event.clipboardData) return;
      stop(event);
      if (!event.clipboardData.types.includes("text/plain")) return;
      replace(
        state,
        event.clipboardData.getData("text/plain").replace(/\r\n?/g, "\n"),
      );
    },
    true,
  );
  document.addEventListener(
    "keydown",
    (event) => {
      const state = current(event);
      if (!state || event.isComposing) return;
      if (
        (event.metaKey || event.ctrlKey) &&
        !event.altKey &&
        event.key.toLowerCase() === "a"
      ) {
        stop(event);
        activate(state);
        state.range.selectNodeContents(state.source);
        state.selection.removeAllRanges();
        state.selection.addRange(state.range);
      } else if (
        ["Backspace", "Delete"].includes(event.key) &&
        !state.range.collapsed
      ) {
        stop(event);
        replace(state, "");
      }
    },
    true,
  );
  document.addEventListener(
    "beforeinput",
    (event) => {
      const state = current(event);
      if (
        state &&
        event.cancelable &&
        event.inputType?.startsWith("delete") &&
        !state.range.collapsed
      ) {
        stop(event);
        replace(state, "");
      }
    },
    true,
  );
  document.addEventListener(
    "copy",
    (event) => {
      const state = current(event);
      if (!state || state.range.collapsed || !event.clipboardData) return;
      stop(event);
      event.clipboardData.setData("text/plain", state.range.toString());
    },
    true,
  );
  document.addEventListener(
    "cut",
    (event) => {
      const state = current(event);
      if (!state || state.range.collapsed || !event.clipboardData) return;
      stop(event);
      event.clipboardData.setData("text/plain", state.range.toString());
      replace(state, "");
    },
    true,
  );
}
