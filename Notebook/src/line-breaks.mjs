// Use portable inline <br /> markers for explicit same-paragraph line breaks.
// Lute otherwise interprets two consecutive soft newlines as new paragraphs.
function paragraphHasNewline(html) {
  // Newlines between blocks and inside fenced code need no transformation.
  // Inspect strings first to avoid building another full DOM for long notes.
  for (const match of html.matchAll(/<p\b[^>]*>[\s\S]*?<\/p>/gi))
    if (match[0].includes("\n")) return true;
  return false;
}
export function installLineBreaks(lute) {
  const template = (html) => {
    const node = document.createElement("template");
    node.innerHTML = html;
    return node;
  };
  const sample = template(lute.Md2VditorIRDOM("A<br />B\n"));
  const marker = sample.content.querySelector('[data-type="html-inline"]');
  const encode = (html) => {
    if (!/<br\b/i.test(html) && !paragraphHasNewline(html)) return html;
    const node = template(html);
    for (const p of node.content.querySelectorAll("p")) {
      if (
        p.closest(
          "li,td,th,blockquote,[data-type=code-block],[data-type=html-block]",
        )
      )
        continue;
      const walk = document.createTreeWalker(p, NodeFilter.SHOW_TEXT);
      const texts = [];
      while (walk.nextNode()) texts.push(walk.currentNode);
      for (const text of texts) {
        if (
          !text.data.includes("\n") ||
          text.parentElement.closest(
            'code,[data-type="html-inline"],.vditor-ir__marker',
          )
        )
          continue;
        const parts = text.data.split("\n");
        const fragment = document.createDocumentFragment();
        parts.forEach((part, index) => {
          if (index) fragment.append(marker.cloneNode(true));
          fragment.append(document.createTextNode(part));
        });
        text.replaceWith(fragment);
      }
      for (const br of p.querySelectorAll("br")) {
        if (!br.closest('[data-type="html-inline"]'))
          br.replaceWith(marker.cloneNode(true));
      }
    }
    return node.innerHTML;
  };
  const decode = (html) => {
    if (
      !html.includes('data-type="html-inline"') &&
      !html.includes('data-type="html-block"') &&
      !paragraphHasNewline(html)
    )
      return html;
    const node = template(html);
    for (const block of [...node.content.children]) {
      if (block.dataset.type !== "html-block") continue;
      const value =
        block.querySelector('code[data-type="html-block"]')?.textContent || "";
      if (!/^(?:\s*<br\s*\/?\s*>\s*){2,}$/i.test(value)) continue;
      const p = document.createElement("p");
      p.dataset.block = "0";
      for (const match of value.matchAll(/<br\s*\/?\s*>/gi))
        p.append(document.createElement("br"));
      p.append(...block.querySelectorAll("wbr"));
      block.replaceWith(p);
    }
    for (const span of node.content.querySelectorAll(
      '[data-type="html-inline"]',
    )) {
      if (
        !span.closest("p") ||
        span.closest(
          "li,td,th,blockquote,[data-type=code-block],[data-type=html-block]",
        )
      )
        continue;
      if (
        !/^\s*<br\s*\/?\s*>\s*$/i.test(
          span.querySelector("code")?.textContent || "",
        )
      )
        continue;
      span.replaceWith(
        document.createElement("br"),
        ...span.querySelectorAll("wbr"),
      );
    }
    // Markdown soft newlines also need actual DOM line breaks at the caret.
    for (const p of node.content.querySelectorAll("p")) {
      if (
        p.closest(
          "li,td,th,blockquote,[data-type=code-block],[data-type=html-block]",
        )
      )
        continue;
      const walk = document.createTreeWalker(p, NodeFilter.SHOW_TEXT);
      const texts = [];
      while (walk.nextNode()) texts.push(walk.currentNode);
      for (const text of texts) {
        if (
          !text.data.includes("\n") ||
          text.parentElement.closest("code,.vditor-ir__marker")
        )
          continue;
        const parts = text.data.split("\n");
        const fragment = document.createDocumentFragment();
        parts.forEach((part, index) => {
          if (index) fragment.append(document.createElement("br"));
          fragment.append(document.createTextNode(part));
        });
        text.replaceWith(fragment);
      }
    }
    return node.innerHTML;
  };
  const serialize = lute.VditorIRDOM2Md.bind(lute);
  lute.VditorIRDOM2Md = (html) => serialize(encode(html));
  for (const name of [
    "Md2VditorIRDOM",
    "SpinVditorIRDOM",
    "HTML2VditorIRDOM",
  ]) {
    const render = lute[name].bind(lute);
    lute[name] = (...args) => {
      if (name === "SpinVditorIRDOM") args[0] = encode(args[0]);
      return decode(render(...args));
    };
  }
}
