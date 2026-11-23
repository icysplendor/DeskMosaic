import MarkdownIt from "markdown-it";

const parser = new MarkdownIt({ html: true });
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
function blocks(markdown) {
  return parser
    .parse(markdown, {})
    .filter((token) => token.level === 0 && token.map && token.nesting !== -1);
}
function sourceLines(source, work) {
  // A textarea does not expose line rectangles. Measure wrapped logical lines
  // with the same font, width and padding, without changing its selection.
  const style = getComputedStyle(source),
    mirror = document.createElement("div");
  for (const key of [
    "font",
    "letterSpacing",
    "lineHeight",
    "padding",
    "tabSize",
    "wordBreak",
    "overflowWrap",
    "textIndent",
  ])
    mirror.style[key] = style[key];
  Object.assign(mirror.style, {
    position: "fixed",
    left: "-100000px",
    top: "0",
    visibility: "hidden",
    width: source.clientWidth + "px",
    boxSizing: "border-box",
    whiteSpace: source.wrap === "off" ? "pre" : "pre-wrap",
  });
  const height =
    parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
  const lines = source.value.split("\n").map((text) => {
    const line = document.createElement("div");
    line.textContent = text || "\u200b";
    line.style.minHeight = height + "px";
    mirror.append(line);
    return line;
  });
  document.body.append(mirror);
  try {
    return work(lines);
  } finally {
    mirror.remove();
  }
}
function rootFor(editor) {
  return editor.vditor.currentMode === "sv"
    ? editor.vditor.sv.element
    : editor.vditor.ir.element;
}
function renderedBlocks(root) {
  return [...root.children].filter((element) =>
    element.hasAttribute("data-block"),
  );
}
function firstLine(root) {
  return (
    root.getBoundingClientRect().top +
    root.clientTop +
    (parseFloat(getComputedStyle(root).paddingTop) || 0)
  );
}
function codeBody(block) {
  return block.querySelector(
    block.classList.contains("vditor-ir__node--expand")
      ? ".vditor-ir__marker--pre > code"
      : ".vditor-ir__preview > code",
  );
}
export function captureReadingPosition(editor) {
  const root = rootFor(editor),
    tokens = blocks(editor.getValue());
  const ratio =
    root.scrollTop / Math.max(1, root.scrollHeight - root.clientHeight);
  if (!tokens.length) return { ratio };
  if (editor.vditor.currentMode === "sv") {
    return sourceLines(root, (lines) => {
      const y =
        root.scrollTop + (parseFloat(getComputedStyle(root).paddingTop) || 0);
      const index = lines.findIndex(
        (line) => line.offsetTop + line.offsetHeight > y,
      );
      const line = index < 0 ? lines.length - 1 : index;
      const block = tokens.findIndex((token) => token.map[1] > line);
      const tokenIndex = block < 0 ? tokens.length - 1 : block;
      const offset = clamp(
        line - tokens[tokenIndex].map[0],
        0,
        tokens[tokenIndex].map[1] - tokens[tokenIndex].map[0] - 1,
      );
      return {
        block: tokenIndex,
        offset,
        fraction: clamp(
          (y - lines[line].offsetTop) / Math.max(1, lines[line].offsetHeight),
          0,
          1,
        ),
        ratio,
      };
    });
  }
  const elements = renderedBlocks(root),
    y = firstLine(root);
  if (elements.length !== tokens.length) return { ratio };
  let index = 0,
    distance = Infinity;
  elements.forEach((element, i) => {
    const rect = element.getBoundingClientRect();
    const delta = Math.max(rect.top - y, y - rect.bottom, 0);
    if (delta < distance) {
      distance = delta;
      index = i;
    }
  });
  const token = tokens[index],
    element = elements[index],
    rect = element.getBoundingClientRect();
  const count = token.map[1] - token.map[0];
  let position =
    clamp((y - rect.top) / Math.max(1, rect.height), 0, 0.999999) * count;
  if (token.type === "fence") {
    const code = codeBody(element),
      header = element.querySelector(".vditor-copy");
    if (code && header) {
      const body = code.getBoundingClientRect(),
        style = getComputedStyle(code);
      position =
        y < body.top
          ? clamp((y - rect.top) / header.offsetHeight, 0, 0.999999)
          : 1 +
            clamp(
              (y - body.top + code.scrollTop - parseFloat(style.paddingTop)) /
                parseFloat(style.lineHeight),
              0,
              Math.max(0, count - 2.000001),
            );
    }
  }
  return {
    block: index,
    offset: Math.floor(position),
    fraction: position % 1,
    ratio,
  };
}
function applyPosition(editor, anchor) {
  const root = rootFor(editor),
    tokens = blocks(editor.getValue());
  if (anchor.block === undefined || !tokens[anchor.block]) {
    root.scrollTop =
      anchor.ratio * Math.max(0, root.scrollHeight - root.clientHeight);
    return;
  }
  const token = tokens[anchor.block],
    count = token.map[1] - token.map[0];
  const offset = clamp(anchor.offset, 0, count - 1),
    fraction = anchor.fraction;
  if (editor.vditor.currentMode === "sv") {
    sourceLines(root, (lines) => {
      const line = lines[Math.min(lines.length - 1, token.map[0] + offset)];
      root.scrollTop =
        line.offsetTop +
        line.offsetHeight * fraction -
        (parseFloat(getComputedStyle(root).paddingTop) || 0);
    });
    return;
  }
  const elements = renderedBlocks(root),
    element = elements[anchor.block];
  if (elements.length !== tokens.length || !element) {
    root.scrollTop =
      anchor.ratio * Math.max(0, root.scrollHeight - root.clientHeight);
    return;
  }
  let y =
    element.getBoundingClientRect().top +
    ((offset + fraction) / count) * element.getBoundingClientRect().height;
  if (token.type === "fence") {
    const code = codeBody(element),
      header = element.querySelector(".vditor-copy");
    if (code && header) {
      if (!offset)
        y = header.getBoundingClientRect().top + fraction * header.offsetHeight;
      else {
        const style = getComputedStyle(code),
          lineY =
            parseFloat(style.paddingTop) +
            (offset - 1 + fraction) * parseFloat(style.lineHeight);
        code.scrollTop = clamp(
          lineY - (parseFloat(style.paddingTop) || 0),
          0,
          code.scrollHeight - code.clientHeight,
        );
        y = code.getBoundingClientRect().top + lineY - code.scrollTop;
      }
    }
  }
  root.scrollTop += y - firstLine(root);
}
export function restoreReadingPosition(editor, anchor) {
  let cancelled = false,
    frame;
  const root = rootFor(editor);
  const apply = () => {
    if (!cancelled) applyPosition(editor, anchor);
  };
  const schedule = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(apply);
  };
  // Allow image/font/highlight layout to settle, but stop as soon as the user acts.
  const observer = new ResizeObserver(schedule);
  observer.observe(root);
  if (editor.vditor.currentMode === "ir")
    renderedBlocks(root).forEach((element) => observer.observe(element));
  const events = [
    "wheel",
    "pointerdown",
    "keydown",
    "beforeinput",
    "touchstart",
  ];
  const cancel = () => {
    cancelled = true;
    cancelAnimationFrame(frame);
    observer.disconnect();
    clearTimeout(timer);
    events.forEach((event) =>
      document.removeEventListener(event, cancel, true),
    );
    root.removeEventListener("load", schedule, true);
  };
  const timer = setTimeout(cancel, 700);
  events.forEach((event) =>
    document.addEventListener(event, cancel, { capture: true, passive: true }),
  );
  root.addEventListener("load", schedule, true);
  apply();
  schedule();
  return cancel;
}
