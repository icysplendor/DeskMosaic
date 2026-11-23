import MarkdownIt from "markdown-it";

const markdown = new MarkdownIt({ html: true });

export function normalizeTableSeparators(source) {
  if (!/\n\n\n[\t ]*\|/.test(source)) return source;
  const lines = source.split("\n"),
    padding = new Set();
  for (const token of markdown.parse(source, {})) {
    if (token.type !== "table_open" || token.level !== 0) continue;
    const start = token.map[0];
    // Lute adds formatting-only padding before tables. User-authored empty
    // paragraphs are encoded as <br /> before serialization, and stay intact.
    if (lines[start - 1] === "" && lines[start - 2] === "")
      padding.add(start - 1);
  }
  return lines.filter((_, index) => !padding.has(index)).join("\n");
}

// Ordinary Markdown collapses additional blank lines. Preserve only gaps
// between top-level blocks, never whitespace inside code, lists or HTML.
export function preserveBlankLines(source) {
  if (!/\n[\t \r]*\n[\t \r]*\n|^[\t \r]*\n/.test(source)) return source;
  const lines = source.split(/\r?\n/);
  if (lines.at(-1) === "") lines.pop();
  const blocks = markdown
    .parse(source, {})
    .filter((x) => x.level === 0 && x.map);
  // Markdown HTML blocks can end at a blank line while the HTML element is
  // still open. Leave such source untouched rather than insert into raw HTML.
  if (
    blocks.some(
      (x) =>
        x.type === "html_block" && !/^\s*<br\s*\/?\s*>\s*$/i.test(x.content),
    )
  )
    return source;
  const additions = new Set();
  let end = 0;
  for (const block of blocks) {
    const [start, next] = block.map;
    // Reference definitions have no rendered block token. Do not interpret
    // their separator lines as additional paragraph spacing.
    if (lines.slice(end, start).every((line) => /^\s*$/.test(line))) {
      for (let i = end + (end ? 1 : 0); i < start; i++) additions.add(i);
    }
    end = Math.max(end, next);
  }
  if (lines.slice(end).every((line) => /^\s*$/.test(line))) {
    for (let i = end + 1; i < lines.length; i++) additions.add(i);
  }
  if (!additions.size) return source;
  return (
    lines.map((line, i) => (additions.has(i) ? "<br />\n" : line)).join("\n") +
    "\n"
  );
}

export function installBlankParagraphs(lute) {
  const renderBlank = lute.Md2VditorIRDOM.bind(lute);
  const blank = document.createElement("template");
  blank.innerHTML = renderBlank("<br />\n");
  const originalSerialize = lute.VditorIRDOM2Md.bind(lute);
  const serialize = (html) => normalizeTableSeparators(originalSerialize(html));
  lute.VditorIRDOM2Md = (html) => {
    if (!/<p\b[^>]*>(?:[\s\u200b]|<br\s*\/?>|<wbr\s*\/?>)*<\/p>/i.test(html))
      return serialize(html);
    const template = document.createElement("template");
    template.innerHTML = html;
    const children = [...template.content.children];
    for (const [index, paragraph] of children.entries()) {
      if (
        paragraph.tagName !== "P" ||
        paragraph.textContent.replace(/[\s\u200b]/g, "") ||
        paragraph.querySelector(":not(br):not(wbr)")
      )
        continue;
      // An empty note and the editor's automatic landing pad after a block
      // are not user-authored spacing. Preserve actual paragraph gaps.
      if (
        index === children.length - 1 &&
        (!children[index - 1] || children[index - 1].tagName !== "P")
      )
        continue;
      paragraph.replaceWith(blank.content.firstElementChild.cloneNode(true));
    }
    return serialize(template.innerHTML);
  };
  for (const name of [
    "Md2VditorIRDOM",
    "SpinVditorIRDOM",
    "HTML2VditorIRDOM",
  ]) {
    const render = lute[name].bind(lute);
    lute[name] = (...args) => {
      if (name === "Md2VditorIRDOM") args[0] = preserveBlankLines(args[0]);
      const html = render(...args);
      if (!html.includes('data-type="html-block"')) return html;
      const template = document.createElement("template");
      template.innerHTML = html;
      for (const block of [...template.content.children]) {
        if (
          block.dataset.type !== "html-block" ||
          block.tagName !== "DIV" ||
          !/^\s*<br\s*\/?\s*>\s*$/i.test(
            block.querySelector('code[data-type="html-block"]')?.textContent ||
              "",
          )
        )
          continue;
        const paragraph = document.createElement("p");
        paragraph.dataset.block = "0";
        block.querySelectorAll("wbr").forEach((x) => paragraph.append(x));
        block.replaceWith(paragraph);
      }
      return template.innerHTML;
    };
  }
}
