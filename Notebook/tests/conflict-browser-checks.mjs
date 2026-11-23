// Uses only /tmp/notebook-conflict-ui-library and an isolated preview on 4174.
import assert from "node:assert/strict";
import { mkdir, writeFile, readFile, rm } from "node:fs/promises";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
const name = process.argv[3] || "webkit";
const { [name]: engine } = await import(pathToFileURL(process.argv[2]).href);
const root = "/tmp/notebook-conflict-ui-library";
const path = "books/conflict-check/entry/main.md";
const local = "本地正文\n",
  online = "在线正文\n";
const base = "https://cloud.fixture/remote.php/dav/files/test/Notes/";
const browser = await engine.launch({
  headless: true,
  ...(name === "chromium"
    ? {
        executablePath: "/usr/bin/google-chrome",
        args: ["--no-sandbox"],
      }
    : {}),
});
try {
  for (const scenario of process.env.NOTEBOOK_CONFLICT_PROBE
    ? ["probe"]
    : [
        "local",
        "remote",
        "merge",
        "locked",
        "stale",
        "denied",
        "closed-error",
        "empty",
      ]) {
    await rm(root, { recursive: true, force: true });
    await mkdir(`${root}/books/conflict-check/entry/reference`, {
      recursive: true,
    });
    const files = {
      "books/conflict-check/book.json": JSON.stringify({
        id: "conflict-check",
        title: "冲突回归",
        created: 0,
      }),
      "books/conflict-check/entry/note.json": JSON.stringify({
        id: "entry",
        title: "处理按钮回归",
        created: 0,
        updated: Date.now(),
      }),
      [path]: local,
    };
    for (const [p, text] of Object.entries(files))
      await writeFile(`${root}/${p}`, text);
    await writeFile(
      `${root}/.sync.json`,
      JSON.stringify({
        files: {
          [path]: {
            hash: createHash("sha256").update("同步基准\n").digest("hex"),
          },
        },
        conflicts: [],
      }),
    );
    const context = await browser.newContext({
      viewport: { width: 1280, height: 860 },
    });
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.addInitScript(
      ({ files, path, online, base }) => {
        const encode = (text) => btoa(unescape(encodeURIComponent(text)));
        let version = 0,
          deny = false,
          gate = false;
        const data = new Map(
          Object.entries(files).map(([p, text]) => [
            p,
            { data: encode(text), etag: `"${++version}"` },
          ]),
        );
        const put = (p, text) =>
          data.set(p, { data: encode(text), etag: `"${++version}"` });
        put(path, online);
        window.conflictTest = {
          put,
          data,
          getCount: 0,
          lock: () =>
            put(
              ".notebook-lock.json",
              JSON.stringify({
                owner: "another-device",
                expires: Date.now() + 60000,
              }),
            ),
          unlock: () => data.delete(".notebook-lock.json"),
          deny: (value) => {
            deny = value;
          },
          gate: () => {
            gate = true;
          },
        };
        const http = async ({ method, path: p, data: body, headers = {} }) => {
          if (p === path && method === "GET") {
            window.conflictTest.getCount++;
            if (gate) {
              gate = false;
              await new Promise((resolve) => {
                window.conflictTest.release = resolve;
              });
            }
          }
          const file = data.get(p);
          if (deny && p === path) return { status: 403, data: "", etag: "" };
          if (method === "PROPFIND") {
            const start = p.replace(/\/$/, "") + (p ? "/" : "");
            const entries = new Map([[start, true]]);
            for (const key of data.keys())
              if (key.startsWith(start)) {
                const rest = key.slice(start.length);
                if (rest)
                  entries.set(
                    start +
                      rest.split("/")[0] +
                      (rest.includes("/") ? "/" : ""),
                    rest.includes("/"),
                  );
              }
            const xml =
              '<d:multistatus xmlns:d="DAV:">' +
              [...entries]
                .map(
                  ([key, folder]) =>
                    `<d:response><d:href>${base + key}</d:href><d:propstat><d:prop><d:resourcetype>${folder ? "<d:collection/>" : ""}</d:resourcetype><d:getetag>${data.get(key)?.etag || ""}</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>`,
                )
                .join("") +
              "</d:multistatus>";
            return { status: 207, data: encode(xml), etag: "" };
          }
          if (method === "GET")
            return {
              status: file ? 200 : 404,
              data: file?.data || "",
              etag: file?.etag || "",
            };
          if (method === "MKCOL") return { status: 201, data: "", etag: "" };
          if (
            (headers["If-Match"] && file?.etag !== headers["If-Match"]) ||
            (headers["If-None-Match"] === "*" && file)
          )
            return { status: 412, data: "", etag: file?.etag || "" };
          if (method === "DELETE") data.delete(p);
          if (method === "PUT")
            data.set(p, { data: body, etag: `"${++version}"` });
          return {
            status: file ? 204 : 201,
            data: "",
            etag: data.get(p)?.etag || "",
          };
        };
        window.chrome = {
          webview: {
            postMessage: async (message) => {
              try {
                let value;
                if (message.op === "settings")
                  value = {
                    platform: "mac",
                    nativeTitlebar: true,
                    theme: "Light",
                    syncEnabled: true,
                    url: base,
                    user: "test",
                  };
                else if (message.op === "http")
                  value = await http(message.args);
                else {
                  const response = await fetch("/_bridge", {
                    method: "POST",
                    body: JSON.stringify(message),
                  });
                  const result = await response.json();
                  if (result.error) throw Error(result.error);
                  value = result.result;
                }
                window.nativeReply(message.id, value);
                if (message.op === "ready") window.conflictReady = true;
              } catch (error) {
                window.nativeReply(message.id, null, error.message);
              }
            },
          },
        };
      },
      { files, path, online: scenario === "empty" ? "" : online, base },
    );
    await page.goto("http://127.0.0.1:4174/index.html");
    await page.waitForFunction(
      () =>
        window.conflictReady &&
        document.getElementById("sync-status").textContent.includes("项未完成"),
    );
    const open = async () => {
      await page.locator("#sync-results").click();
      await page.locator("#sync-report-conflicts").click();
      await page.locator("#conflict-dialog[open]").waitFor();
    };
    await open();
    const row = page.locator("#conflict-list > div").first();
    const choose = (text) =>
      row.getByRole("button", { name: text, exact: true });
    if (scenario === "probe") {
      await page.evaluate(() => window.conflictTest.lock());
      await choose("保留本地").click();
      await page.locator("#toast:not([hidden])").waitFor();
      const evidence = await page.evaluate(() => {
        const toast = document.getElementById("toast"),
          box = toast.getBoundingClientRect();
        return {
          message: toast.textContent,
          covered: !toast.contains(
            document.elementFromPoint(box.x + 5, box.y + 5),
          ),
          dialog: document.getElementById("conflict-dialog").open,
        };
      });
      assert(evidence.dialog && evidence.covered);
      console.log("REPRODUCED hidden conflict error:", evidence);
      await context.close();
      continue;
    }
    if (scenario === "locked" || scenario === "denied") {
      await page.evaluate(
        (locked) =>
          locked ? window.conflictTest.lock() : window.conflictTest.deny(true),
        scenario === "locked",
      );
      await choose("保留本地").click();
      await row.locator('.conflict-status[data-error="true"]').waitFor();
      const text = await row.locator(".conflict-status").innerText();
      assert(
        scenario === "locked"
          ? text.includes("另一台设备")
          : text.includes("403"),
      );
      assert.equal(await choose("保留本地").isEnabled(), true);
      assert.equal(await readFile(`${root}/${path}`, "utf8"), local);
      const visible = await row
        .locator(".conflict-status")
        .evaluate((status) => {
          const box = status.getBoundingClientRect();
          return status.contains(
            document.elementFromPoint(box.x + 5, box.y + 5),
          );
        });
      assert(visible, "error is covered by the dialog's top layer");
      await page.evaluate(() => {
        window.conflictTest.unlock();
        window.conflictTest.deny(false);
      });
      await choose("保留本地").click();
    } else if (scenario === "closed-error") {
      await page.evaluate(() => window.conflictTest.gate());
      await choose("保留本地").click();
      await page.waitForFunction(() => !!window.conflictTest.release);
      await page.locator("#conflict-close").click();
      await page.evaluate(() => {
        window.conflictTest.deny(true);
        window.conflictTest.release();
      });
      await page.locator("#toast:not([hidden])").waitFor();
      assert((await page.locator("#toast").innerText()).includes("403"));
      await open();
      await page.evaluate(() => window.conflictTest.deny(false));
      await choose("保留本地").click();
    } else if (scenario === "merge" || scenario === "stale") {
      await row.locator("summary").click();
      await page.waitForFunction(
        () => !!document.querySelector("#conflict-list > div")?.dataset.loaded,
      );
      await row
        .getByRole("textbox", { name: "合并后的正文", exact: true })
        .fill("人工合并正文\n");
      if (scenario === "stale") {
        await page.evaluate(
          (p) => window.conflictTest.put(p, "第三台设备的新版本\n"),
          path,
        );
        await choose("采用在线").click();
        await row.locator('.conflict-status[data-error="true"]').waitFor();
        assert.equal(await readFile(`${root}/${path}`, "utf8"), local);
        await choose("重新读取版本").click();
        await page.waitForFunction(
          () =>
            document.querySelector('[aria-label="冲突时的在线正文"]').value ===
            "第三台设备的新版本\n",
        );
        assert.equal(
          await row
            .getByRole("textbox", { name: "合并后的正文", exact: true })
            .inputValue(),
          "人工合并正文\n",
        );
      }
      await choose("保存合并正文并同步").click();
    } else if (scenario === "local") {
      await page.evaluate(() => window.conflictTest.gate());
      await choose("保留本地").click();
      await page.waitForFunction(() => !!window.conflictTest.release);
      assert.equal(await choose("采用在线").isDisabled(), true);
      assert(
        (await row.locator(".conflict-status").innerText()).includes(
          "正在处理",
        ),
      );
      await page.locator("#conflict-close").click();
      await open();
      assert.equal(await choose("保留本地").isDisabled(), true);
      await page.evaluate(() => window.conflictTest.release());
    } else await choose("采用在线").click();
    await page.waitForFunction(
      () =>
        !document.getElementById("conflict-dialog").open &&
        document.getElementById("sync-status").textContent ===
          "Nextcloud 已同步",
    );
    const expected =
      scenario === "merge" || scenario === "stale"
        ? "人工合并正文\n"
        : scenario === "remote"
          ? online
          : scenario === "empty"
            ? ""
            : local;
    assert.equal(await readFile(`${root}/${path}`, "utf8"), expected);
    const remoteText = await page.evaluate(
      (p) =>
        decodeURIComponent(escape(atob(window.conflictTest.data.get(p).data))),
      path,
    );
    assert.equal(remoteText, expected);
    assert.equal(
      JSON.parse(await readFile(`${root}/.sync.json`, "utf8")).conflicts.length,
      0,
    );
    const report = JSON.parse(
      await readFile(`${root}/.sync-report.json`, "utf8"),
    );
    assert(
      report.items.find((item) => item.id === "entry").complete,
      "the entire note must finish syncing",
    );
    assert.deepEqual(errors, []);
    console.log(`PASS ${name} conflict ${scenario}`);
    await context.close();
  }
} finally {
  await browser.close();
}
