import { BrowserFiles } from "./browser-storage.mjs";
import { viewBrowserImage } from "./image-viewer.mjs";
export const isBrowser = () =>
  !window.webkit?.messageHandlers.notebook && !window.chrome?.webview;
let io, profile, settings, renewal;
const $ = (id) => document.getElementById(id);
async function api(path, body) {
  const response = await fetch("/api/" + path, {
    method: body ? "POST" : "GET",
    credentials: "same-origin",
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
    cache: "no-store",
  });
  const data = await response.json();
  if (!response.ok)
    throw Object.assign(Error(data.error || "服务暂时不可用"), {
      status: response.status,
    });
  return data;
}
function authForm() {
  const panel = document.createElement("section");
  panel.id = "web-login";
  panel.innerHTML = `<form class="web-login-card"><img src="app-icon.png" alt=""><p class="web-kicker">你的随身笔记本</p><h1>登录笔记服务</h1><p class="muted">使用笔记本独立账号登录。笔记正文会保存在这台设备上，离线也能编辑。</p><label>用户名<input name="user" autocomplete="username" required maxlength="64"></label><label>密码<input name="password" type="password" autocomplete="current-password" required maxlength="512"></label><label data-confirm hidden>确认密码<input name="confirm" type="password" autocomplete="new-password"></label><p data-error role="alert"></p><button class="primary" type="submit">登录</button></form>`;
  return panel;
}
async function login() {
  document.body.classList.add("web-locked");
  const panel = authForm();
  const token = new URLSearchParams(location.hash.slice(1)).get("setup");
  if (token) {
    history.replaceState(null, "", location.pathname + location.search);
    panel.querySelector("h1").textContent = "设置笔记本账号";
    panel.querySelector(".muted").textContent =
      "这是笔记本专用账号。设置至少 12 个字符的密码；此链接仅可使用一次。";
    panel.querySelector("[data-confirm]").hidden = false;
    panel.querySelector("[name=confirm]").required = true;
    panel.querySelector("[name=password]").autocomplete = "new-password";
    panel.querySelector("[name=password]").minLength = 12;
    panel.querySelector("button").textContent = "保存并登录";
  }
  document.body.append(panel);
  return new Promise((resolve) => {
    panel.querySelector("form").onsubmit = async (event) => {
      event.preventDefault();
      const form = event.currentTarget,
        button = form.querySelector("button");
      button.disabled = true;
      try {
        if (
          token &&
          form.elements.password.value !== form.elements.confirm.value
        )
          throw Error("两次输入的密码不一致");
        const result = await api(token ? "setup" : "login", {
          ...(token ? { token } : {}),
          user: form.elements.user.value,
          password: form.elements.password.value,
        });
        form.elements.password.value = "";
        form.elements.confirm.value = "";
        panel.remove();
        document.body.classList.remove("web-locked");
        resolve(result);
      } catch (error) {
        panel.querySelector("[data-error]").textContent = error.message;
      } finally {
        button.disabled = false;
      }
    };
  });
}
async function configureInitial() {
  const dialog = $("sync-dialog");
  dialog.querySelector("h2").textContent = "连接你的笔记库";
  $("dav-url").placeholder =
    "https://cloud.example.com/remote.php/dav/files/用户名/笔记目录/";
  $("sync-cancel").hidden = true;
  $("open-root").hidden = true;
  $("maintenance").hidden = true;
  const preventCancel = (event) => event.preventDefault();
  dialog.addEventListener("cancel", preventCancel);
  dialog.showModal();
  await new Promise((resolve) => {
    $("sync-save").onclick = async () => {
      $("sync-save").disabled = true;
      try {
        profile = await api("configure", {
          url: $("dav-url").value.trim(),
          user: $("dav-user").value.trim(),
          password: $("dav-password").value,
        });
        $("dav-password").value = "";
        dialog.close();
        resolve();
      } catch (error) {
        $("sync-error").textContent = error.message;
      } finally {
        $("sync-save").disabled = false;
      }
    };
  });
  dialog.removeEventListener("cancel", preventCancel);
  $("sync-cancel").hidden = false;
  $("maintenance").hidden = false;
}
async function request(method, path, data = null, headers = {}) {
  try {
    return await api("dav", {
      method,
      path,
      data,
      headers,
      account: profile.account,
    });
  } catch (error) {
    if (error.status === 401) {
      renewal ||= login()
        .then((result) => {
          if (result.account !== profile.account) location.reload();
          profile = result;
          localStorage.setItem("notes-session", JSON.stringify(profile));
        })
        .finally(() => {
          renewal = null;
        });
      await renewal;
      throw Error("登录已恢复，请再次同步；离线修改仍保留");
    }
    throw error;
  }
}
async function download(path, name) {
  const data = await io.readBinary(path);
  if (data === null) throw Error("附件尚未下载，请联网后重试");
  const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0)),
    url = URL.createObjectURL(new Blob([bytes]));
  const a = document.createElement("a");
  a.href = url;
  a.download = name || path.split("/").at(-1);
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
export async function installBrowserBridge() {
  document.documentElement.classList.add("web-root");
  document.body.classList.add("web-app");
  document.body.dataset.mobilePage = "notes";
  let fullHeight = innerHeight;
  const resize = () => {
    const viewport = window.visualViewport;
    const height = viewport?.height || innerHeight;
    fullHeight = Math.max(fullHeight, innerHeight, height);
    document.documentElement.style.setProperty("--web-height", height + "px");
    document.documentElement.style.setProperty(
      "--web-top",
      (viewport?.offsetTop || 0) + "px",
    );
    const focused = document.activeElement;
    const editable =
      focused?.isContentEditable || focused?.matches("input,textarea");
    document.body.dataset.keyboard = String(
      !!editable && fullHeight - height > 120,
    );
  };
  resize();
  window.visualViewport?.addEventListener("resize", resize);
  window.visualViewport?.addEventListener("scroll", resize);
  window.addEventListener("resize", resize);
  document.addEventListener("focusin", resize);
  document.addEventListener("focusout", () => setTimeout(resize, 50));
  window.addEventListener("orientationchange", () => {
    fullHeight = innerHeight;
    setTimeout(resize, 100);
  });
  if ("serviceWorker" in navigator) {
    const registration = await navigator.serviceWorker
      .register("/sw.js")
      .catch(() => null);
    if (registration)
      navigator.serviceWorker.ready.then(() => {
        $("offline-status").textContent = "应用可离线启动";
      });
  }
  const remembered = JSON.parse(
    localStorage.getItem("notes-session") || "null",
  );
  try {
    if (localStorage.getItem("notes-logout-pending")) {
      try {
        await api("logout", {});
        localStorage.removeItem("notes-logout-pending");
      } catch {}
    }
    profile =
      localStorage.getItem("notes-locked") ||
      new URLSearchParams(location.hash.slice(1)).has("setup")
        ? await login()
        : await api("session");
  } catch (error) {
    profile =
      error.status === 401
        ? await login()
        : remembered?.expires > Date.now()
          ? remembered
          : await login();
  }
  if (!profile.account) await configureInitial();
  localStorage.removeItem("notes-locked");
  localStorage.setItem("notes-session", JSON.stringify(profile));
  if (navigator.locks) {
    await new Promise((resolve, reject) => {
      navigator.locks
        .request(
          "notes-editor-" + profile.account,
          { ifAvailable: true },
          async (lock) => {
            if (!lock) {
              reject(
                Error(
                  "这台设备已在另一个窗口打开笔记。请关闭那个窗口后刷新，避免两份离线修改互相覆盖。",
                ),
              );
              return;
            }
            resolve();
            await new Promise(() => {});
          },
        )
        .catch(reject);
    });
  }
  io = await BrowserFiles.open(profile.account, request);
  settings = {
    ...profile,
    platform: "web",
    theme: localStorage.getItem("notes-theme") || "Light",
    nativeTitlebar: false,
  };
  window.notebookBrowser = { io, request };
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: async ({ id, op, args: a }) => {
      try {
        let result;
        if (op === "settings") result = settings;
        else if (op === "theme") {
          settings.theme = a.value;
          localStorage.setItem("notes-theme", a.value);
          result = settings;
        } else if (op === "configure") {
          const next = await api("configure", a);
          if (next.account !== profile.account) {
            location.reload();
            throw Error("已切换服务器笔记库，正在载入对应的离线副本");
          }
          profile = next;
          settings = { ...settings, ...next };
          localStorage.setItem("notes-session", JSON.stringify(profile));
          result = settings;
        } else if (op === "http")
          result = await request(a.method, a.path, a.data, a.headers);
        else if (op === "read" || op === "readBinary" || op === "list")
          result = await io[op](a.path);
        else if (op === "write") result = await io.writeBinary(a.path, a.data);
        else if (op === "mkdir" || op === "remove" || op === "purgeDeleted")
          result = await io[op](a.path);
        else if (op === "move") result = await io.move(a.path, a.to);
        else if (op === "clipboard") {
          await navigator.clipboard.writeText(a.text);
        } else if (op === "readClipboard")
          result = await navigator.clipboard.readText();
        else if (op === "open") {
          if (io.entries.get(a.path)?.dir)
            throw Error("手机端通过笔记列表管理目录");
          await download(a.path);
        } else if (op === "viewImage")
          await viewBrowserImage(a.path, (path) => io.readBinary(path));
        else if (op === "saveAttachment") await download(a.path, a.name);
        else if (op === "external") {
          const url = new URL(a.url);
          if (!["https:", "http:"].includes(url.protocol))
            throw Error("链接无效");
          window.open(url.href, "_blank", "noopener,noreferrer");
        } else if (op === "ready") setupNavigation();
        else if (op !== "window") throw Error("手机端暂不支持此操作");
        window.nativeReply(id, result ?? null);
      } catch (error) {
        window.nativeReply(id, null, error.message);
      }
    },
  };
  return io;
}
function setupNavigation() {
  $("open-root").hidden = true;
  $("web-books").onclick = async () => {
    await window.notebookFlush();
    $("trash-close").click();
    document.body.dataset.mobilePage = "books";
  };
  $("web-back").onclick = async () => {
    await window.notebookFlush();
    $("trash-close").click();
    document.body.dataset.mobilePage = "notes";
  };
  $("web-note-list").onclick = () =>
    (document.body.dataset.mobilePage = "notes");
  $("mobile-books").onclick = () => $("web-books").click();
  $("mobile-notes").onclick = () => $("web-back").click();
  $("mobile-sync").onclick = () => window.notebookSync();
  $("mobile-theme").onclick = () => $("theme").click();
  const updateNavigation = () => {
    const books = document.body.dataset.mobilePage === "books";
    $("mobile-books").setAttribute("aria-current", books ? "page" : "false");
    $("mobile-notes").setAttribute("aria-current", books ? "false" : "page");
  };
  new MutationObserver(updateNavigation).observe(document.body, {
    attributes: true,
    attributeFilter: ["data-mobile-page"],
  });
  updateNavigation();
  $("web-logout").onclick = async () => {
    await window.notebookFlush();
    if (
      (await io.pendingCount()) &&
      !confirm(
        "还有未同步的修改。退出后会保留在本机，重新登录后才能继续同步。确定退出？",
      )
    )
      return;
    localStorage.setItem("notes-locked", "true");
    localStorage.setItem("notes-logout-pending", "true");
    try {
      await api("logout", {});
      localStorage.removeItem("notes-logout-pending");
    } catch {}
    localStorage.removeItem("notes-session");
    location.reload();
  };
  $("web-export").onclick = async () => {
    await window.notebookFlush();
    const files = [...io.entries.values()].filter(
      (r) => !r.dir && !r.deleted && r.data !== null,
    );
    const blob = new Blob(
        [
          JSON.stringify(
            {
              version: 1,
              library: profile.url,
              exported: new Date().toISOString(),
              files,
            },
            null,
            2,
          ),
        ],
        { type: "application/json" },
      ),
      url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download =
      "notes-backup-" + new Date().toISOString().slice(0, 10) + ".json";
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  };
  const badge = async () => {
    const count = await io.pendingCount();
    $("web-sync-state").textContent = !navigator.onLine
      ? `离线${count ? " · " + count + " 项待同步" : ""}`
      : count
        ? count + " 项待同步"
        : "文字已保存在本机";
  };
  let timer;
  window.addEventListener("notebook-storage", () => {
    clearTimeout(timer);
    timer = setTimeout(() => badge().catch(() => {}), 150);
  });
  const synchronize = () => {
    badge().catch(() => {});
    if (navigator.onLine) window.notebookSync();
  };
  window.addEventListener("online", synchronize);
  window.addEventListener("offline", () => badge().catch(() => {}));
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden")
      window.notebookFlush().catch(() => {});
    else synchronize();
  });
  // iOS may suspend background timers. Always retry when the app becomes visible.
  setInterval(() => {
    if (document.visibilityState === "visible" && navigator.onLine)
      window.notebookSync();
  }, 60000);
  const editor = $("editor");
  editor.addEventListener(
    "input",
    () => {
      clearTimeout(setupNavigation.editTimer);
      setupNavigation.editTimer = setTimeout(synchronize, 5000);
    },
    true,
  );
  if (navigator.storage?.persist)
    navigator.storage.persist().then((persistent) => {
      $("offline-status").textContent = persistent
        ? "离线存储已获持久保留"
        : "离线内容已缓存；请及时同步未上传的修改";
    });
  badge().catch(() => {});
}
