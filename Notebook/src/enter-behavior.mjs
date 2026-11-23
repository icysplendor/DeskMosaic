// Keep the editor's native rules for code, lists, tables, headings and source.
export function installEnterBehavior(editor, { enabled, before, after }) {
  const host = document.getElementById("editor");
  let inserting = false;
  const insert = (event, paragraph) => {
    if (
      inserting ||
      event.isComposing ||
      !enabled() ||
      editor.vditor.currentMode !== "ir" ||
      !event.target.isContentEditable
    )
      return;
    const root = editor.vditor.ir.element;
    const selection = getSelection();
    if (!selection?.rangeCount) return;
    const range = selection.getRangeAt(0);
    const parent = (node) =>
      (node.nodeType === Node.ELEMENT_NODE
        ? node
        : node.parentElement
      )?.closest("p");
    let p = parent(range.startContainer);
    const emptyRoot =
      !p && range.startContainer === root && !root.childNodes.length;
    if (
      !emptyRoot &&
      (!p ||
        !root.contains(p) ||
        p !== parent(range.endContainer) ||
        p.closest(
          "li,td,th,blockquote,[data-type=code-block],[data-type=html-block]",
        ))
    )
      return;
    event.preventDefault();
    event.stopImmediatePropagation();
    before();
    if (emptyRoot) {
      p = document.createElement("p");
      p.dataset.block = "0";
      root.append(p);
      range.selectNodeContents(p);
      range.collapse(false);
      selection.removeAllRanges();
      selection.addRange(range);
    }
    inserting = true;
    try {
      document.execCommand(paragraph ? "insertParagraph" : "insertLineBreak");
      if (!paragraph) {
        const caret = getSelection();
        if (caret?.rangeCount) {
          const at = caret.getRangeAt(0);
          const end = parent(at.startContainer);
          const last = end?.lastChild;
          // Chromium adds a second terminal BR as a caret landing pad. Keep
          // the authored break before the caret, remove only that extra pad.
          if (
            last?.nodeName === "BR" &&
            at.startContainer === end &&
            at.startOffset === end.childNodes.length - 1
          )
            last.remove();
          const placeholder = document.createTextNode("\u200b");
          at.insertNode(placeholder);
          at.setStartAfter(placeholder);
          at.collapse(true);
          caret.removeAllRanges();
          caret.addRange(at);
        }
      }
      after();
    } finally {
      inserting = false;
    }
  };
  host.addEventListener(
    "keydown",
    (event) => {
      if (
        event.key === "Enter" &&
        !event.ctrlKey &&
        !event.metaKey &&
        !event.altKey
      )
        insert(event, event.shiftKey);
    },
    true,
  );
  // Mobile keyboards can emit beforeinput without a keydown event.
  host.addEventListener(
    "beforeinput",
    (event) => {
      if (event.inputType === "insertParagraph") insert(event, false);
      else if (event.inputType === "insertLineBreak") insert(event, true);
    },
    true,
  );
}
