// Real pointer/keyboard checks for both engines. Requires the isolated preview
// server on 4173 and a separately installed Playwright (no production dependency).
// node tests/code-activation-browser-checks.mjs /tmp/tools/node_modules/playwright/index.mjs webkit
import assert from "node:assert/strict";
import { mkdir, writeFile, readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
const { [process.argv[3] || "webkit"]: engine } = await import(
  process.argv[2] ? pathToFileURL(process.argv[2]).href : "playwright"
);
const root = "/tmp/notebook-ui-test-library";
const folder = `${root}/books/activation-check/code-activation`;
await mkdir(`${folder}/reference`, { recursive: true });
await writeFile(
  `${root}/books/activation-check/book.json`,
  JSON.stringify({ id: "activation-check", title: "点击回归", created: 0 }),
);
await writeFile(
  `${folder}/note.json`,
  JSON.stringify({
    id: "code-activation",
    title: "代码激活回归",
    created: 0,
    updated: Date.now(),
  }),
);
await writeFile(`${folder}/main.md`, "正文前\n\n```txt\nabc\n```\n\n正文后\n");
const browser = await engine.launch({
  headless: true,
  ...(process.argv[3] === "chromium"
    ? {
        executablePath: process.env.NOTEBOOK_CHROME || "/usr/bin/google-chrome",
        args: ["--no-sandbox"],
      }
    : {}),
});
try {
  const page = await browser.newPage({
    viewport: { width: 1280, height: 822 },
  });
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.addInitScript(() => {
    window.bridgeWrites = [];
    window.activationNativeReady = false;
    window.chrome = {
      webview: {
        postMessage: async (message) => {
          if (message.op === "ready") window.activationNativeReady = true;
          try {
            if (
              message.op === "write" &&
              message.args.path.endsWith("/main.md")
            )
              window.bridgeWrites.push(message.args.path);
            const response = await fetch("/_bridge", {
              method: "POST",
              body: JSON.stringify(message),
            });
            const value = await response.json();
            window.nativeReply(message.id, value.result, value.error);
          } catch (error) {
            window.nativeReply(message.id, null, error.message);
          }
        },
      },
    };
  });
  await page.goto("http://127.0.0.1:4173/index.html");
  await page.waitForFunction(() => window.activationNativeReady);
  // Simulate WKWebView assigning initial focus without a user pressing Tab.
  // :focus-visible alone also matches this state in a fresh WebKit window.
  for (const panel of ["books", "notes"]) {
    const divider = page.locator(`#${panel} > .column-resize`);
    await divider.focus();
    const handle = await divider.evaluate((d) => ({
      background: getComputedStyle(d, "::after").backgroundColor,
      outline: getComputedStyle(d, "::after").outlineStyle,
    }));
    assert.equal(handle.background, "rgba(0, 0, 0, 0)");
    assert.equal(handle.outline, "none", "native initial focus painted a blue marker");
  }
  await page.locator('[data-filter="all"]').click();
  await page.locator(".note-row").filter({ hasText: "代码激活回归" }).click();
  await page.waitForFunction(
    () =>
      !document.getElementById("editor").inert &&
      document
        .getElementById("breadcrumb")
        .textContent.includes("代码激活回归"),
  );
  await page.locator(".vditor-ir > pre[contenteditable]").waitFor();
  const setSource = async (text) => {
    await page.locator("#source-mode").click();
    await page.locator(".vditor-sv").fill(text);
    await page.waitForTimeout(180);
    await page.evaluate(() => window.notebookFlush());
    await page.locator("#preview-mode").click();
    await page.waitForTimeout(150);
  };
  const markdown = async () => {
    await page.locator("#source-mode").click();
    const value = await page.locator(".vditor-sv").inputValue();
    await page.locator("#preview-mode").click();
    return value;
  };
  const metrics = (block) =>
    block.evaluate((b) => {
      const rect = b.getBoundingClientRect();
      return [
        rect.top,
        rect.height,
        b.querySelector(".vditor-copy").getBoundingClientRect().height,
      ];
    });
  // User reproduction: type 111, Enter once, then click the code toolbar.
  // Additional deliberate Enter presses must still retain their spacing.
  for (const behavior of ["paragraph", "line"]) {
    await page.locator('.vditor-toolbar [data-type="display"]').click();
    await page
      .locator('#display-dialog select[name="enterBehavior"]')
      .selectOption(behavior);
    await page.locator('#display-dialog button[value="save"]').click();
    for (const presses of [1, 3]) {
      await setSource("111\n");
      await page
        .locator(".vditor-ir > pre > p")
        .first()
        .evaluate((p) => {
          p.closest("[contenteditable=true]").focus();
          const range = document.createRange();
          range.selectNodeContents(p);
          range.collapse(false);
          getSelection().removeAllRanges();
          getSelection().addRange(range);
        });
      for (let i = 0; i < presses; i++) await page.keyboard.press("Enter");
      await page.evaluate(() => window.notebookFlush());
      const beforeInsertion = await readFile(`${folder}/main.md`, "utf8");
      await page.locator('.vditor-toolbar [data-type="code"]').click();
      await page.waitForTimeout(200);
      const spacing = await page
        .locator(".vditor-ir > pre")
        .evaluate((root) => {
          const code = root.querySelector('[data-type="code-block"]');
          const text = [...root.children].find(
            (p) => p.tagName === "P" && p.textContent.includes("111"),
          );
          return {
            gap:
              code.getBoundingClientRect().top -
              text.getBoundingClientRect().bottom,
            height: text.getBoundingClientRect().height,
            html: root.innerHTML,
          };
        });
      if (presses === 1)
        assert(
          spacing.gap <= 5 && spacing.height <= 25,
          `extra gap after one Enter (${behavior}): ${JSON.stringify(spacing)}`,
        );
      const value = await markdown();
      assert.equal(
        (value.match(/<br \/>/g) || []).length,
        presses - 1,
        `authored spacing changed (${behavior}, ${presses}): ${value}`,
      );
      if (presses === 1) assert.equal(value, "111\n\n```\n\n```\n");
      await page.locator('.vditor-toolbar [data-type="undo"]').click();
      await page.waitForTimeout(150);
      assert.equal(
        await markdown(),
        beforeInsertion,
        "Undo must restore the line before inserting code",
      );
      await page.locator('.vditor-toolbar [data-type="redo"]').click();
      await page.waitForTimeout(150);
      assert.equal(
        await markdown(),
        value,
        "Redo must not reintroduce synthetic spacing",
      );
      await page.evaluate(() => window.notebookFlush());
      assert.equal(await readFile(`${folder}/main.md`, "utf8"), value);
    }
  }
  for (const body of [
    "```txt\nabc\n```\n",
    "```\na\n\n\nb\n```\n",
    `\`\`\`txt\n${"long_token_".repeat(80)}\n\`\`\`\n`,
    "```txt\n\n```\n",
    "```\nfirst\n```\n\n```python\nprint('second')\n```\n",
  ]) {
    await setSource(`正文前\n\n${body}\n正文后\n`);
    const baseline = await markdown();
    await page.evaluate(() => {
      window.activationInputs = 0;
      document
        .querySelector(".vditor-ir > pre")
        .addEventListener("input", () => window.activationInputs++);
    });
    const blocks = page.locator('.vditor-ir__node[data-type="code-block"]');
    for (let i = 0; i < (await blocks.count()); i++) {
      const block = blocks.nth(i);
      const source = block.locator(".vditor-ir__marker--pre > code");
      const bytes = await source.textContent();
      for (let repeat = 0; repeat < 3; repeat++) {
        await page
          .locator(".vditor-ir p")
          .filter({ hasText: "正文前" })
          .click();
        const before = await metrics(block);
        await block
          .locator(".vditor-ir__preview > code")
          .click({ position: { x: 30, y: 15 } });
        await page.waitForTimeout(80);
        assert.equal(
          await source.textContent(),
          bytes,
          "activation inserted code whitespace",
        );
        const after = await metrics(block);
        before.forEach((value, j) =>
          assert(
            Math.abs(after[j] - value) < 1,
            `code geometry drift: ${before} -> ${after}`,
          ),
        );
        assert.equal(
          await page
            .locator('[data-type="code-block"].vditor-ir__node--expand')
            .count(),
          1,
        );
      }
    }
    assert.equal(
      await page.evaluate(() => window.activationInputs),
      0,
      "click-only navigation emitted an edit",
    );
    assert.equal(
      await markdown(),
      baseline,
      "click-only navigation changed Markdown",
    );
    await page.evaluate(() => window.notebookFlush());
    assert.equal(
      await readFile(`${folder}/main.md`, "utf8"),
      baseline,
      JSON.stringify(
        await page.evaluate(() => ({
          status: document.getElementById("save-status").textContent,
          crumb: document.getElementById("breadcrumb").textContent,
          writes: window.bridgeWrites,
        })),
      ),
    );
  }
  // A source made genuinely empty while editing must not gain a synthetic LF.
  await setSource("正文前\n\n```txt\n\n```\n\n正文后\n");
  const block = page.locator('[data-type="code-block"]');
  await block
    .locator(".vditor-ir__marker--pre > code")
    .evaluate((s) => s.replaceChildren());
  await block
    .locator(".vditor-ir__preview > code")
    .click({ position: { x: 20, y: 10 } });
  assert.equal(
    await block.locator(".vditor-ir__marker--pre > code").textContent(),
    "",
  );
  await page.keyboard.insertText("typed empty");
  await page.waitForTimeout(200);
  assert((await markdown()).includes("typed empty"));
  // Split text/caret markers must still map a click to the real source text.
  await setSource("正文前\n\n```txt\nabc def\n\n\nlast\n```\n\n正文后\n");
  await block.locator(".vditor-ir__marker--pre > code").evaluate((s) => {
    const text = s.textContent;
    s.replaceChildren(
      document.createTextNode(text.slice(0, 1)),
      document.createElement("wbr"),
      document.createTextNode(text.slice(1)),
    );
  });
  await block
    .locator(".vditor-ir__preview > code")
    .click({ position: { x: 45, y: 15 } });
  await page.keyboard.insertText("X");
  await page.waitForTimeout(200);
  const edited = await markdown();
  assert(edited.includes("X"));
  assert(edited.includes("\n\n\nlast"), "intentional code blank lines lost");
  assert.equal(
    edited.replace("X", ""),
    "正文前\n\n```txt\nabc def\n\n\nlast\n```\n\n正文后\n",
  );
  for (const theme of ["light", "dark"]) {
    if (
      (await page.evaluate(() => document.documentElement.dataset.theme)) !==
      theme
    )
      await page.locator("#theme").click();
    const divider = page.locator("#books > .column-resize");
    await divider.hover({ position: { x: 1, y: 40 } });
    await page.locator("#search").focus();
    await page.keyboard.press("Tab");
    await divider.focus();
    await page.waitForFunction(
      () =>
        document.activeElement ===
        document.querySelector("#books > .column-resize"),
    );
    const style = await divider.evaluate((d) => {
      const s = getComputedStyle(d),
        handle = getComputedStyle(d, "::after");
      return {
        background: s.backgroundColor,
        outline: s.outlineStyle,
        height: parseFloat(handle.height),
        focus: d.hasAttribute("data-keyboard-focus"),
        markerOutline: handle.outlineStyle,
      };
    });
    assert.equal(style.background, "rgba(0, 0, 0, 0)");
    assert.equal(style.outline, "none");
    assert(style.height <= 32);
    assert.equal(style.focus, true, "Tab navigation lost its keyboard focus marker");
    assert.equal(style.markerOutline, "solid");
    const before = await page
      .locator("#books")
      .evaluate((p) => p.getBoundingClientRect().width);
    await page.keyboard.press("ArrowRight");
    await page.waitForFunction(
      (width) =>
        document.getElementById("books").getBoundingClientRect().width ===
        width,
      before + 10,
    );
    assert.equal(
      await page
        .locator("#books")
        .evaluate((p) => p.getBoundingClientRect().width),
      before + 10,
    );
    const rect = await divider.boundingBox();
    await page.mouse.move(rect.x + 1, rect.y + 40);
    await page.mouse.down();
    await page.mouse.move(rect.x + 21, rect.y + 40);
    await page.mouse.up();
    assert.equal(
      await divider.evaluate((d) => getComputedStyle(d, "::after").outlineStyle),
      "none",
      "pointer resizing retained the keyboard focus ring",
    );
    assert.equal(
      await page.evaluate(() => getSelection().toString()),
      "",
      "resizing selected unrelated sidebar text",
    );
    await page.waitForFunction(
      (width) =>
        document.getElementById("books").getBoundingClientRect().width ===
        width,
      before + 30,
    );
    // Returning from another native window must not restore a stale blue ring.
    await page.keyboard.press("ArrowLeft");
    assert.equal(
      await divider.evaluate((d) => getComputedStyle(d, "::after").outlineStyle),
      "solid",
    );
    await page.evaluate(() => window.dispatchEvent(new Event("blur")));
    await page.mouse.move(600, 60);
    const restored = await divider.evaluate((d) => {
      d.blur();
      d.focus();
      return {
        background: getComputedStyle(d, "::after").backgroundColor,
        outline: getComputedStyle(d, "::after").outlineStyle,
      };
    });
    assert.equal(restored.background, "rgba(0, 0, 0, 0)");
    assert.equal(restored.outline, "none");
    assert.equal(
      await page
        .locator("#books")
        .evaluate((p) => p.getBoundingClientRect().width),
      before + 20,
    );
    await page.screenshot({
      path: `/tmp/notebook-activation-${process.argv[3] || "webkit"}-${theme}.png`,
    });
  }
  assert.deepEqual(errors, []);
  console.log(
    `PASS ${process.argv[3] || "webkit"}: one/three Enter then code insertion in both key modes, Undo/Redo and saves, repeated activation, empty/split source editing, exact Markdown, stable geometry and separator keyboard/mouse resizing in both themes`,
  );
} finally {
  await browser.close();
}
