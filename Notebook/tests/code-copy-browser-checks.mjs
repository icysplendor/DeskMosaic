// Run with the isolated preview server on 4173:
// node tests/code-copy-browser-checks.mjs /path/to/playwright/index.mjs chromium
// Repeat with webkit to cover the macOS editor engine.
import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
const engineName = process.argv[3] || "webkit";
const { [engineName]: engine } = await import(
  pathToFileURL(process.argv[2]).href
);
const book = "/tmp/notebook-ui-test-library/books/copy-check";
const folder = `${book}/code-copy`;
await mkdir(folder, { recursive: true });
await writeFile(
  `${book}/book.json`,
  JSON.stringify({ id: "copy-check", title: "复制回归", created: 0 }),
);
await writeFile(
  `${folder}/note.json`,
  JSON.stringify({
    id: "code-copy",
    title: "代码复制回归",
    created: 0,
    updated: Date.now(),
  }),
);
await writeFile(`${folder}/main.md`, "```txt\ninitial\n```\n");
const browser = await engine.launch({
  headless: true,
  ...(engineName === "chromium"
    ? {
        executablePath: process.env.NOTEBOOK_CHROME || "/usr/bin/google-chrome",
        args: ["--no-sandbox"],
      }
    : {}),
});
try {
  const page = await browser.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.addInitScript(() => {
    window.copied = null;
    window.chrome = {
      webview: {
        postMessage: async (message) => {
          try {
            if (message.op === "editCode") {
              window.pendingCodeEditor = message.args;
              window.finishCodeEdit = (text) => {
                window.pendingCodeEditor = null;
                window.nativeReply(message.id, text, null);
              };
              return;
            }
            const response = await fetch("/_bridge", {
              method: "POST",
              body: JSON.stringify(message),
            });
            const value = await response.json();
            if (message.op === "clipboard") window.copied = message.args.text;
            window.nativeReply(message.id, value.result, value.error);
            if (message.op === "ready") window.copyFixtureReady = true;
          } catch (error) {
            window.nativeReply(message.id, null, error.message);
          }
        },
      },
    };
  });
  await page.goto("http://127.0.0.1:4173/index.html");
  await page.waitForFunction(() => window.copyFixtureReady);
  await page.locator('[data-filter="all"]').click();
  await page.locator(".note-row").filter({ hasText: "代码复制回归" }).click();
  await page.waitForFunction(
    () =>
      !document.getElementById("editor").inert &&
      document
        .getElementById("breadcrumb")
        .textContent.includes("代码复制回归"),
  );
  for (const text of [
    "echo hello",
    "  first\n\tsecond\n\nlast  ",
    "last\n",
    "last\n\n",
    "",
  ]) {
    await page.locator("#source-mode").click();
    await page
      .locator(".vditor-sv")
      .fill(`正文前\n\n\`\`\`txt\n${text}\n\`\`\`\n\n正文后\n`);
    await page.evaluate(() => window.notebookFlush());
    await page.locator("#preview-mode").click();
    const block = page.locator('.vditor-ir__node[data-type="code-block"]');
    for (const editing of [false, true]) {
      if (editing) {
        await block.locator(".vditor-ir__preview > code").click();
        await block
          .locator(".vditor-ir__marker--pre > code")
          .evaluate((source) => {
            const range = document.createRange();
            range.selectNodeContents(source);
            range.collapse(true);
            getSelection().removeAllRanges();
            getSelection().addRange(range);
          });
      }
      const before = await page.evaluate(() => ({
        node: getSelection().anchorNode?.textContent,
        offset: getSelection().anchorOffset,
      }));
      await page.evaluate(() => {
        window.copied = null;
      });
      await block.locator(".vditor-copy button:not(.code-popout)").click();
      await page.waitForFunction(() => window.copied !== null);
      assert.equal(
        await page.evaluate(() => window.copied),
        text,
        `${engineName}: ${JSON.stringify(text)}, editing=${editing}`,
      );
      if (editing)
        assert.deepEqual(
          await page.evaluate(() => ({
            node: getSelection().anchorNode?.textContent,
            offset: getSelection().anchorOffset,
          })),
          before,
          "copy moved caret",
        );
    }
  }
  const setSource = async (value) => {
    await page.locator("#source-mode").click();
    await page.locator(".vditor-sv").fill(value);
    await page.evaluate(() => window.notebookFlush());
    await page.locator("#preview-mode").click();
  };
  const source = page.locator(".vditor-ir__marker--pre > code");
  const preview = page.locator(".vditor-ir__preview > code");
  // Select directly in the rendered code with a real drag, without first
  // clicking to expand the source. Mouseup/click must retain this selection.
  await setSource("正文前\n\n```txt\noriginal\n```\n\n正文后\n");
  const drag = await preview.evaluate((code) => {
    const walker = document.createTreeWalker(code, NodeFilter.SHOW_TEXT);
    let text;
    while (walker.nextNode()) {
      if (walker.currentNode.textContent.includes("original")) {
        text = walker.currentNode;
        break;
      }
    }
    const range = document.createRange();
    range.setStart(text, 2);
    range.setEnd(text, 5);
    const rect = range.getBoundingClientRect();
    return {
      left: rect.left + 0.1,
      right: rect.right + 0.1,
      y: rect.top + rect.height / 2,
    };
  });
  await page.mouse.move(drag.left, drag.y);
  await page.mouse.down();
  await page.mouse.move(drag.right, drag.y, { steps: 10 });
  await page.mouse.up();
  assert.equal(
    await page.evaluate(() => getSelection().toString()),
    "igi",
    "activating code collapsed the dragged selection",
  );
  await page.keyboard.press("Backspace");
  assert.equal(
    await source.textContent(),
    "ornal\n",
    "dragged code selection was not deleted",
  );
  assert.equal(
    await page.locator('[data-type="code-block"].vditor-ir__node').count(),
    1,
  );
  // A selection may still refer to highlighted, non-editable preview spans.
  // Native paste can target BODY while that selection is visible.
  await setSource("正文前\n\n```txt\noriginal\n```\n\n正文后\n");
  await preview.evaluate((code) => {
    document.activeElement.blur();
    code.innerHTML = "<span>or</span><span>igi</span>nal\n";
    const range = document.createRange();
    range.selectNodeContents(code.children[1]);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    const data = new DataTransfer();
    data.setData("text/plain", '  echo "<tag>"\n\tline\n');
    data.setData("text/html", "<h1>wrong rich content</h1>");
    document.body.dispatchEvent(
      new ClipboardEvent("paste", {
        bubbles: true,
        cancelable: true,
        clipboardData: data,
      }),
    );
  });
  assert.equal(await source.textContent(), 'or  echo "<tag>"\n\tline\nnal\n');
  await page.locator("#source-mode").click();
  assert.equal(
    await page.locator(".vditor-sv").inputValue(),
    '正文前\n\n```txt\nor  echo "<tag>"\n\tline\nnal\n```\n\n正文后\n',
    "paste removed the code fence or surrounding prose",
  );
  await page.locator("#preview-mode").click();
  assert.equal(
    await page.locator('[data-type="code-block"].vditor-ir__node').count(),
    1,
  );
  await setSource("正文前\n\n```txt\noriginal\n```\n\n正文后\n");
  await preview.click();
  await source.evaluate((code) => {
    const range = document.createRange();
    range.setStart(code.parentNode, 0);
    range.setEnd(code.firstChild, 3);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  });
  await page.keyboard.press("Backspace");
  assert.equal(
    await source.textContent(),
    "ginal\n",
    "PRE boundary selection was not deleted",
  );
  await setSource("正文前\n\n```bash\noriginal\n```\n\n正文后\n");
  await preview.click();
  // Match the Cmd+A event forwarded by the macOS accessory window.
  await page.evaluate(() =>
    document.activeElement.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "a",
        metaKey: true,
        bubbles: true,
        cancelable: true,
      }),
    ),
  );
  assert.equal(
    await page.evaluate(() => getSelection().toString().replace(/\n$/, "")),
    "original",
  );
  const terminal = "  root@host:~# echo '<tag>&中文'\n\tsecond\n\nlast  ";
  // A terminal can offer both a colored HTML fragment and plain text. WebKit
  // sends paste to the outer PRE, not the CODE under the selection.
  await page.evaluate((text) => {
    const data = new DataTransfer();
    data.setData("text/plain", text.replace(/\n/g, "\r\n"));
    data.setData(
      "text/html",
      '<div style="color:red"><b>WRONG HTML</b><br><img src="bad"></div>',
    );
    document.querySelector(".vditor-ir > pre").dispatchEvent(
      new ClipboardEvent("paste", {
        bubbles: true,
        cancelable: true,
        clipboardData: data,
      }),
    );
  }, terminal);
  assert.equal(await source.textContent(), terminal + "\n");
  assert.equal(
    await source.locator("*").count(),
    0,
    "rich markup entered code",
  );
  await page.evaluate(() => window.notebookFlush());
  await page.keyboard.press("ControlOrMeta+a");
  await page.keyboard.press("Backspace");
  assert.equal(
    await source.textContent(),
    "\n",
    "selected code was not deleted",
  );
  assert.equal(
    await page.locator('[data-type="code-block"].vditor-ir__node').count(),
    1,
    "deletion removed the code block",
  );
  await page.locator('.vditor-toolbar [data-type="undo"]').click();
  assert.equal(
    await source.textContent(),
    terminal + "\n",
    "undo did not restore pasted code",
  );
  await preview.click();
  await page.keyboard.press("ControlOrMeta+a");
  await page.keyboard.press("Delete");
  assert.equal(await source.textContent(), "\n");
  await setSource("正文前\n\n```bash\noriginal\n```\n\n正文后\n");
  await preview.click();
  await source.evaluate((s) => {
    const range = document.createRange();
    range.setStart(s.firstChild, 2);
    range.setEnd(s.firstChild, 5);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  });
  await page.keyboard.press("Backspace");
  assert.equal(
    await source.textContent(),
    "ornal\n",
    "partial selection deletion changed adjacent text",
  );
  await page.keyboard.insertText("X");
  await page.keyboard.press("Backspace");
  assert.equal(
    await source.textContent(),
    "ornal\n",
    "ordinary character deletion failed",
  );
  await source.evaluate((s) => {
    const range = document.createRange();
    range.setStart(s.firstChild, 0);
    range.setEnd(s.firstChild, 2);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    s.closest("[contenteditable=true]").dispatchEvent(
      new InputEvent("beforeinput", {
        bubbles: true,
        cancelable: true,
        inputType: "deleteContentBackward",
      }),
    );
  });
  assert.equal(
    await source.textContent(),
    "nal\n",
    "beforeinput-only deletion failed",
  );
  await setSource("正文前\n\n```bash\noriginal\n```\n\n正文后\n");
  const popout = page.locator(".code-popout");
  assert.equal(
    await popout.evaluate((b) =>
      b.nextElementSibling.getAttribute("aria-label"),
    ),
    "复制原始代码",
  );
  await popout.click();
  await page.waitForFunction(() => window.pendingCodeEditor);
  assert.equal(
    await page.evaluate(() => window.pendingCodeEditor.text),
    "original",
  );
  assert.equal(
    await page.evaluate(() => window.pendingCodeEditor.language),
    "bash",
  );
  assert.equal(await page.evaluate(() => document.body.inert), true);
  const draft = "  changed\n\tsecond\n\n```\nliteral fence\n```\n\n";
  await page.evaluate(
    (text) => window.finishCodeEdit({ text, language: "python" }),
    draft,
  );
  await page.waitForFunction(() => !document.body.inert);
  await page.evaluate(() => window.notebookFlush());
  assert.equal(await source.textContent(), draft + "\n");
  await page.locator("#source-mode").click();
  assert.equal(
    await page.locator(".vditor-sv").inputValue(),
    `正文前\n\n\`\`\`\`python\n${draft}\n\`\`\`\`\n\n正文后\n`,
  );
  await page.locator("#preview-mode").click();
  await page.locator('.vditor-toolbar [data-type="undo"]').click();
  assert.equal(await source.textContent(), "original\n");
  assert.equal(
    await page.locator(".code-language").inputValue(),
    "bash",
    "undo did not restore code language",
  );
  await popout.click();
  await page.waitForFunction(() => window.pendingCodeEditor);
  await page.evaluate(() => window.finishCodeEdit(null));
  await page.waitForFunction(() => !document.body.inert);
  assert.equal(
    await source.textContent(),
    "original\n",
    "cancel changed the block",
  );
  const longText = Array.from(
    { length: 300 },
    (_, i) => `line ${i}: \t中文 <tag> ${"x".repeat(80)}`,
  ).join("\n");
  await setSource(`\`\`\`txt\n${longText}\n\`\`\`\n`);
  assert.equal(
    await preview.evaluate((code) => code.clientHeight),
    400,
    "long code viewport must be 400px",
  );
  await preview.click();
  assert.equal(
    await source.evaluate((code) => code.clientHeight),
    400,
    "editing height differs from preview",
  );
  await popout.click();
  await page.waitForFunction(() => window.pendingCodeEditor);
  assert.equal(
    await page.evaluate(() => window.pendingCodeEditor.text),
    longText,
    "long code was truncated",
  );
  await page.evaluate(() => window.finishCodeEdit(null));
  await page.waitForFunction(() => !document.body.inert);
  await page.evaluate(() => window.notebookFlush());
  assert.deepEqual(errors, []);
  console.log(
    `PASS ${engineName}: copy whitespace/caret; terminal HTML paste; select/delete/undo; pop-out save/cancel, literal fences and surrounding Markdown`,
  );
} finally {
  await browser.close();
}
