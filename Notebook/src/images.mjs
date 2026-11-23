import { attachmentPath } from "./core.mjs";

// Adapter for the pinned Vditor/Lute IR renderer. Markdown is held in the IR
// marker spans; image src is display-only. Transform in an inert template before
// insertion, so a reference/... URL never tries to load from the application bundle.
export function installImageRenderer(lute, resolveImage) {
  const stripAttachmentPreviews = (html) => {
    if (!html.includes("data-notebook-attachment-preview")) return html;
    const template = document.createElement("template");
    template.innerHTML = html;
    template.content
      .querySelectorAll("[data-notebook-attachment-preview]")
      .forEach((node) => node.remove());
    return template.innerHTML;
  };
  const serialize = lute.VditorIRDOM2Md.bind(lute);
  lute.VditorIRDOM2Md = (html) => serialize(stripAttachmentPreviews(html));
  for (const name of [
    "Md2VditorIRDOM",
    "SpinVditorIRDOM",
    "HTML2VditorIRDOM",
  ]) {
    const render = lute[name].bind(lute);
    lute[name] = (...args) => {
      if (name !== "Md2VditorIRDOM") args[0] = stripAttachmentPreviews(args[0]);
      const html = render(...args);
      // Avoid parsing the rendered document a second time when it contains
      // neither images nor links that could refer to attachments.
      if (!/<img\b|data-type="a"/i.test(html)) return html;
      const template = document.createElement("template");
      template.innerHTML = html;
      for (const wrapper of template.content.querySelectorAll(
        '[data-type="a"]',
      )) {
        const href = wrapper.querySelector(
          ":scope > .vditor-ir__marker--link",
        )?.textContent;
        if (!href || !attachmentPath(href)) continue;
        wrapper.dataset.notebookAttachment = "";
        wrapper.contentEditable = "false";
        const label = wrapper.cloneNode(true);
        label
          .querySelectorAll(
            ".vditor-ir__marker,[data-notebook-attachment-preview]",
          )
          .forEach((node) => node.remove());
        wrapper
          .querySelectorAll("[data-notebook-attachment-preview]")
          .forEach((node) => node.remove());
        const link = document.createElement("a");
        link.dataset.notebookAttachmentPreview = "";
        link.href = href;
        link.textContent = label.textContent;
        link.className = "notebook-attachment-link";
        link.dataset.filetype = label.textContent
          .split(".")
          .at(-1)
          .slice(0, 8)
          .toUpperCase();
        link.title = "点击打开附件；右键可另存为";
        wrapper.append(link);
      }
      for (const image of template.content.querySelectorAll("img")) {
        // The entire image link is one editing unit. Clicking the preview must
        // not place the caret inside just its hidden URL marker.
        image
          .closest('[data-type="img"]')
          ?.setAttribute("contenteditable", "false");
        const original =
          image.dataset.notebookReference || image.getAttribute("src");
        const path = attachmentPath(original || "");
        if (!path) continue;
        image.dataset.notebookReference = original;
        image.src =
          resolveImage(path) ||
          "data:image/svg+xml," +
            encodeURIComponent(
              '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="48"><text x="8" y="28" fill="#72829a" font-size="14">附件图片暂时无法加载</text></svg>',
            );
      }
      return template.innerHTML;
    };
  }
}

export function installImageSelection(editor, readOnly = () => false) {
  const host = document.getElementById("editor");
  let selected;
  const select = (event) => {
    if (readOnly() || editor.vditor.currentMode !== "ir" || event.button !== 0)
      return;
    const image = event.target.closest('.vditor-ir [data-type="img"]');
    if (!image) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    selected?.classList.remove("notebook-image-selected");
    selected = image;
    image.classList.add("notebook-image-selected");
    const root = editor.vditor.ir.element;
    root.focus({ preventScroll: true });
    const range = document.createRange();
    range.selectNode(image);
    const selection = getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  };
  document.addEventListener("selectionchange", () => {
    if (!selected) return;
    const selection = getSelection();
    const range = selection.rangeCount ? selection.getRangeAt(0) : null;
    const index = selected.parentNode
      ? [...selected.parentNode.childNodes].indexOf(selected)
      : -1;
    if (
      !host.contains(selected) ||
      !range ||
      range.startContainer !== selected.parentNode ||
      range.endContainer !== selected.parentNode ||
      range.startOffset !== index ||
      range.endOffset !== index + 1
    ) {
      selected.classList.remove("notebook-image-selected");
      selected = null;
    }
  });
  host.addEventListener("mousedown", select, true);
  host.addEventListener("click", select, true);
}
