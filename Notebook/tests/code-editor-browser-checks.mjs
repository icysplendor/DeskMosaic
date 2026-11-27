// Isolated preview server on 4173; run with Playwright's index.mjs and engine.
import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
const name = process.argv[3] || "webkit";
const { [name]: engine } = await import(pathToFileURL(process.argv[2]).href);
const browser = await engine.launch({
  headless: true,
  ...(name === "chromium"
    ? { executablePath: "/usr/bin/google-chrome", args: ["--no-sandbox"] }
    : {}),
});
const original = 'function greet() {\n    return "中文🙂";\n}\n';
async function checkFooter(page) {
  const layout = await page.evaluate(() => {
    const footer = document.querySelector(".code-footer");
    const bounds = footer.getBoundingClientRect();
    const padding = getComputedStyle(footer);
    return {
      fitsViewport: bounds.bottom <= innerHeight && bounds.right <= innerWidth,
      buttons: ["cancel", "save"].map((id) => {
        const button = document.getElementById(id);
        const rect = button.getBoundingClientRect();
        return {
          id,
          fitsFooter:
            rect.top >= bounds.top + parseFloat(padding.paddingTop) &&
            rect.bottom <= bounds.bottom - parseFloat(padding.paddingBottom) &&
            rect.left >= bounds.left + parseFloat(padding.paddingLeft) &&
            rect.right <= bounds.right - parseFloat(padding.paddingRight),
          clickable:
            document.elementFromPoint(
              rect.left + rect.width / 2,
              rect.bottom - 1,
            ) === button,
        };
      }),
    };
  });
  assert(layout.fitsViewport, "footer extends outside the viewport");
  for (const button of layout.buttons) {
    assert(button.fitsFooter, `${button.id} extends into footer padding`);
    assert(button.clickable, `${button.id} is clipped or covered`);
  }
}
try {
  // A preloaded Windows page waits for the current block without a stale draft.
  {
    const page = await browser.newPage();
    await page.addInitScript(() => {
      window.messages = [];
      window.chrome = {
        webview: { postMessage: (m) => window.messages.push(m) },
      };
    });
    await page.goto("http://127.0.0.1:4173/code-editor.html");
    await page.waitForFunction(() =>
      window.messages.some((m) => m.op === "ready"),
    );
    assert.equal(await page.locator(".cm-editor").count(), 0);
    for (const dark of [false, true]) {
      // Each spare page is used once; a later initialize cannot replace edits.
      await page.evaluate(
        (dark) =>
          window.codeEditorInitialize({
            text: "fresh draft 中文🙂",
            language: "txt",
            dark,
            wrap: false,
          }),
        dark,
      );
      assert.equal(
        await page.locator(".cm-content").textContent(),
        "fresh draft 中文🙂",
      );
      assert.equal(
        await page.evaluate(() => document.documentElement.dataset.theme),
        dark ? "dark" : "light",
      );
      await page.locator(".cm-content").fill("unsaved draft");
      await page.evaluate(() =>
        window.codeEditorInitialize({ text: "stale draft" }),
      );
      assert.equal(
        await page.locator(".cm-content").textContent(),
        "unsaved draft",
      );
      await page.reload();
      await page.waitForFunction(() =>
        window.messages.some((m) => m.op === "ready"),
      );
    }
    await page.close();
  }
  for (const dark of [false, true]) {
    const page = await browser.newPage({
      viewport: { width: 1100, height: 760 },
    });
    const errors = [];
    page.on("pageerror", (e) => errors.push(e.message));
    await page.addInitScript(
      (args) => {
        window.messages = [];
        const receive = (m) => {
          window.messages.push(m);
          if (m.op === "ready") {
            window.codeEditorInitialize(args);
            window.editorReady = true;
          }
          if (m.op === "clipboard") window.nativeReply(m.id, null, null);
        };
        // Exercise the actual WKWebView and WebView2 message shapes.
        if (args.dark)
          window.webkit = {
            messageHandlers: { codeEditor: { postMessage: receive } },
          };
        else window.chrome = { webview: { postMessage: receive } };
      },
      { text: original, language: "javascript", dark, wrap: true },
    );
    await page.goto("http://127.0.0.1:4173/code-editor.html");
    await page.waitForFunction(() => window.editorReady);
    await checkFooter(page);
    const content = page.locator(".cm-content");
    assert.equal(await page.locator("#line-count").textContent(), "4 行");
    assert.equal(
      await page.locator("#char-count").textContent(),
      `${[...original].length} 字符`,
    );
    assert.equal(
      await page.locator("#selection-count").textContent(),
      "选中 0 字符",
    );
    assert.equal(
      await page.locator("#cursor-position").textContent(),
      "行 1，列 1",
    );
    assert.equal(
      await page
        .locator(".cm-editor")
        .evaluate((e) => getComputedStyle(e).backgroundColor),
      dark ? "rgb(38, 40, 50)" : "rgb(240, 241, 246)",
    );
    assert(
      (await page.locator(".cm-line span").count()) > 0,
      "syntax highlighting missing",
    );
    assert(
      await page.locator(".cm-lineNumbers").count(),
      "line number gutter missing",
    );
    await page.locator('.cm-foldGutter span[title="折叠代码"]').first().click();
    assert.equal(await page.locator(".cm-foldPlaceholder").count(), 1);
    await page.locator(".cm-foldPlaceholder").click();
    await page.locator(".cm-foldPlaceholder").waitFor({ state: "detached" });
    assert.equal(await page.locator(".cm-foldPlaceholder").count(), 0);
    await content.focus();
    await page.keyboard.press("ControlOrMeta+Home");
    for (let i = 0; i < 3; i++) await page.keyboard.press("Shift+ArrowRight");
    assert.equal(
      await page.locator("#selection-count").textContent(),
      "选中 3 字符",
    );
    await page
      .locator(".cm-selectionBackground")
      .first()
      .waitFor({ state: "visible" });
    assert.equal(
      await page
        .locator(".cm-selectionBackground")
        .first()
        .evaluate((e) => getComputedStyle(e).backgroundColor),
      dark ? "rgb(57, 62, 80)" : "rgb(225, 229, 238)",
    );
    await page.keyboard.press("Backspace");
    assert.equal(
      await page.locator("#char-count").textContent(),
      `${[...original].length - 3} 字符`,
    );
    await page.keyboard.press("ControlOrMeta+z");
    assert.equal(
      await page.locator("#char-count").textContent(),
      `${[...original].length} 字符`,
    );
    assert.equal(
      await page.locator("#edit-state").getAttribute("data-dirty"),
      "false",
    );
    await page.locator("#find").click();
    await page.locator('.cm-search input[name="search"]').fill("中文");
    assert((await page.locator(".cm-searchMatch").count()) > 0);
    await page.keyboard.press("Escape");
    await page.locator("#go-line").click();
    const go = page.locator(".cm-goto-line input");
    await go.fill("2");
    await go.press("Enter");
    assert(
      (await page.locator("#cursor-position").textContent()).startsWith(
        "行 2，",
      ),
    );
    await page.locator("#font-size").selectOption("18");
    assert.equal(
      await page
        .locator(".cm-scroller")
        .evaluate((e) => getComputedStyle(e).fontSize),
      "18px",
    );
    await page.locator("#wrap").uncheck();
    assert.equal(await page.locator(".cm-lineWrapping").count(), 0);
    await page.locator("#wrap").check();
    assert.equal(
      await page.locator("#edit-state").getAttribute("data-dirty"),
      "false",
      "display settings changed the document",
    );
    await page.locator("#copy-all").click();
    assert.equal(
      await page.evaluate(
        () => window.messages.find((m) => m.op === "clipboard").args.text,
      ),
      original,
    );
    await page.locator("#language").selectOption("python");
    assert.equal(
      await page.locator("#edit-state").getAttribute("data-dirty"),
      "true",
    );
    await page.evaluate(() => window.codeEditorRequestClose());
    assert(await page.locator("#discard-dialog").evaluate((d) => d.open));
    await page.locator("#keep-editing").click();
    await page.keyboard.press("ControlOrMeta+s");
    assert.equal(
      await page.evaluate(
        () => window.messages.filter((m) => m.op === "save").length,
      ),
      1,
      "save shortcut submitted twice",
    );
    assert.deepEqual(
      await page.evaluate(
        () => window.messages.find((m) => m.op === "save").args,
      ),
      { text: original, language: "python" },
    );
    const pasted = '  echo "<tag>"\n\t中文🙂\n\n';
    await content.focus();
    await page.keyboard.press("ControlOrMeta+a");
    await content.evaluate((element, text) => {
      const data = new DataTransfer();
      data.setData("text/plain", text.replace(/\n/g, "\r\n"));
      data.setData("text/html", "<h1>wrong terminal HTML</h1>");
      element.dispatchEvent(
        new ClipboardEvent("paste", {
          bubbles: true,
          cancelable: true,
          clipboardData: data,
        }),
      );
    }, pasted);
    assert.equal(
      await page.locator("#char-count").textContent(),
      `${[...pasted].length} 字符`,
    );
    await page.keyboard.press("ControlOrMeta+s");
    assert.deepEqual(
      await page.evaluate(
        () => window.messages.filter((m) => m.op === "save").at(-1).args,
      ),
      { text: pasted, language: "python" },
    );
    await page.keyboard.press("ControlOrMeta+z");
    await page.locator("#language").selectOption("javascript");
    await page.locator("#font-size").selectOption("14");
    await page.setViewportSize({ width: 640, height: 420 });
    assert(
      await page.evaluate(() => document.body.scrollWidth <= innerWidth),
      "minimum window overflows horizontally",
    );
    assert(
      await page
        .locator("#code-editor")
        .evaluate((e) => e.getBoundingClientRect().height > 100),
    );
    // Native window sizes include title bars, leaving a smaller web viewport.
    for (const viewport of [
      { width: 1100, height: 730 },
      { width: 640, height: 380 },
      { width: 512, height: 304 },
    ]) {
      await page.setViewportSize(viewport);
      await checkFooter(page);
      await page.locator("#editor-message").evaluate((element) => {
        element.textContent = "复制失败，请稍后重试。".repeat(4);
      });
      await checkFooter(page);
      await page.locator("#editor-message").evaluate((element) => {
        element.textContent = "";
      });
    }
    await page.setViewportSize({ width: 1100, height: 760 });
    await content.focus();
    await page.keyboard.press("ControlOrMeta+Home");
    for (let i = 0; i < 8; i++) await page.keyboard.press("Shift+ArrowRight");
    await page.screenshot({
      path: `/tmp/notebook-code-editor-${name}-${dark ? "dark" : "light"}.png`,
    });
    assert.deepEqual(errors, []);
    await page.close();
  }
  console.log(
    `PASS ${name}: both themes, syntax/gutters, Unicode counts, selection/edit/undo, search, go-to-line, wrap/font, language save, discard guard, minimum layout, footer bounds and button hit tests`,
  );
} finally {
  await browser.close();
}
