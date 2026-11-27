import { isBrowser, installBrowserBridge } from "./browser.mjs";
import { changeTable, tableMarkdown, chooseTableSize } from "./tables.mjs";
import {
  createIcons,
  Library,
  Files,
  Clock,
  Plus,
  Folder,
  Trash2,
  Cloud,
  RefreshCw,
  Settings2,
  Search,
  Paperclip,
  Ellipsis,
  SunMoon,
  Minus,
  Square,
  X,
  ChevronDown,
  NotebookPen,
  File,
  RotateCcw,
} from "lucide";
import {
  NotebookStore,
  encode,
  decode,
  attachmentPath,
  attachmentLink,
  references,
  DAY,
} from "./core.mjs";
import { WebDAVSync } from "./sync.mjs";
import { listRemoteFiles } from "./webdav.mjs";
import { installImageRenderer, installImageSelection } from "./images.mjs";
import { installBlankParagraphs } from "./blank-lines.mjs";
import { installEnterBehavior } from "./enter-behavior.mjs";
import { installLineBreaks } from "./line-breaks.mjs";
import { CODE_LANGUAGES, detectCodeLanguage } from "./code.mjs";
import {
  installCodeEditing,
  selectedCode,
  setCodeText,
} from "./code-editing.mjs";
import { MarkdownHistory } from "./history.mjs";
import { VisibleWork } from "./visible-work.mjs";
import { captureReadingPosition, restoreReadingPosition } from "./position.mjs";
import { installPageGutterScrolling } from "./scrolling.mjs";
const $ = (id) => document.getElementById(id);
const icons = () =>
  createIcons({
    icons: {
      Library,
      Files,
      Clock,
      Plus,
      Folder,
      Trash2,
      Cloud,
      RefreshCw,
      Settings2,
      Search,
      Paperclip,
      Ellipsis,
      SunMoon,
      Minus,
      Square,
      X,
      ChevronDown,
      NotebookPen,
      File,
      RotateCcw,
    },
  });
let externalConflict = false,
  configuring = false,
  resolvingExternal = false,
  editingCode = false;
let sequence = 0;
const pending = new Map();
window.nativeReply = (id, result, error) => {
  const call = pending.get(id);
  if (!call) return;
  pending.delete(id);
  error ? call.reject(new Error(error)) : call.resolve(result);
};
function native(op, args = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    pending.set(id, { resolve, reject });
    const message = { id, op, args };
    if (window.webkit?.messageHandlers.notebook)
      window.webkit.messageHandlers.notebook.postMessage(message);
    else if (window.chrome?.webview) window.chrome.webview.postMessage(message);
    else {
      pending.delete(id);
      reject(new Error("笔记窗口需要原生文件服务"));
    }
  });
}
const io = {
  read: (path) => native("read", { path }),
  write: (path, text) => native("write", { path, data: encode(text) }),
  readBinary: (path) => native("readBinary", { path }),
  writeBinary: (path, data) => native("write", { path, data }),
  list: (path) => native("list", { path }),
  mkdir: (path) => native("mkdir", { path }),
  move: (path, to) => native("move", { path, to }),
  remove: (path) => native("remove", { path }),
  purgeDeleted: (path) => native("purgeDeleted", { path }),
  deferReconcile: (path) =>
    window.notebookBrowser?.io.deferReconcile(path) ?? false,
  commonBase: (path) => window.notebookBrowser?.io.entries.get(path)?.common,
  trackedPaths: () =>
    [...(window.notebookBrowser?.io.entries.values() || [])]
      .filter((r) => r.deleted)
      .map((r) => r.path),
};
const histories = new Map();
let history;
let cancelPositionRestore = () => {};
let selectionQueue = Promise.resolve(),
  selectionVersion = 0,
  loadingNote = false;
let store = new NotebookStore(io),
  catalog,
  active,
  bookId = "all",
  editor,
  settings,
  dirty = false,
  saveTimer,
  saving = Promise.resolve(),
  switching = false,
  attachmentTab = "live",
  syncing = false;
let syncRetryTimer;
let syncRetryAttempt = 0;
let lastSyncReport = null;
let lastSyncEngine = null;
const pendingConflictResolutions = new Map();
let mobileEditing = false;
let enterBehavior = "paragraph";
const mobileScreen = matchMedia("(max-width: 760px)");
const mobileReadOnly = () =>
  settings?.platform === "web" && mobileScreen.matches && !mobileEditing;
function applyMobileAccess() {
  if (!editor) return;
  const readonly = mobileReadOnly();
  document.body.dataset.mobileEditing = String(!readonly);
  $("mobile-edit").textContent = readonly ? "开启编辑" : "完成编辑";
  $("mobile-edit").setAttribute("aria-pressed", String(!readonly));
  const mode = editor.vditor.currentMode;
  const surface = editor.vditor[mode]?.element;
  if (
    readonly &&
    (mode === "sv"
      ? !surface?.disabled
      : surface?.getAttribute("contenteditable") !== "false")
  )
    editor.disabled();
  else if (
    !readonly &&
    (mode === "sv"
      ? surface?.disabled
      : surface?.getAttribute("contenteditable") === "false")
  )
    editor.enable();
  $("editor")
    .querySelectorAll(".code-language, .vditor-ir input[type=checkbox]")
    .forEach((x) => {
      x.disabled =
        readonly ||
        (x.matches(".code-language") &&
          !x
            .closest('[data-type="code-block"]')
            ?.querySelector('[data-type="code-block-info"]'));
    });
}
mobileScreen.addEventListener("change", () => {
  mobileEditing = false;
  applyMobileAccess();
});
function toast(message) {
  $("toast").textContent = message;
  $("toast").hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => ($("toast").hidden = true), 7000);
}
function protect(work) {
  return async (...args) => {
    try {
      await work(...args);
    } catch (error) {
      toast(error.message);
    }
  };
}
async function choose(title, text, yes, no, cancelValue = false) {
  const dialog = document.createElement("dialog"),
    heading = label(title, "h2"),
    description = label(text, "p"),
    actions = document.createElement("div");
  actions.className = "dialog-actions";
  const cancel = document.createElement("button"),
    accept = document.createElement("button");
  cancel.textContent = no;
  accept.textContent = yes;
  accept.className = "primary";
  actions.append(cancel, accept);
  dialog.append(heading, description, actions);
  document.body.append(dialog);
  dialog.showModal();
  return new Promise((resolve) => {
    let result = cancelValue;
    accept.onclick = () => {
      result = true;
      dialog.close();
    };
    cancel.onclick = () => {
      result = false;
      dialog.close();
    };
    dialog.onclose = () => {
      dialog.remove();
      resolve(result);
    };
  });
}
function label(text, tag = "span") {
  const node = document.createElement(tag);
  node.textContent = text;
  return node;
}
function date(time) {
  return new Date(time).toLocaleString("zh-CN", {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}
function icon(name) {
  const element = document.createElement("i");
  element.setAttribute("data-lucide", name);
  return element;
}
async function ask(title, value = "") {
  $("text-title").textContent = title;
  $("text-value").value = value;
  $("text-dialog").returnValue = "";
  $("text-cancel").onclick = () => $("text-dialog").close("cancel");
  $("text-dialog").showModal();
  $("text-value").focus();
  $("text-value").select();
  return new Promise((resolve) =>
    $("text-dialog").addEventListener(
      "close",
      () =>
        resolve(
          $("text-dialog").returnValue === "ok"
            ? $("text-value").value.trim()
            : null,
        ),
      { once: true },
    ),
  );
}
async function refresh() {
  catalog = await store.catalog();
  renderNavigation();
  renderNotes();
  $("empty-title").textContent = catalog.notes.length ? "选择一篇笔记" : "写下第一篇笔记";
  $("empty-hint").textContent = catalog.notes.length
    ? "从列表打开已有笔记，或新建一篇。"
    : "可以直接写进快速笔记，也可以先建一个笔记本。";
}
function renderNavigation() {
  $("book-list").replaceChildren();
  for (const book of catalog.books.filter((x) => x.id !== "quick-notes")) {
    const button = document.createElement("button");
    button.className = bookId === book.id ? "active" : "";
    const title = label(book.title);
    title.className = "book-title";
    const count = label(
      String(catalog.notes.filter((x) => x.bookId === book.id).length),
    );
    count.className = "count";
    button.append(icon("folder"), title, count);
    button.onclick = protect(async () => {
      await flush();
      bookId = book.id;
      leaveTrash();
      renderNavigation();
      renderNotes();
      if (settings.platform === "web")
        document.body.dataset.mobilePage = "notes";
    });
    button.oncontextmenu = (event) => bookMenu(event, book);
    $("book-list").append(button);
  }
  for (const button of document.querySelectorAll("[data-filter]"))
    button.classList.toggle("active", button.dataset.filter === bookId);
  $("list-title").textContent =
    catalog.books.find((x) => x.id === bookId)?.title ||
    (bookId === "recent" ? "最近使用" : "全部笔记");
  icons();
}
function renderNotes() {
  const search = $("search").value.toLocaleLowerCase();
  let notes = catalog.notes
    .filter(
      (x) =>
        (bookId === "all" || bookId === "recent" || x.bookId === bookId) &&
        `${x.title}\n${x.markdown}`.toLocaleLowerCase().includes(search),
    )
    .sort((a, b) => b.updated - a.updated);
  if (bookId === "recent") notes = notes.slice(0, 30);
  $("note-list").replaceChildren();
  for (const note of notes) {
    const button = document.createElement("button");
    button.className = `note-row ${active?.path === note.path ? "active" : ""}`;
    button.title = note.title;
    const summary = note.markdown
      .replace(/^#\s+[^\n]*(?:\n|$)/, "")
      .replace(/!?\[([^\]]*)\]\([^)]+\)/g, "$1")
      .replace(/[#*`\[\]>]/g, "")
      .replace(/\s+/g, " ")
      .slice(0, 80);
    const time = label(date(note.updated), "time");
    const heading = document.createElement("div");
    heading.className = "note-row-heading";
    heading.append(label(note.title, "strong"), time);
    button.append(heading, label(summary, "p"));
    button.onclick = protect(() => selectNote(note));
    button.oncontextmenu = (event) => noteMenu(event, note);
    $("note-list").append(button);
  }
  if (!notes.length)
    $("note-list").append(
      label(search ? "没有匹配的笔记" : "还没有笔记", "p"),
    );
}
async function flush() {
  if (resolvingExternal)
    throw new Error("请先处理正文的外部修改，再保存或退出");
  clearTimeout(saveTimer);
  if (dirty && active && editor) {
    const note = active,
      value = editor.getValue();
    history?.record(value);
    dirty = false;
    $("save-status").textContent = "正在保存…";
    saving = saving
      .catch(() => {})
      .then(() => store.save(note, value))
      .then(() => {
        note.markdown = value;
        note.updated = Date.now();
        const row = catalog?.notes.find((x) => x.id === note.id);
        if (row) {
          row.markdown = value;
          row.updated = note.updated;
        }
        renderNotes();
        $("save-status").textContent =
          (settings.platform === "web" ? "已保存到本机 · " : "已保存 · ") +
          date(Date.now());
      })
      .catch((error) => {
        dirty = true;
        externalConflict = error.message.includes("其他程序");
        $("save-status").textContent = externalConflict
          ? "检测到外部修改 · 点击处理"
          : "保存失败 · 点击重试";
        throw error;
      });
  }
  await saving;
}
function selectNote(note) {
  const version = ++selectionVersion;
  const current = () => version === selectionVersion;
  // Serialize switches and discard superseded requests. The old editor must
  // never accept keystrokes after its save snapshot and before the new content.
  const task = selectionQueue
    .catch(() => {})
    .then(async () => {
      if (!current()) return;
      loadingNote = true;
      $("editor").inert = true;
      $("note-toolbar").inert = true;
      try {
        await loadNote(note, current);
      } finally {
        loadingNote = false;
        $("editor").inert = false;
        $("note-toolbar").inert = false;
      }
    });
  selectionQueue = task;
  return task;
}
async function loadNote(note, current) {
  cancelPositionRestore();
  leaveTrash();
  await flush();
  if (!current()) return;
  // Always read disk for external/sync edits; do not serve stale catalog text.
  const [markdown, recovery] = await Promise.all([
    io.read(`${note.path}/main.md`),
    io.read(`.drafts/${note.id}.md`),
  ]);
  if (!current()) return;
  if (markdown === null) throw new Error("笔记已被移动或删除");
  let value = markdown,
    recovered = false;
  if (
    recovery !== null &&
    recovery !== markdown &&
    (await choose(
      "发现未完成的保存",
      "恢复稿已保留。恢复后先在正文中检查，再保存；也可以打开原文对比。",
      "打开恢复稿",
      "打开原文",
    ))
  ) {
    value = recovery;
    recovered = true;
  }
  if (!current()) return;
  if (active && active.path !== note.path) {
    store.drafts.delete(active.path);
    imageCache.clear();
  }
  active = note;
  active.markdown = markdown;
  store.setDraft(note.path, markdown);
  $("empty").hidden = true;
  $("editor").hidden = false;
  $("breadcrumb").textContent =
    `${catalog.books.find((x) => x.id === note.bookId)?.title || ""} / ${note.title}`;
  dirty = recovered;
  if (editor) {
    switching = true;
    editor.setValue(value, true);
    switching = false;
  } else await createEditor(value);
  store.setDraft(note.path, value);
  history = histories.get(note.id);
  if (!history) {
    history = new MarkdownHistory(value);
    histories.set(note.id, history);
  } else if (history.value !== value) {
    history.seal();
    history.record(value);
    history.seal();
  }
  history.normalizeCurrent(editor.getValue());
  paintHistory();
  $("note-toolbar")
    .querySelectorAll("button")
    .forEach((x) => (x.disabled = false));
  renderNotes();
  // Attachment bookkeeping stays on the store's serial queue, but does not
  // delay the next selection. Images are confined to the currently open note.
  protect(renderAttachments)();
  protect(() => store.rememberPage(note, bookId))();
  if (settings.platform === "web") document.body.dataset.mobilePage = "editor";
  mobileEditing = false;
  applyMobileAccess();
  protect(async () => {
    await warmImages(note, value, current);
    if (current() && active?.path === note.path) await renderImages();
  })();
}
async function createEditor(value) {
  await new Promise((resolve) => {
    editor = new window.Vditor("editor", {
      undoDelay: 100,
      cdn: new URL("vendor/vditor", location.href).href.replace(/\/$/, ""),
      lang: "zh_CN",
      mode: "ir",
      value: "",
      height: "100%",
      minHeight: 200,
      cache: { enable: false },
      image: {
        // Vditor's full-screen overlay belongs to the editor document. Use the
        // OS viewer (or a separate browser tab) instead, including in read-only mode.
        isPreview: false,
        preview: protect(async (image) => {
          const source =
            image.dataset.notebookReference || image.getAttribute("src") || "";
          const path = attachmentPath(source);
          if (path && active)
            await native("viewImage", { path: `${active.path}/${path}` });
          else if (/^https?:\/\//i.test(source))
            await native("external", { url: source });
          else throw new Error("这张图片没有可打开的附件文件");
        }),
      },
      customRenders: deferredCodeRenders(value),
      toolbar: [
        "undo",
        "redo",
        "|",
        "headings",
        "heading2",
        "heading3",
        "bold",
        "italic",
        "strike",
        "|",
        "list",
        "ordered-list",
        "check",
        "quote",
        "code",
        "inline-code",
        "link",
        "table",
        "|",
        "edit-mode",
        "display",
      ].map((name) =>
        name === "|"
          ? name
          : {
              name,
              ...(name === "headings"
                ? {
                    tip: "一级标题",
                    icon: '<span class="heading-icon">H₁</span>',
                  }
                : {}),
              ...(name === "heading2" || name === "heading3"
                ? {
                    tip: name === "heading2" ? "二级标题" : "三级标题",
                    icon: `<span class="heading-icon">H${name.endsWith("2") ? "₂" : "₃"}</span>`,
                  }
                : {}),
              ...(name === "check" ? { tip: "任务/多选框" } : {}),
              ...(name === "display"
                ? {
                    tip: "选项",
                    icon: "选项",
                    className: "notebook-display",
                  }
                : {}),
              tipPosition:
                name === "display"
                  ? "sw"
                  : name === "undo"
                    ? "se"
                    : name === "edit-mode"
                      ? "sw"
                      : "s",
            },
      ),
      theme: settings.theme === "Dark" ? "dark" : "classic",
      preview: {
        delay: 100,
        mode: "editor",
        markdown: { sanitize: true },
        theme: {
          current: settings.theme === "Dark" ? "dark" : "light",
          path: new URL("vendor/vditor/dist/css/content-theme", location.href)
            .href,
        },
        hljs: {
          enable: true,
          lineNumber: false,
          style: settings.theme === "Dark" ? "github-dark" : "github",
          renderMenu: codeMenu,
        },
      },
      upload: {
        handler: async (files) => {
          try {
            await attachFiles(files);
            return null;
          } catch (error) {
            toast(error.message);
            return error.message;
          }
        },
      },
      input: (markdown) => {
        if (switching || !active) return;
        dirty = true;
        history?.record(markdown);
        paintHistory();
        store.setDraft(active.path, markdown);
        $("save-status").textContent = "尚未保存…";
        clearTimeout(saveTimer);
        saveTimer = setTimeout(protect(flush), 350);
      },
      after: () => {
        const setValue = editor.setValue.bind(editor);
        editor.setValue = (markdown, ...args) => {
          codeWork.reset();
          editor.vditor.options.customRenders = deferredCodeRenders(markdown);
          return setValue(markdown, ...args);
        };
        installImageRenderer(editor.vditor.lute, (path) =>
          imageCache.get(`${active.path}/${path}`),
        );
        installBlankParagraphs(editor.vditor.lute);
        installLineBreaks(editor.vditor.lute);
        switching = true;
        editor.setValue(value, true);
        switching = false;
        icons();
        resolve();
      },
    });
  });
  applyTheme();
  // Vditor delays its input callback by undoDelay. Mark native edits dirty
  // immediately so switching/closing just after Delete cannot miss the change.
  $("editor").addEventListener(
    "input",
    () => {
      if (!switching && active) dirty = true;
    },
    true,
  );
  installImageSelection(editor, mobileReadOnly);
  installCodeEditing(editor, {
    enabled: () => !mobileReadOnly(),
    before: () => {
      history?.record(editor.getValue());
      history?.seal();
    },
    after: () => editor.vditor.options.input(editor.getValue()),
  });
  installEnterBehavior(editor, {
    enabled: () => enterBehavior === "line" && !mobileReadOnly(),
    before: () => history?.record(editor.getValue()),
    after: () => editor.vditor.options.input(editor.getValue()),
  });
  installPageGutterScrolling(editor, () => cancelPositionRestore());
  $("editor").addEventListener(
    "mousedown",
    (event) => {
      if (
        event.target.closest(
          '.vditor-toolbar [data-type="code"], .vditor-toolbar [data-type="table"], .vditor-toolbar [data-type="headings"], .vditor-toolbar [data-type="heading2"], .vditor-toolbar [data-type="heading3"], .vditor-toolbar [data-type="display"]',
        )
      )
        event.preventDefault();
    },
    true,
  );
  $("editor").addEventListener(
    "click",
    (event) => {
      // Read-only content must not trigger Vditor's marker expansion or table popover.
      // Keep attachment links and copy controls interactive.
      if (
        mobileReadOnly() &&
        event.target.closest(".vditor-ir") &&
        !event.target.closest("a, button, .vditor-copy")
      ) {
        event.stopImmediatePropagation();
        return;
      }
      const preview = event.target.closest(".vditor-ir__preview > code");
      const block = preview?.closest('[data-type="code-block"]');
      if (block) {
        if (mobileReadOnly()) {
          event.stopImmediatePropagation();
          return;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
        const selected = selectedCode(editor.vditor.ir.element);
        if (selected?.block === block && !selected.range.collapsed) {
          focusCode(block, 0);
          selected.selection.removeAllRanges();
          selected.selection.addRange(selected.range);
          return;
        }
        const caret = document.caretRangeFromPoint?.(
          event.clientX,
          event.clientY,
        );
        let offset = 0;
        if (caret && preview.contains(caret.startContainer)) {
          const before = document.createRange();
          before.selectNodeContents(preview);
          before.setEnd(caret.startContainer, caret.startOffset);
          offset = before.toString().length;
        }
        focusCode(block, offset);
        return;
      }
      const heading = event.target.closest(
        '.vditor-toolbar [data-type="headings"], .vditor-toolbar [data-type="heading2"], .vditor-toolbar [data-type="heading3"]',
      );
      if (heading) {
        event.preventDefault();
        event.stopImmediatePropagation();
        const level =
          heading.dataset.type === "headings"
            ? 1
            : +heading.dataset.type.slice(-1);
        $("editor")
          .querySelector(`.vditor-toolbar [data-tag="h${level}"]`)
          ?.click();
        return;
      }
      if (event.target.closest('.vditor-toolbar [data-type="display"]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
        protect(showDisplaySettings)();
        return;
      }
      if (event.target.closest('.vditor-toolbar [data-type="code"]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
        insertCode();
        return;
      }
      if (event.target.closest('.vditor-toolbar [data-type="table"]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
        protect(insertTable)();
        return;
      }
      const command = event.target.closest("[data-type=undo],[data-type=redo]");
      if (command) {
        event.preventDefault();
        event.stopImmediatePropagation();
        history?.record(editor.getValue());
        restoreHistory(command.dataset.type);
      }
    },
    true,
  );
  $("editor").addEventListener(
    "keydown",
    (event) => {
      if (mobileReadOnly()) return;
      if (
        (event.ctrlKey || event.metaKey) &&
        !event.altKey &&
        !event.shiftKey &&
        event.key.toLowerCase() === "u"
      ) {
        event.preventDefault();
        event.stopImmediatePropagation();
        insertCode();
        return;
      }
      if (
        (event.ctrlKey || event.metaKey) &&
        !event.altKey &&
        ["z", "y"].includes(event.key.toLowerCase())
      ) {
        event.preventDefault();
        event.stopImmediatePropagation();
        history?.record(editor.getValue());
        restoreHistory(
          event.key.toLowerCase() === "y" || event.shiftKey ? "redo" : "undo",
        );
      }
    },
    true,
  );
  $("editor").addEventListener("contextmenu", (event) => {
    const cell = event.target.closest(".vditor-ir td, .vditor-ir th");
    if (!cell || !active || mobileReadOnly()) return;
    event.stopImmediatePropagation();
    const table = cell.closest("table");
    const row = cell.parentElement.rowIndex,
      column = cell.cellIndex;
    const item = (title, command, danger = false) => ({
      title,
      danger,
      run: () => editTable(table, row, column, command),
    });
    menuAt(event, [
      item("上方插入行", "row-before"),
      item("下方插入行", "row-after"),
      item("左侧插入列", "column-before"),
      item("右侧插入列", "column-after"),
      null,
      item("列左对齐", "align-left"),
      item("列居中", "align-center"),
      item("列右对齐", "align-right"),
      null,
      item("清空单元格", "clear-cell"),
      item("删除当前行", "delete-row", true),
      item("删除当前列", "delete-column", true),
      item("删除表格…", "delete-table", true),
      null,
      ...textFormatItems(event, true),
    ]);
  });
  $("editor").addEventListener("contextmenu", (event) => {
    const link = event.target.closest("a");
    const path = link && attachmentPath(link.getAttribute("href") || "");
    if (!path || !active) return;
    event.stopImmediatePropagation();
    const full = `${active.path}/${path}`,
      name = link.textContent;
    menuAt(event, [
      { title: "打开附件", run: () => native("open", { path: full }) },
      {
        title: "另存为…",
        run: () => native("saveAttachment", { path: full, name }),
      },
    ]);
  });
  $("editor").addEventListener("contextmenu", (event) => {
    if (
      mobileReadOnly() ||
      !active ||
      !event.target.closest(".vditor-ir [contenteditable], .vditor-sv")
    )
      return;
    menuAt(event, textFormatItems(event));
  });
  new MutationObserver(() => {
    codeWork.prune();
    paintHistory();
    renderCodeMenus();
    applyMobileAccess();
    // Cached images are replaced within this microtask, before the browser fetches a library-relative URL.
    protect(renderImages)();
  }).observe($("editor"), {
    childList: true,
    characterData: true,
    subtree: true,
  });
  $("editor").addEventListener(
    "click",
    protect(async (event) => {
      const link = event.target.closest("a");
      if (!link) return;
      const href = link.getAttribute("href");
      event.preventDefault();
      event.stopPropagation();
      const path = attachmentPath(href || "");
      if (path && active)
        await native("open", { path: `${active.path}/${path}` });
      else if (/^https?:\/\//i.test(href || ""))
        await native("external", { url: href });
    }),
    true,
  );
}
function paintHistory() {
  if (!history || !editor) return;
  for (const [type, enabled] of [
    ["undo", history.canUndo],
    ["redo", history.canRedo],
  ]) {
    const button = $("editor").querySelector(`[data-type=${type}]`);
    if (button) {
      const disabled = !enabled;
      button.classList.toggle("vditor-menu--disabled", disabled);
      button.setAttribute("aria-disabled", String(disabled));
    }
  }
}
function restoreHistory(type) {
  if (!history || !editor || !active) return;
  if (
    (type === "undo" && !history.canUndo) ||
    (type === "redo" && !history.canRedo)
  )
    return;
  const value = type === "undo" ? history.undo() : history.redo();
  switching = true;
  editor.setValue(value, false);
  switching = false;
  history.normalizeCurrent(editor.getValue());
  dirty = true;
  store.setDraft(active.path, value);
  paintHistory();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(
    protect(async () => {
      await flush();
      await renderAttachments();
    }),
    150,
  );
}

function focusCode(block, offset) {
  const source = block?.querySelector("pre.vditor-ir__marker--pre code");
  if (!source) return;
  const root = block.closest("[contenteditable=true]");
  for (const expanded of root?.querySelectorAll(
    '[data-type="code-block"].vditor-ir__node--expand',
  ) || [])
    if (expanded !== block)
      expanded.classList.remove("vditor-ir__node--expand");
  // Reveal the destination before focusing it. Focusing a hidden source lets
  // WebKit choose a caret outside the code. Activation never writes content.
  block.classList.add("vditor-ir__node--expand");
  root?.focus({ preventScroll: true });
  const text = source.textContent;
  let remaining = Math.max(
    0,
    Math.min(
      offset ?? text.length - (text.endsWith("\n") ? 1 : 0),
      text.length,
    ),
  );
  const range = document.createRange();
  // Browsers may split text around a caret marker. Map the offset through all
  // text nodes, and use an element boundary for a genuinely empty code block.
  range.setStart(source, 0);
  const walker = document.createTreeWalker(source, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (remaining <= node.length) {
      range.setStart(node, remaining);
      break;
    }
    remaining -= node.length;
  }
  range.collapse(true);
  const selection = getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
  // Reveal the caret inside the code's own scroll area without moving the page.
  const caret = range.getBoundingClientRect();
  const body = source.getBoundingClientRect();
  if (caret.height) {
    const bottom = body.top + source.clientHeight - 12;
    const right = body.left + source.clientWidth - 16;
    if (caret.bottom > bottom) source.scrollTop += caret.bottom - bottom;
    else if (caret.top < body.top + 12)
      source.scrollTop -= body.top + 12 - caret.top;
    if (caret.right > right) source.scrollLeft += caret.right - right;
    else if (caret.left < body.left + 16)
      source.scrollLeft -= body.left + 16 - caret.left;
  }
  const viewport = block.closest(".vditor-ir").getBoundingClientRect();
  const header = (
    block.querySelector(".vditor-copy") || block
  ).getBoundingClientRect();
  // Clicking an already-visible header must not scroll a tall code body into view.
  if (header.bottom < viewport.top || header.top > viewport.bottom)
    block.scrollIntoView({ block: "start" });
}
function textFormatItems(event, compact = false) {
  const note = active,
    mode = editor.vditor.currentMode;
  const root = editor.vditor.ir.element,
    source = editor.vditor.sv.element;
  const selection = getSelection();
  const range = selection.rangeCount
    ? selection.getRangeAt(0).cloneRange()
    : null;
  const offsets = [source.selectionStart, source.selectionEnd];
  const insideCode = !!event.target.closest(
    '[data-type="code-block"], [data-type="img"]',
  );
  const restoreSelection = () => {
    if (active !== note || editor.vditor.currentMode !== mode) return false;
    if (mode === "sv") {
      source.focus();
      source.setSelectionRange(...offsets);
    } else if (
      range &&
      root.contains(range.startContainer) &&
      root.contains(range.endContainer)
    ) {
      root.focus({ preventScroll: true });
      selection.removeAllRanges();
      selection.addRange(range);
    } else return false;
    return true;
  };
  const run = (type, heading = null) => {
    if (!restoreSelection()) return;
    history?.record(editor.getValue());
    history?.seal();
    const button = heading
      ? $("editor").querySelector(`.vditor-toolbar [data-tag="h${heading}"]`)
      : $("editor").querySelector(`.vditor-toolbar [data-type="${type}"]`);
    button?.click();
  };
  const format = (title, type, heading = null) => ({
    title,
    disabled: insideCode,
    run: () => run(type, heading),
  });
  const selectedText =
    mode === "sv" ? source.value.slice(...offsets) : selection.toString();
  const clipboard = (title, operation, disabled = false) => ({
    title,
    disabled,
    run: async () => {
      if (active !== note || editor.vditor.currentMode !== mode) return;
      if (operation === "copy" || operation === "cut")
        await native("clipboard", { text: selectedText });
      const text =
        operation === "paste" ? await native("readClipboard", {}) : "";
      if (!restoreSelection() || operation === "copy") return;
      history?.record(editor.getValue());
      history?.seal();
      if (operation === "cut") document.execCommand("delete", false);
      else if (text) {
        const data = new DataTransfer();
        data.setData("text/plain", text);
        (mode === "sv" ? source : root).dispatchEvent(
          new ClipboardEvent("paste", {
            bubbles: true,
            cancelable: true,
            clipboardData: data,
          }),
        );
      }
      editor.vditor.options.input(editor.getValue());
    },
  });
  const items = [
    clipboard("复制", "copy", !selectedText),
    clipboard("剪切", "cut", !selectedText),
    clipboard("粘贴", "paste"),
    null,
    format("加粗", "bold"),
    format("斜体", "italic"),
    format("删除线", "strike"),
    format("行内代码", "inline-code"),
  ];
  if (compact) return items;
  items.push(
    null,
    {
      title: "标题…",
      disabled: insideCode,
      children: [
        "一级标题",
        "二级标题",
        "三级标题",
        "四级标题",
        "五级标题",
        "六级标题",
      ].map((title, index) => format(title, "headings", index + 1)),
    },
    null,
    format("无序列表", "list"),
    format("有序列表", "ordered-list"),
    format("任务/多选框", "check"),
    format("引用", "quote"),
    format("插入链接", "link"),
    format("代码块", "code"),
    null,
    {
      title: "撤销",
      disabled: !history?.canUndo,
      run: () => {
        history?.record(editor.getValue());
        restoreHistory("undo");
      },
    },
    {
      title: "重做",
      disabled: !history?.canRedo,
      run: () => restoreHistory("redo"),
    },
  );
  return items;
}
function applyDisplaySettings(value = {}) {
  enterBehavior = value?.enterBehavior === "line" ? "line" : "paragraph";
  const autoWrap = value?.autoWrap !== false;
  document.documentElement.dataset.autoWrap = String(autoWrap);
  const lineHeight = Math.min(
    2.5,
    Math.max(1, Number(value?.lineHeight) || 1.5),
  );
  const paragraphGap = Math.min(
    32,
    Math.max(
      0,
      Number.isFinite(Number(value?.paragraphGap))
        ? Number(value.paragraphGap)
        : 4,
    ),
  );
  document.documentElement.style.setProperty(
    "--notebook-line-height",
    lineHeight,
  );
  document.documentElement.style.setProperty(
    "--notebook-paragraph-gap",
    paragraphGap + "px",
  );
  return { lineHeight, paragraphGap, autoWrap, enterBehavior };
}
async function showDisplaySettings() {
  const initial = applyDisplaySettings(store.state.display);
  const dialog = document.createElement("dialog");
  dialog.id = "display-dialog";
  dialog.innerHTML = `<form method="dialog"><h2>选项</h2>
    <label>回车行为<select name="enterBehavior"><option value="paragraph">Enter 换段，Shift+Enter 换行（默认）</option><option value="line">Enter 换行，Shift+Enter 换段</option></select></label>
    <p class="muted">仅影响可编辑预览的普通正文；代码块、列表、表格和源码保留原有回车行为。</p>
    <label>行间距（倍）<input name="lineHeight" type="number" min="1" max="2.5" step="0.05" required></label>
    <label>段落额外间距（px）<input name="paragraphGap" type="number" min="0" max="32" step="1" required></label>
    <label class="inline"><input name="autoWrap" type="checkbox">自动换行</label>
    <p class="muted">代码块和表格内容尽量适应页面宽度；关闭后允许横向滚动。换行只影响显示，复制和保存仍保留原文。</p>
    <p class="muted">调整立即预览，保存后下次打开仍生效。</p>
    <div class="dialog-actions"><button type="button" data-reset>恢复默认</button><button type="button" data-cancel>取消</button><button class="primary" value="save">保存</button></div></form>`;
  const form = dialog.querySelector("form");
  const fields = form.elements;
  fields.lineHeight.value = initial.lineHeight;
  fields.paragraphGap.value = initial.paragraphGap;
  fields.autoWrap.checked = initial.autoWrap;
  fields.enterBehavior.value = initial.enterBehavior;
  const preview = () => {
    if (form.checkValidity())
      applyDisplaySettings({
        lineHeight: +fields.lineHeight.value,
        paragraphGap: +fields.paragraphGap.value,
        autoWrap: fields.autoWrap.checked,
        enterBehavior: fields.enterBehavior.value,
      });
  };
  form.oninput = preview;
  dialog.querySelector("[data-reset]").onclick = () => {
    fields.lineHeight.value = 1.5;
    fields.paragraphGap.value = 4;
    fields.autoWrap.checked = true;
    fields.enterBehavior.value = "paragraph";
    preview();
  };
  dialog.querySelector("[data-cancel]").onclick = () => dialog.close("cancel");
  document.body.append(dialog);
  dialog.showModal();
  await new Promise((resolve) => {
    dialog.onclose = resolve;
  });
  try {
    if (dialog.returnValue === "save") {
      const display = applyDisplaySettings({
        lineHeight: +fields.lineHeight.value,
        paragraphGap: +fields.paragraphGap.value,
        autoWrap: fields.autoWrap.checked,
        enterBehavior: fields.enterBehavior.value,
      });
      await store.serial(async () => {
        const previous = store.state.display;
        store.state.display = display;
        try {
          await store.put(".state.json", store.state);
        } catch (error) {
          store.state.display = previous;
          throw error;
        }
      });
    } else applyDisplaySettings(initial);
  } catch (error) {
    applyDisplaySettings(initial);
    throw error;
  } finally {
    dialog.remove();
  }
}
function focusTableCell(cell) {
  if (!cell) return;
  editor.vditor.ir.element.focus({ preventScroll: true });
  const range = document.createRange();
  range.selectNodeContents(cell);
  range.collapse(true);
  getSelection().removeAllRanges();
  getSelection().addRange(range);
}
async function insertTable() {
  if (!editor || !active) return;
  const note = active,
    mode = editor.vditor.currentMode;
  const source = editor.vditor.sv.element;
  const sourceSelection = [source.selectionStart, source.selectionEnd];
  const selection = getSelection();
  const saved = selection.rangeCount
    ? selection.getRangeAt(0).cloneRange()
    : null;
  const size = await chooseTableSize(
    $("editor").querySelector('.vditor-toolbar [data-type="table"]'),
  );
  if (!size || active !== note || editor.vditor.currentMode !== mode) return;
  history?.record(editor.getValue());
  history?.seal();
  if (mode === "sv") {
    source.focus();
    source.setSelectionRange(...sourceSelection);
  } else prepareAttachmentInsertion(saved);
  const before = new Set(editor.vditor.ir.element.querySelectorAll("table"));
  editor.insertMD(tableMarkdown(size.rows, size.columns));
  editor.vditor.options.input(editor.getValue());
  history?.seal();
  if (mode === "ir") {
    const table = [...editor.vditor.ir.element.querySelectorAll("table")].find(
      (table) => !before.has(table),
    );
    focusTableCell(table?.rows[0]?.cells[0]);
  }
}
async function editTable(table, row, column, command) {
  if (!active || !table.isConnected || editor.vditor.currentMode !== "ir")
    return;
  const note = active;
  const root = editor.vditor.ir.element;
  const index = [...root.querySelectorAll("table")].indexOf(table);
  const removesTable =
    command === "delete-table" ||
    (command === "delete-row" && table.rows.length === 1) ||
    (command === "delete-column" && table.rows[0].cells.length === 1);
  if (
    removesTable &&
    !(await choose(
      "删除表格",
      "将删除整张表格及其内容，可以撤销恢复。",
      "删除",
      "取消",
    ))
  )
    return;
  if (active !== note || !table.isConnected) return;
  history?.record(editor.getValue());
  history?.seal();
  const top = root.scrollTop;
  const target = changeTable(table, row, column, command);
  const markdown = editor.getValue();
  switching = true;
  editor.setValue(markdown, false);
  switching = false;
  editor.vditor.options.input(editor.getValue());
  history?.seal();
  root.scrollTop = top;
  if (target)
    focusTableCell(
      root.querySelectorAll("table")[index]?.rows[target.row]?.cells[
        target.column
      ],
    );
  else root.focus({ preventScroll: true });
}
function insertCode() {
  if (!editor || !active) return;
  history?.record(editor.getValue());
  history?.seal();
  editor.focus();
  const content = editor.getSelection() || "";
  const fence = "`".repeat(
    Math.max(3, ...[...content.matchAll(/`+/g)].map((x) => x[0].length + 1)),
  );
  // IR inserts a standalone block fragment. A leading separator newline is
  // meaningful to preserveBlankLines and becomes an unwanted empty paragraph.
  const blockMarkdown = `${fence}\n${content}${content.endsWith("\n") ? "" : "\n"}${fence}\n`;
  if (editor.vditor.currentMode === "sv") {
    const source = $("editor").querySelector(".vditor-sv");
    const cursor = source.selectionStart + fence.length + 2 + content.length;
    editor.insertMD("\n" + blockMarkdown);
    source.setSelectionRange(cursor, cursor);
    history?.seal();
    return;
  }
  const before = new Set(
    $("editor").querySelectorAll('[data-type="code-block"]'),
  );
  const selection = getSelection();
  const range = selection.rangeCount ? selection.getRangeAt(0) : null;
  const node = range?.startContainer;
  const paragraph = (
    node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement
  )?.closest("p");
  const emptyParagraph =
    range?.collapsed &&
    paragraph?.parentElement === editor.vditor.ir.element &&
    !paragraph.textContent.replace(/[\s\u200b]/g, "") &&
    !paragraph.querySelector(":not(br):not(wbr)")
      ? paragraph
      : null;
  const trailingLine = [];
  let trailingText;
  if (
    range?.collapsed &&
    !emptyParagraph &&
    paragraph?.parentElement === editor.vditor.ir.element
  ) {
    const tail = range.cloneRange();
    tail.setEnd(paragraph, paragraph.childNodes.length);
    const afterCaret = tail.cloneContents();
    // A terminal soft break may also have one native caret-padding BR. The
    // current empty line is replaced by the block, not kept before it.
    if (
      !afterCaret.textContent.replace(/[\s\u200b]/g, "") &&
      !afterCaret.querySelector(":not(br):not(wbr)") &&
      afterCaret.querySelectorAll("br").length <= 1
    ) {
      const children = [...paragraph.childNodes];
      const preceding = children.filter(
        (_, index) => range.comparePoint(paragraph, index + 1) <= 0,
      );
      const last = preceding
        .reverse()
        .find(
          (child) =>
            child.nodeName !== "WBR" &&
            (child.nodeType !== Node.TEXT_NODE ||
              child.textContent.includes("\n") ||
              child.textContent.replace(/[\s\u200b]/g, "")),
        );
      if (last?.nodeName === "BR")
        trailingLine.push(...children.slice(children.indexOf(last)));
      else if (last?.nodeType === Node.TEXT_NODE) {
        // WebKit's editable PRE uses LF text nodes where Chromium uses BRs.
        const newline = last.data.lastIndexOf("\n");
        if (
          newline >= 0 &&
          !last.data.slice(newline + 1).replace(/[\s\u200b]/g, "")
        ) {
          trailingText = { node: last, value: last.data.slice(0, newline) };
          trailingLine.push(...children.slice(children.indexOf(last) + 1));
        }
      }
    }
  }
  editor.insertMD(blockMarkdown);
  // Vditor inserts a block after the current paragraph. Consume only the empty
  // paragraph holding the caret; earlier user-authored blank lines stay intact.
  if (
    emptyParagraph?.isConnected &&
    !emptyParagraph.textContent.replace(/[\s\u200b]/g, "") &&
    !emptyParagraph.querySelector(":not(br):not(wbr)")
  ) {
    emptyParagraph.remove();
  }
  if (trailingLine.length || trailingText) {
    if (trailingText) trailingText.node.data = trailingText.value;
    trailingLine.forEach((child) => child.remove());
  }
  editor.vditor.options.input(editor.getValue());
  history?.seal();
  requestAnimationFrame(() => {
    const selected = getSelection().anchorNode?.parentElement?.closest(
      '[data-type="code-block"]',
    );
    const blocks = [
      ...$("editor").querySelectorAll('[data-type="code-block"]'),
    ];
    focusCode(
      selected || blocks.find((x) => !before.has(x)) || blocks.at(-1),
      content.length,
    );
  });
}
const codeMenus = new WeakMap();
const codeDetections = new Map();
const codeWork = new VisibleWork();
function deferredCodeRenders(markdown) {
  const languages = new Set(["", ...CODE_LANGUAGES]);
  for (const match of markdown.matchAll(/^\s{0,3}(?:`{3,}|~{3,})([^\s`~]*)/gm))
    languages.add(match[1]);
  return [...languages].map((language) => ({
    language,
    render: (preview) => {
      const code = preview.querySelector(":scope > code");
      if (!code) return;
      // Vditor's default copy renderer reads innerText then writes styles for
      // every block, forcing a full layout each time. Use source text instead.
      code.style.maxHeight = "400px";
      const menu = document.createElement("div");
      menu.className = "vditor-copy";
      code.before(menu);
      codeMenu(code, menu);
    },
  }));
}
function codeLanguage(text) {
  // Small shared detection cache, not an editor/document cache. Repeated menu
  // construction should not run highlightAuto over every language again.
  const key = text.length <= 4096 ? text : null;
  const cached = key !== null && codeDetections.get(key);
  if (cached && cached.highlighter === window.hljs) {
    codeDetections.delete(key);
    codeDetections.set(key, cached);
    return cached.language;
  }
  const language = detectCodeLanguage(text, window.hljs);
  if (key !== null) {
    codeDetections.set(key, { language, highlighter: window.hljs });
    if (codeDetections.size > 32)
      codeDetections.delete(codeDetections.keys().next().value);
  }
  return language;
}
function renderCodeMenus() {
  for (const block of $("editor").querySelectorAll(
    '.vditor-ir__node[data-type="code-block"]',
  )) {
    const preview = block.querySelector(".vditor-ir__preview");
    const code = preview?.querySelector(":scope > code");
    if (!code) continue;
    const source = block.querySelector(".vditor-ir__marker--pre code");
    const info = block.querySelector('[data-type="code-block-info"]');
    const signature = JSON.stringify([info?.textContent, source?.textContent]);
    let menu = preview.querySelector(".vditor-copy");
    if (menu && codeMenus.get(menu) === signature) continue;
    if (!menu) {
      menu = document.createElement("div");
      menu.className = "vditor-copy";
      code.before(menu);
      // Empty blocks skip Vditor's renderer. Mark the menu as already rendered.
      code.style.maxHeight = "400px";
    }
    codeMenu(code, menu);
  }
}
function codeMenu(code, menu) {
  menu.replaceChildren();
  menu.contentEditable = "false";
  // Prevent the editor's preview click handler and browser focus from moving the caret.
  for (const event of ["pointerdown", "mousedown"])
    menu["on" + event] = (e) => {
      e.stopPropagation();
      if (e.target.closest("button")) e.preventDefault();
    };
  menu.onclick = (e) => e.stopPropagation();
  const block = code.closest('.vditor-ir__node[data-type="code-block"]');
  const source = block?.querySelector("pre.vditor-ir__marker--pre code");
  const info = block?.querySelector('[data-type="code-block-info"]');
  if (source) source.style.maxHeight = code.style.maxHeight || "400px";
  codeMenus.set(menu, JSON.stringify([info?.textContent, source?.textContent]));
  const explicit = info
    ? info.textContent.replace(/\u200b/g, "").trim()
    : code.className.match(/language-([\w+-]+)/)?.[1] || "";
  const automatic = "txt";
  const language = document.createElement("select");
  language.className = "code-language";
  language.setAttribute("aria-label", "代码块语言");
  language.title = "选择代码语言；自动识别不确定时使用 txt";
  for (const value of [
    "",
    ...CODE_LANGUAGES,
    ...(explicit && !CODE_LANGUAGES.includes(explicit) ? [explicit] : []),
  ]) {
    const option = label(value ? value : `自动（${automatic}）`, "option");
    option.value = value;
    language.append(option);
  }
  language.value = explicit;
  language.disabled = !info;
  let cursor;
  language.onpointerdown = () => {
    const selection = getSelection();
    if (
      source &&
      selection.rangeCount &&
      source.contains(selection.anchorNode)
    ) {
      const range = selection.getRangeAt(0).cloneRange();
      range.selectNodeContents(source);
      range.setEnd(selection.anchorNode, selection.anchorOffset);
      cursor = range.toString().length;
    }
  };
  language.onchange = () => {
    history?.record(editor.getValue());
    history?.seal();
    const index = [
      ...$("editor").querySelectorAll('[data-type="code-block"]'),
    ].indexOf(block);
    info.textContent = "\u200b" + language.value;
    const markdown = editor.getValue();
    switching = true;
    editor.setValue(markdown, false);
    switching = false;
    editor.vditor.options.input(editor.getValue());
    focusCode(
      $("editor").querySelectorAll('[data-type="code-block"]')[index],
      cursor,
    );
  };
  codeWork.schedule(block || code, () => {
    if (
      !menu.isConnected ||
      codeMenus.get(menu) !==
        JSON.stringify([info?.textContent, source?.textContent])
    )
      return;
    const text = source?.textContent ?? code.textContent;
    const automatic = explicit ? "txt" : codeLanguage(text);
    language.options[0].textContent = `自动（${automatic}）`;
    const detected = explicit || automatic;
    if (window.hljs?.getLanguage(detected)) {
      try {
        code.innerHTML = window.hljs.highlight(text, {
          language: detected,
          ignoreIllegals: true,
        }).value;
        code.classList.add("hljs");
      } catch {}
    }
  });
  const copy = document.createElement("button");
  copy.type = "button";
  copy.textContent = "复制";
  copy.setAttribute("aria-label", "复制原始代码");
  copy.onclick = protect(async (event) => {
    event.preventDefault();
    event.stopPropagation();
    const clone = (source || code).cloneNode(true);
    clone
      .querySelectorAll(".highlight-ln,.hljs-ln-numbers")
      .forEach((x) => x.remove());
    // Vditor includes the line ending before the closing Markdown fence.
    // Remove only that structural ending; preserve intentional blank lines,
    // indentation and trailing spaces in the code itself.
    const text = clone.textContent.replace(/(?:\r\n|\n|\r)$/, "");
    await native("clipboard", { text });
    copy.textContent = "已复制";
    setTimeout(() => (copy.textContent = "复制"), 1500);
  });
  const popout = document.createElement("button");
  popout.type = "button";
  popout.className = "code-popout";
  popout.textContent = "弹出编辑";
  popout.setAttribute("aria-label", "在独立窗口编辑代码块");
  // Native hosts own the separate document window. PWA editing stays inline.
  popout.hidden = settings.platform === "web";
  popout.disabled = !source || mobileReadOnly();
  popout.onclick = protect(async (event) => {
    event.preventDefault();
    event.stopPropagation();
    await flush();
    const note = active;
    const original = source.textContent;
    const text = original.replace(/\n$/, "");
    editingCode = true;
    document.body.inert = true;
    let result;
    try {
      result = await native("editCode", {
        text,
        language: explicit,
        dark: settings.theme === "Dark",
        wrap: document.documentElement.dataset.autoWrap !== "false",
      });
    } finally {
      editingCode = false;
      document.body.inert = false;
    }
    const editedText = typeof result === "string" ? result : result?.text;
    const editedLanguage =
      typeof result?.language === "string" &&
      (CODE_LANGUAGES.includes(result.language) ||
        result.language === "" ||
        result.language === explicit)
        ? result.language
        : explicit;
    if (
      typeof editedText !== "string" ||
      (editedText === text && editedLanguage === explicit)
    )
      return;
    if (
      active !== note ||
      !source.isConnected ||
      source.textContent !== original
    ) {
      // Keep the returned draft available instead of overwriting a changed note.
      await recoverCodeDraft(editedText);
      return;
    }
    history?.record(editor.getValue());
    history?.seal();
    setCodeText(source, editedText.replace(/\r\n?/g, "\n"));
    if (info) info.textContent = "\u200b" + editedLanguage;
    editor.vditor.options.input(editor.getValue());
    history?.seal();
    focusCode(block);
    await flush();
  });
  menu.append(language, popout, copy);
}

async function recoverCodeDraft(text) {
  const dialog = document.createElement("dialog");
  const title = label("原笔记已变化，请复制并保留这份代码", "h2");
  const area = document.createElement("textarea");
  area.value = text;
  area.style.cssText = "width:70vw;height:60vh;font-family:monospace";
  const close = label("关闭", "button");
  close.onclick = () => dialog.close();
  dialog.append(title, area, close);
  document.body.append(dialog);
  dialog.showModal();
  await new Promise((resolve) =>
    dialog.addEventListener("close", resolve, { once: true }),
  );
  dialog.remove();
}
const imageCache = new Map(),
  imageLoads = new Map(),
  imageWaiters = [];
let imageReads = 0;
async function loadImage(note, path) {
  const full = `${note.path}/${path}`,
    version = selectionVersion;
  if (imageCache.has(full)) return;
  const key = `${version}:${full}`;
  if (imageLoads.has(key)) return imageLoads.get(key);
  const task = (async () => {
    if (imageReads >= 3)
      await new Promise((resolve) => imageWaiters.push(resolve));
    else imageReads++;
    try {
      if (version !== selectionVersion || active?.path !== note.path) return;
      const data = await io.readBinary(full).catch((error) => {
        if (settings.platform === "web") return null;
        throw error;
      });
      if (data && version === selectionVersion && active?.path === note.path)
        imageCache.set(full, `data:${imageType(path)};base64,${data}`);
    } finally {
      const next = imageWaiters.shift();
      if (next) next();
      else imageReads--;
    }
  })();
  imageLoads.set(key, task);
  try {
    await task;
  } finally {
    imageLoads.delete(key);
  }
}
const imageType = (path) =>
  /\.svg$/i.test(path)
    ? "image/svg+xml"
    : /\.jpe?g$/i.test(path)
      ? "image/jpeg"
      : /\.gif$/i.test(path)
        ? "image/gif"
        : /\.webp$/i.test(path)
          ? "image/webp"
          : /\.avif$/i.test(path)
            ? "image/avif"
            : /\.bmp$/i.test(path)
              ? "image/bmp"
              : "image/png";
async function warmImages(
  note,
  markdown,
  current = () => active?.path === note.path,
) {
  const paths = [...references(markdown)].filter((path) =>
    /\.(png|jpe?g|svg|gif|webp|avif|bmp)$/i.test(path),
  );
  let index = 0;
  // Bounded concurrency avoids retaining multiple notes or flooding the native
  // bridge with all large image buffers at once.
  await Promise.all(
    Array.from({ length: Math.min(3, paths.length) }, async () => {
      while (current() && index < paths.length) {
        const path = paths[index++],
          full = `${note.path}/${path}`;
        if (imageCache.has(full)) continue;
        await loadImage(note, path);
      }
    }),
  );
}
async function renderImages() {
  if (!active) return;
  const note = active;
  for (const image of $("editor").querySelectorAll("img")) {
    const raw = image.dataset.notebookReference || image.getAttribute("src");
    const path = attachmentPath(raw || "");
    if (!path) continue;
    const full = `${note.path}/${path}`;
    if (!imageCache.has(full)) {
      await loadImage(note, path);
    }
    // IR keeps Markdown in marker spans; only its display image is rewritten.
    if (
      active?.path === note.path &&
      image.closest(
        '.vditor-ir__node[data-type="img"],.vditor-ir__preview,.vditor-preview',
      )
    ) {
      image.dataset.notebookReference = raw;
      if (imageCache.has(full) && image.src !== imageCache.get(full))
        image.src = imageCache.get(full);
    }
  }
}
// Attachment imports must never become hidden Markdown inside a code block.
// Keep a valid text caret; insert after code when the last edit was in code.
function prepareAttachmentInsertion(savedRange = null) {
  if (editor.vditor.currentMode === "sv") {
    editor.focus();
    return;
  }
  const root = editor.vditor.ir.element;
  const selection = getSelection();
  const candidate =
    savedRange ||
    (selection.rangeCount ? selection.getRangeAt(0).cloneRange() : null);
  const range =
    candidate &&
    root.contains(candidate.startContainer) &&
    root.contains(candidate.endContainer)
      ? candidate
      : document.createRange();
  const container =
    range.startContainer.nodeType === Node.ELEMENT_NODE
      ? range.startContainer
      : range.startContainer.parentElement;
  const code = root.contains(container)
    ? container.closest('[data-type="code-block"]')
    : null;
  if (code || range !== candidate) {
    const paragraph = document.createElement("p");
    paragraph.dataset.block = "0";
    paragraph.append(document.createElement("br"));
    if (code) code.after(paragraph);
    else root.append(paragraph);
    range.selectNodeContents(paragraph);
    range.collapse(true);
  } else range.collapse(false);
  root.focus({ preventScroll: true });
  selection.removeAllRanges();
  selection.addRange(range);
}
async function attachFiles(files) {
  if (!active || !editor) throw new Error("请先创建或选择一篇笔记");
  const note = active;
  // Keep the insertion point while awaiting native file writes.
  const selection = getSelection();
  const range = selection?.rangeCount
    ? selection.getRangeAt(0).cloneRange()
    : null;
  const links = [];
  for (const file of files) {
    const data = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result).split(",")[1]);
      reader.onerror = reject;
      reader.readAsDataURL(file);
    });
    const attachment = await store.importAttachment(note, file.name, data);
    const isImage =
      file.type.startsWith("image/") ||
      /\.(png|jpe?g|gif|svg|webp|avif|bmp)$/i.test(file.name);
    if (isImage)
      imageCache.set(
        `${note.path}/${attachment.path}`,
        `data:${imageType(attachment.path)};base64,${data}`,
      );
    links.push(attachmentLink(attachment.name, attachment.filename, isImage));
  }
  if (active?.path !== note.path)
    throw new Error("笔记已切换，文件已安全保存到原笔记，请从附件面板插入");
  prepareAttachmentInsertion(range);
  editor.insertMD(links.join("\n") + "\n");
  dirty = true;
  await flush();
  await renderAttachments();
}
let attachmentRenderVersion = 0;
async function renderAttachments() {
  const version = ++attachmentRenderVersion;
  if (!active) return;
  const note = active;
  const { state, problems } = await store.serial(() =>
    store.reconcile(note.path, false),
  );
  if (active?.path !== note.path) return;
  if (version !== attachmentRenderVersion) return;
  $("attachment-scope").textContent = note.title;
  $("attachment-list").replaceChildren();
  $("attachments").querySelector("span").textContent =
    `附件 ${Object.keys(state.entries).length}`;
  for (const [filename, entry] of Object.entries(state.entries)) {
    const status = entry.trashSince
      ? "trash"
      : entry.unusedSince != null
        ? "pending"
        : "live";
    if (status !== attachmentTab) continue;
    const row = document.createElement("div");
    row.className = "attachment-row";
    const details = document.createElement("div");
    details.className = "details";
    details.append(
      label(entry.name, "strong"),
      label(
        entry.trashSince
          ? `引用已移除 · ${date(entry.trashSince)}`
          : entry.unusedSince
            ? "待回收 · 保护期内"
            : "正文正在引用",
        "small",
      ),
    );
    const action = document.createElement("button");
    action.textContent = entry.trashSince ? "恢复并插入" : "插入正文";
    action.onclick = protect(async () => {
      const markdown = await store.restoreAttachment(note, filename);
      prepareAttachmentInsertion();
      editor.insertMD(markdown + "\n");
      dirty = true;
      await flush();
      await renderAttachments();
    });
    const open = document.createElement("button");
    open.textContent = "打开";
    open.onclick = protect(() =>
      native("open", {
        path: `${note.path}/${entry.trashSince ? ".trash" : "reference"}/${filename}`,
      }),
    );
    row.append(icon("file"), details);
    if (entry.trashSince) {
      const remaining = label(
        `剩余 ${Math.max(0, Math.ceil((15 * DAY - Date.now() + entry.trashSince) / DAY))} 天`,
      );
      remaining.className = "remaining";
      row.append(remaining);
    }
    const save = document.createElement("button");
    save.textContent = "另存为";
    save.onclick = protect(() =>
      native("saveAttachment", {
        path: `${note.path}/${entry.trashSince ? ".trash" : "reference"}/${filename}`,
        name: entry.name,
      }),
    );
    row.append(open, save, action);
    if (entry.unusedSince && !entry.trashSince) {
      const recycle = document.createElement("button");
      recycle.textContent = "立即回收";
      recycle.onclick = protect(async () => {
        await flush();
        await store.recycleAttachment(note, filename);
        await renderAttachments();
      });
      row.append(recycle);
    }
    if (entry.trashSince) {
      const remove = document.createElement("button");
      remove.textContent = "永久删除";
      remove.className = "danger";
      remove.onclick = protect(async () => {
        if (
          !(await choose(
            "永久删除附件",
            `将彻底删除“${entry.name}”，此操作无法撤销。`,
            "永久删除",
            "取消",
          ))
        )
          return;
        await flush();
        await store.purgeAttachment(note, filename);
        await renderAttachments();
      });
      row.append(remove);
    }

    $("attachment-list").append(row);
  }
  if (problems.length)
    $("attachment-list").append(label(problems.join("；"), "p"));
  icons();
}
function retrySync(error) {
  const delay =
    Math.max(
      error.retryAfter || 0,
      Math.min(120000, 5000 * 2 ** Math.min(syncRetryAttempt++, 5)),
    ) +
    Math.random() * 1000;
  syncRetryTimer = setTimeout(() => {
    if (
      settings.platform === "web" &&
      (document.visibilityState !== "visible" || !navigator.onLine)
    )
      return;
    protect(sync)();
  }, delay);
  return Math.ceil(delay / 1000);
}
async function showSyncReport() {
  const report = lastSyncReport || (await store.json(".sync-report.json"));
  $("sync-report-summary").textContent = report
    ? `已同步 ${report.completed} 项 · ${report.failed} 项未完成 · ${date(report.at)}`
    : "还没有同步结果";
  $("sync-report-list").replaceChildren();
  for (const item of (report?.items || [])
    .slice()
    .sort((a, b) => Number(a.complete) - Number(b.complete))) {
    const row = document.createElement("article");
    row.dataset.complete = String(item.complete);
    const entity = (item.type === "note" ? catalog.notes : catalog.books).find(
      (x) => x.id === item.id,
    );
    row.append(
      label(
        `${{ note: "笔记", book: "笔记本", scope: "目录", library: "笔记库" }[item.type]} · ${entity?.title || item.title || item.id}`,
        "strong",
      ),
    );
    row.append(
      label(item.complete ? "完整同步" : "未完成 · 本地内容已保留", "p"),
    );
    for (const error of item.errors) {
      row.append(label(error.message, "p"));
      row.append(label(error.path, "p"));
    }
    $("sync-report-list").append(row);
  }
  $("sync-report-conflicts").hidden =
    !lastSyncEngine || !report?.conflicts?.length;
  $("sync-report-dialog").showModal();
}
async function sync() {
  if (syncing || configuring || editingCode || !settings.syncEnabled) return;
  clearTimeout(syncRetryTimer);
  syncing = true;
  try {
    await flush();
    $("sync-status").textContent = "正在同步…";
    const browserIO = window.notebookBrowser?.io;
    if (settings.platform === "web" && !navigator.onLine) {
      $("sync-status").textContent = "离线 · 修改已保存在本机";
      return;
    }
    const remote = {
      request: (method, path, data, headers) =>
        native("http", { method, path, data, headers }),
      list: async (request) => {
        const listing = await listRemoteFiles(
          request,
          settings.url,
          (visited) => {
            $("sync-status").textContent = `正在扫描 · ${visited} 个目录`;
          },
        );
        if (browserIO) await store.serial(() => browserIO.inventory(listing));
        return listing;
      },
    };
    const engine = new WebDAVSync(
      store,
      remote,
      ({ current, total, phase, status, attempt, retries }) => {
        $("sync-status").textContent =
          phase === "retry"
            ? `${status === 423 ? "网盘文件暂时被锁定" : "网络暂时不可用"} · 正在重试 ${attempt}/${retries}`
            : `正在同步 · ${current}/${total}`;
      },
      browserIO ? (path) => browserIO.wanted(path) : undefined,
    );
    lastSyncEngine = engine;
    const result = await engine.run();
    lastSyncReport = result;
    if (browserIO) await store.serial(() => browserIO.finishSync());
    if (result.failed) {
      const errors = result.items.flatMap((x) => x.errors);
      const retryable = errors.filter((x) => x.retryable);
      const seconds = retryable.length
        ? retrySync({
            retryAfter: Math.max(...retryable.map((x) => x.retryAfter)),
          })
        : null;
      $("sync-status").textContent =
        `已同步 ${result.completed} 项 · ${result.failed} 项未完成` +
        (seconds ? ` · ${seconds} 秒后重试` : "");
      $("sync-status").title = errors.map((x) => x.message).join("\n");
      $("remote-status").textContent = "部分同步 · 点击查看结果";
    } else {
      syncRetryAttempt = 0;
      $("sync-status").textContent = result.warning
        ? "文件已同步 · 同步锁待释放"
        : "Nextcloud 已同步";
      $("sync-status").title = result.warning || "";
      $("remote-status").textContent = "已同步 · " + date(Date.now());
    }
    imageCache.clear();
    if (active) {
      await warmImages(active, editor.getValue());
      await renderImages();
    }
    const selected = active?.id;
    await refresh();
    const fresh = catalog.notes.find((x) => x.id === selected);
    if (
      fresh &&
      !dirty &&
      (fresh.path !== active?.path || fresh.markdown !== active?.markdown)
    )
      await selectNote(fresh);
    else if (fresh && active) {
      active.title = fresh.title;
      active.bookId = fresh.bookId;
    } else if (active && !fresh && !dirty) {
      active = null;
      $("editor").hidden = true;
      $("empty").hidden = false;
    }
  } catch (error) {
    lastSyncReport = {
      at: Date.now(),
      completed: 0,
      failed: 1,
      conflicts: [],
      items: [
        {
          id: "library",
          type: "library",
          title: "笔记库",
          complete: false,
          errors: [
            {
              path: error.path || "",
              message: error.message,
              code: error.code || "SYNC_ERROR",
            },
          ],
        },
      ],
    };
    await store.serial(() => store.put(".sync-report.json", lastSyncReport));
    if (
      ["SYNC_BUSY", "SYNC_RETRY", "DAV_LOCKED", "SYNC_LEASE_LOST"].includes(
        error.code,
      )
    ) {
      const seconds = retrySync(error);
      $("sync-status").textContent =
        `${error.code === "SYNC_BUSY" ? "等待另一台设备" : error.code === "DAV_LOCKED" ? "网盘文件暂时被锁定" : "网络暂时不可用"} · ${seconds} 秒后重试`;
      $("sync-status").title = error.message;
      return;
    }
    $("sync-status").textContent = "同步未完成";
    $("sync-status").title = error.message;
    throw error;
  } finally {
    syncing = false;
  }
}
function conflictRow(id) {
  return [...$("conflict-list").children].find(
    (row) => row.dataset.conflictId === id,
  );
}
function updateConflictRow(id, message, error = false) {
  const row = conflictRow(id);
  if (!row) return;
  const pending = pendingConflictResolutions.get(id);
  row.setAttribute("aria-busy", String(!!pending));
  for (const button of row.querySelectorAll("button"))
    button.disabled =
      !!pending ||
      (button.dataset.comparison === "true" && !row.dataset.loaded);
  const merged = row.querySelector('[aria-label="合并后的正文"]');
  if (merged) merged.readOnly = !!pending;
  if (message !== undefined) {
    const status = row.querySelector(".conflict-status");
    status.textContent = message;
    status.dataset.error = String(error);
    if (error && $("conflict-dialog").open)
      status.scrollIntoView({ block: "nearest" });
  }
}
function showConflicts(engine, conflicts) {
  $("conflict-list").replaceChildren();
  for (const entry of conflicts) {
    const conflict = { ...entry };
    const row = document.createElement("div");
    row.dataset.conflictId = conflict.id;
    row.append(label(conflict.path, "p"));
    const status = label("请选择保留的版本，或展开对照后合并。", "p");
    status.className = "conflict-status";
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");
    const perform = async (message, work) => {
      if (pendingConflictResolutions.has(conflict.id)) return;
      pendingConflictResolutions.set(conflict.id, message);
      updateConflictRow(conflict.id, message);
      try {
        await work();
      } catch (error) {
        updateConflictRow(conflict.id, error.message, true);
        if (!$("conflict-dialog").open) toast(error.message);
      } finally {
        pendingConflictResolutions.delete(conflict.id);
        updateConflictRow(conflict.id);
      }
    };
    const resolved = async () => {
      await refresh();
      const fresh = catalog.notes.find((x) => x.id === active?.id);
      if (fresh && !dirty) await selectNote(fresh);
      else if (active && !fresh && !dirty) await clearSelection();
      conflictRow(conflict.id)?.remove();
      if (!$("conflict-list").children.length) $("conflict-dialog").close();
      // Finish the whole note (metadata and attachments), then refresh the
      // report. Resolving main.md alone must not leave a stale failed item.
      await protect(sync)();
    };
    for (const [text, local] of [
      ["保留本地", true],
      ["采用在线", false],
    ]) {
      const button = document.createElement("button");
      button.textContent = text;
      button.onclick = () =>
        perform(`正在处理“${text}”…`, async () => {
          await flush();
          await engine.resolve(conflict, local);
          await resolved();
        });
      row.append(button);
    }
    let loadComparison;
    let details;
    if (conflict.path.endsWith("/main.md")) {
      details = document.createElement("details");
      details.append(label("查看并手动合并正文", "summary"));
      const local = document.createElement("textarea"),
        remote = document.createElement("textarea");
      local.readOnly = remote.readOnly = true;
      local.setAttribute("aria-label", "冲突时的本地正文");
      remote.setAttribute("aria-label", "冲突时的在线正文");
      const merged = document.createElement("textarea");
      merged.setAttribute("aria-label", "合并后的正文");
      details.append(
        label("当前本地版本", "p"),
        local,
        label("在线版本", "p"),
        remote,
        label("合并后的正文（可编辑）", "p"),
        merged,
      );
      loadComparison = async () => {
        const [localText, remoteText] = await Promise.all([
          io.read(conflict.path),
          io.read(`.conflicts/${conflict.id}/remote`),
        ]);
        local.value = localText ?? "（本地已删除）";
        remote.value = remoteText ?? "（在线已删除）";
        // Re-reading versions must preserve an unfinished manual merge.
        if (!row.dataset.loaded) merged.value = localText ?? "";
        row.dataset.loaded = "true";
        updateConflictRow(conflict.id);
      };
      details.ontoggle = async () => {
        if (
          !details.open ||
          row.dataset.loaded ||
          pendingConflictResolutions.has(conflict.id)
        )
          return;
        try {
          await loadComparison();
        } catch (error) {
          updateConflictRow(conflict.id, error.message, true);
        }
      };
      const commit = document.createElement("button");
      commit.textContent = "保存合并正文并同步";
      commit.dataset.comparison = "true";
      commit.disabled = true;
      commit.onclick = () =>
        perform("正在保存合并正文并同步…", async () => {
          const markdown = merged.value;
          await flush();
          await engine.resolve(conflict, true, markdown);
          await resolved();
        });
      details.append(commit);
    }
    const reload = document.createElement("button");
    reload.textContent = "重新读取版本";
    reload.onclick = () =>
      perform("正在读取最新版本…", async () => {
        await flush();
        const current = await engine.review(conflict);
        if (!current) {
          await resolved();
          return;
        }
        Object.assign(conflict, current);
        if (loadComparison) {
          await loadComparison();
          details.open = true;
        }
        updateConflictRow(
          conflict.id,
          "已读取最新版本，请比较后选择。未完成的合并正文已保留。",
        );
      });
    row.append(reload, status);
    if (details) row.append(details);
    $("conflict-list").append(row);
    const pending = pendingConflictResolutions.get(conflict.id);
    updateConflictRow(conflict.id, pending || undefined);
  }
  $("conflict-dialog").showModal();
}

async function maintenance() {
  if (configuring || editingCode) return;
  await flush();
  if (settings.syncEnabled && !syncing && settings.platform !== "web")
    await protect(sync)();
  const report = await store.maintenance(settings.syncEnabled);
  const issues = report.flatMap((x) => x.problems);
  $("maintenance-status").textContent = issues.length
    ? `${issues.length} 个附件问题`
    : "附件巡检正常";
  if (active) await renderAttachments();
}
async function configure() {
  $("dav-url").value = settings.url || "";
  $("dav-user").value = settings.user || "";
  $("dav-password").value = "";
  $("dav-enabled").checked = settings.syncEnabled;
  $("grace").value = store.grace / 1000;
  $("sync-error").textContent = "";
  $("sync-dialog").showModal();
}
function applyTheme() {
  document.documentElement.dataset.theme =
    settings.theme === "Dark" ? "dark" : "light";
  document
    .querySelector('meta[name="theme-color"]')
    ?.setAttribute(
      "content",
      settings.theme === "Dark" ? "#111b2b" : "#f8fbff",
    );
  editor?.setTheme(
    settings.theme === "Dark" ? "dark" : "classic",
    settings.theme === "Dark" ? "dark" : "light",
    settings.theme === "Dark" ? "github-dark" : "github",
    new URL("vendor/vditor/dist/css/content-theme", location.href).href,
  );
}

function leaveTrash() {
  $("workspace").classList.remove("trash-view");
  $("trash-page").hidden = true;
  $("trash").classList.remove("active");
}
async function showTrash() {
  await flush();
  await store.tail;
  $("workspace").classList.add("trash-view");
  $("trash-page").hidden = false;
  $("trash").classList.add("active");
  await renderTrash();
}
async function renderTrash() {
  $("deleted-list").replaceChildren();
  let count = 0;
  const addRow = (title, kind, timestamp, restore, purge) => {
    count++;
    const row = document.createElement("div");
    row.className = "trash-row";
    const text = document.createElement("div");
    text.className = "trash-row-text";
    text.append(
      label(title, "strong"),
      label(`${kind} · 删除于 ${date(timestamp)}`, "p"),
    );
    const controls = document.createElement("div");
    controls.className = "trash-row-actions";
    const recover = document.createElement("button");
    recover.textContent =
      kind === "笔记本"
        ? "恢复笔记本及笔记"
        : kind === "附件"
          ? "恢复并插入笔记"
          : "恢复笔记";
    recover.onclick = protect(async () => {
      await restore();
      await refresh();
      await renderTrash();
    });
    const remove = document.createElement("button");
    remove.textContent = "永久删除";
    remove.className = "danger";
    remove.onclick = protect(async () => {
      if (
        !(await choose(
          "永久删除",
          `将彻底删除“${title}”及其内容。此操作无法撤销，并会在下次同步时传播到网盘。`,
          "永久删除",
          "取消",
        ))
      )
        return;
      remove.disabled = true;
      try {
        await purge();
        await refresh();
        await renderTrash();
      } finally {
        remove.disabled = false;
      }
    });
    controls.append(recover, remove);
    row.append(text, controls);
    $("deleted-list").append(row);
  };
  for (const row of await store.deletedBooks())
    addRow(
      row.title,
      "笔记本",
      row.at,
      () => store.restoreBook(row),
      () => store.purgeDeleted(row),
    );
  for (const row of await store.deletedNotes())
    addRow(
      row.title,
      "笔记",
      row.at,
      () => store.restoreNote(row),
      () => store.purgeDeleted(row),
    );
  for (const note of (await store.catalog()).notes) {
    const state = await store.json(`${note.path}/.attachments.json`, {
      entries: {},
    });
    const files = await io.list(`${note.path}/.trash`);
    for (const file of files.filter(
      (x) => !x.dir && !x.name.endsWith(".bak"),
    )) {
      const entry = state.entries[file.name];
      if (!entry?.trashSince) continue;
      addRow(
        `${entry.name} · ${note.title}`,
        "附件",
        entry.trashSince,
        async () => {
          const link = await store.restoreAttachment(note, file.name);
          const current = await io.read(`${note.path}/main.md`);
          note.markdown = current;
          await store.save(note, current + "\n\n" + link + "\n");
          if (active?.id === note.id) {
            active.markdown = note.markdown;
            switching = true;
            editor.setValue(note.markdown, false);
            switching = false;
          }
        },
        () => store.purgeAttachment(note, file.name),
      );
    }
  }
  if (!count) {
    const empty = label("回收站为空", "p");
    empty.className = "trash-empty";
    $("deleted-list").append(empty);
  }
}
let contextMenu;
function closeContextMenu() {
  contextMenu?.remove();
  contextMenu = null;
}
function menuAt(event, items) {
  event.preventDefault();
  closeContextMenu();
  const menu = document.createElement("div");
  menu.className = "context-menu";
  menu.setAttribute("role", "menu");
  const origin = event.currentTarget;
  for (const item of items) {
    if (!item) {
      menu.append(document.createElement("hr"));
      continue;
    }
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = item.title;
    button.setAttribute("role", "menuitem");
    button.disabled = !!item.disabled;
    if (item.danger) button.className = "danger";
    button.onclick = protect(async () => {
      closeContextMenu();
      if (item.children) {
        menuAt(
          {
            preventDefault() {},
            currentTarget: origin,
            clientX: parseFloat(menu.style.left),
            clientY: parseFloat(menu.style.top),
          },
          item.children,
        );
      } else await item.run();
    });
    menu.append(button);
  }
  document.body.append(menu);
  contextMenu = menu;
  const rect = origin.getBoundingClientRect();
  menu.style.left =
    Math.max(
      8,
      Math.min(event.clientX || rect.left, innerWidth - menu.offsetWidth - 8),
    ) + "px";
  menu.style.top =
    Math.max(
      8,
      Math.min(
        event.clientY || rect.bottom,
        innerHeight - menu.offsetHeight - 8,
      ),
    ) + "px";
  menu.querySelector("button:not(:disabled)")?.focus();
  menu.onkeydown = (key) => {
    if (key.key === "Escape") {
      closeContextMenu();
      origin.focus();
    }
    if (["ArrowDown", "ArrowUp", "Home", "End"].includes(key.key)) {
      key.preventDefault();
      const buttons = [...menu.querySelectorAll("button:not(:disabled)")];
      const index = buttons.indexOf(document.activeElement);
      const next =
        key.key === "Home"
          ? 0
          : key.key === "End"
            ? buttons.length - 1
            : (index + (key.key === "ArrowDown" ? 1 : -1) + buttons.length) %
              buttons.length;
      buttons[next]?.focus();
    }
  };
}
document.addEventListener("pointerdown", (event) => {
  if (!contextMenu?.contains(event.target)) closeContextMenu();
});
window.addEventListener("blur", closeContextMenu);
window.addEventListener("resize", closeContextMenu);
async function clearSelection() {
  selectionVersion++;
  active = null;
  dirty = false;
  imageCache.clear();
  $("editor").hidden = true;
  $("empty").hidden = false;
  $("attachment-drawer").hidden = true;
  $("breadcrumb").textContent = "选择或新建笔记";
  $("note-toolbar")
    .querySelectorAll("button")
    .forEach((x) => (x.disabled = true));
}
function bookMenu(event, book) {
  menuAt(event, [
    {
      title: "新建笔记",
      run: async () => {
        await flush();
        bookId = book.id;
        await refresh();
        $("new-note").click();
      },
    },
    {
      title: "重命名",
      disabled: book.id === "quick-notes",
      run: async () => {
        const title = await ask("重命名笔记本", book.title);
        if (title?.trim()) {
          await store.renameBook(book, title.trim());
          await refresh();
          if (active?.bookId === book.id)
            $("breadcrumb").textContent = `${title.trim()} / ${active.title}`;
        }
      },
    },
    { title: "在文件夹中打开", run: () => native("open", { path: book.path }) },
    null,
    {
      title: "删除笔记本…",
      danger: true,
      disabled: book.id === "quick-notes",
      run: async () => {
        const count = catalog.notes.filter((x) => x.bookId === book.id).length;
        if (
          !(await choose(
            "删除笔记本",
            `“${book.title}”及其中 ${count} 篇笔记将移入回收站，可完整恢复。`,
            "移入回收站",
            "取消",
          ))
        )
          return;
        await flush();
        await store.deleteBook(book);
        if (active?.bookId === book.id) await clearSelection();
        if (bookId === book.id) bookId = "all";
        await refresh();
      },
    },
  ]);
}
function noteMenu(event, note) {
  const action = async (id) => {
    await selectNote(note);
    $(id).click();
  };
  menuAt(event, [
    { title: "打开笔记", run: () => selectNote(note) },
    { title: "重命名", run: () => action("rename-note") },
    {
      title: "移动到笔记本…",
      run: async () => {
        await selectNote(note);
        $("note-actions").click();
        $("move-book").focus();
      },
    },
    { title: "在文件夹中打开", run: () => native("open", { path: note.path }) },
    null,
    { title: "移入回收站…", danger: true, run: () => action("delete-note") },
  ]);
}
let navigating = Promise.resolve();
function navigate(intent) {
  navigating = navigating
    .catch(() => {})
    .then(async () => {
      leaveTrash();
      await flush();
      await store.ensureQuickBook();
      await refresh();
      $("search").value = "";
      let note;
      if (intent === "quick") {
        bookId = "quick-notes";
        note = catalog.notes
          .filter((x) => x.bookId === bookId)
          .sort((a, b) => b.updated - a.updated)[0];
        if (!note) {
          note = await store.createNote(
            catalog.books.find((x) => x.id === bookId),
            "随手记",
          );
          await refresh();
        }
      } else {
        const page = store.state.lastPage;
        note = catalog.notes.find((x) => x.id === page?.noteId);
        bookId = note
          ? page.filter === note.bookId ||
            ["all", "recent"].includes(page.filter)
            ? page.filter
            : note.bookId
          : "all";
        note ||= catalog.notes.slice().sort((a, b) => b.updated - a.updated)[0];
      }
      renderNavigation();
      renderNotes();
      if (note) {
        if (
          active?.path !== note.path ||
          active.markdown !== note.markdown ||
          !editor
        )
          await selectNote(note);
        else await store.rememberPage(active, bookId);
      }
      if (intent === "quick") editor?.focus();
    });
  return navigating;
}
async function startup() {
  settings = await native("settings");
  document.body.classList.toggle("mac", settings.platform === "mac");
  document.body.classList.toggle("native-titlebar", !!settings.nativeTitlebar);
  applyTheme();
  await store.initialize();
  applyDisplaySettings(store.state.display);
  await refresh();
  const unresolved = await store.json(".sync.json", { conflicts: [] });
  lastSyncReport = await store.json(".sync-report.json");
  if (unresolved.conflicts.length)
    $("sync-status").textContent = `${unresolved.conflicts.length} 个冲突`;
  else
    $("sync-status").textContent = settings.syncEnabled
      ? "等待同步"
      : "仅保存在本地";
  $("quick-notes").oncontextmenu = (event) =>
    bookMenu(
      event,
      catalog.books.find((x) => x.id === "quick-notes"),
    );
  $("new-book").onclick = $("empty-create").onclick = protect(async () => {
    const title = await ask("新建笔记本");
    if (title) {
      const book = await store.createBook(title);
      bookId = book.id;
      await refresh();
    }
  });
  $("new-note").onclick = $("empty-note").onclick = protect(async () => {
    let book =
      catalog.books.find((x) => x.id === bookId) ||
      catalog.books.find((x) => x.id === active?.bookId) ||
      catalog.books.find((x) => x.id !== "quick-notes") ||
      catalog.books[0];
    if (!book) throw new Error("请先创建笔记本");
    const title = await ask("新建笔记", "未命名笔记");
    if (title) {
      const note = await store.createNote(book, title);
      await refresh();
      await selectNote(note);
    }
  });
  document.querySelectorAll("[data-filter]").forEach(
    (button) =>
      (button.onclick = protect(async () => {
        if (button.dataset.filter === "quick-notes") {
          await navigate("quick");
          return;
        }
        await flush();
        leaveTrash();
        bookId = button.dataset.filter;
        renderNavigation();
        renderNotes();
        if (settings.platform === "web")
          document.body.dataset.mobilePage = "notes";
      })),
  );
  $("save-status").setAttribute("role", "button");
  $("save-status").tabIndex = 0;
  $("save-status").onclick = protect(async () => {
    if (!dirty || !active) return;
    if (!externalConflict) {
      await flush();
      return;
    }
    resolvingExternal = true;
    clearTimeout(saveTimer);
    await saving.catch(() => {});
    try {
      const keepLocal = await choose(
        "正文在磁盘上发生变化",
        "两个版本都会保存到本地备份。选择当前内容会备份磁盘版本再保存；选择磁盘版本会备份窗口内容后载入。",
        "保存当前内容",
        "载入磁盘版本",
        null,
      );
      if (keepLocal === null) return;
      const value = editor.getValue();
      const disk = await store.prepareExternalResolution(
        active,
        value,
        keepLocal,
      );
      externalConflict = false;
      saving = Promise.resolve();
      if (keepLocal) {
        resolvingExternal = false;
        dirty = true;
        await flush();
      } else {
        switching = true;
        editor.setValue(disk, true);
        switching = false;
        history?.seal();
        history?.record(editor.getValue());
        dirty = false;
        $("save-status").textContent = "已载入磁盘版本 · 窗口内容已备份";
        await refresh();
        await renderAttachments();
      }
    } finally {
      resolvingExternal = false;
    }
  });
  $("save-status").onkeydown = (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      $("save-status").click();
    }
  };
  $("search").oninput = renderNotes;
  $("theme").onclick = protect(async () => {
    settings.theme = settings.theme === "Dark" ? "Light" : "Dark";
    await native("theme", { value: settings.theme });
    applyTheme();
  });
  const switchMode = (mode) => {
    if (!editor || editor.vditor.currentMode === mode) return;
    cancelPositionRestore();
    codeWork.reset();
    if (mode === "ir")
      editor.vditor.options.customRenders = deferredCodeRenders(
        editor.getValue(),
      );
    const position = captureReadingPosition(editor);
    history?.record(editor.getValue());
    history?.seal();
    switching = true;
    $("editor").querySelector(`button[data-mode="${mode}"]`)?.click();
    switching = false;
    history?.normalizeCurrent(editor.getValue());
    paintHistory();
    $("preview-mode").classList.toggle("active", mode === "ir");
    $("source-mode").classList.toggle("active", mode === "sv");
    cancelPositionRestore = restoreReadingPosition(editor, position);
  };
  $("preview-mode").onclick = () => switchMode("ir");
  $("source-mode").onclick = () => switchMode("sv");
  $("mobile-display").onclick = protect(showDisplaySettings);
  $("mobile-edit").onclick = protect(async () => {
    if (!active) return;
    if (mobileEditing) {
      await flush();
      document.activeElement?.blur();
    }
    mobileEditing = !mobileEditing;
    applyMobileAccess();
  });
  $("attachments").onclick = protect(async () => {
    if (!active) return;
    $("attachment-drawer").hidden = !$("attachment-drawer").hidden;
    await renderAttachments();
  });
  $("close-drawer").onclick = () => ($("attachment-drawer").hidden = true);
  document.querySelectorAll("[data-attachment-tab]").forEach(
    (button) =>
      (button.onclick = protect(async () => {
        attachmentTab = button.dataset.attachmentTab;
        document
          .querySelectorAll("[data-attachment-tab]")
          .forEach((x) => x.classList.toggle("active", x === button));
        await renderAttachments();
      })),
  );
  $("add-attachment").onclick = () => $("file-input").click();
  $("file-input").onchange = protect(async () => {
    await attachFiles([...$("file-input").files]);
    $("file-input").value = "";
  });
  $("library").onclick = $("sync-settings").onclick = protect(configure);
  $("sync-now").onclick = protect(sync);
  $("sync-results").onclick = $("sync-report-open").onclick =
    protect(showSyncReport);
  $("remote-status").onclick = protect(showSyncReport);
  $("remote-status").onkeydown = (event) => {
    if (["Enter", " "].includes(event.key)) {
      event.preventDefault();
      $("remote-status").click();
    }
  };
  $("sync-report-close").onclick = () => $("sync-report-dialog").close();
  $("sync-report-retry").onclick = protect(async () => {
    $("sync-report-dialog").close();
    await sync();
    await showSyncReport();
  });
  $("sync-report-conflicts").onclick = protect(async () => {
    const state = await store.json(".sync.json", { conflicts: [] });
    $("sync-report-dialog").close();
    if (state.conflicts.length && lastSyncEngine)
      showConflicts(lastSyncEngine, state.conflicts);
  });
  $("sync-cancel").onclick = () => {
    $("dav-password").value = "";
    $("sync-dialog").close();
  };
  $("sync-save").onclick = async () => {
    $("sync-save").disabled = true;
    $("sync-error").textContent = "";
    try {
      if (syncing || configuring || pendingConflictResolutions.size)
        throw new Error("请等待当前同步或冲突处理完成后更改配置");
      configuring = true;
      await flush();
      // A manually started conflict resolution also owns a sync pass.
      await store.syncTail;
      await store.tail;
      const grace = Number($("grace").value) * 1000;
      if (grace < 5000 || grace > 3600000)
        throw new Error("保护时间应为 5–3600 秒");
      settings = await native("configure", {
        url: $("dav-url").value.trim(),
        user: $("dav-user").value.trim(),
        password: $("dav-password").value,
        enabled: $("dav-enabled").checked,
      });
      $("dav-password").value = "";
      store.grace = grace;
      store.state.grace = grace;
      await store.serial(() => store.put(".state.json", store.state));
      $("sync-dialog").close();
      configuring = false;
      await sync();
    } catch (error) {
      $("sync-error").textContent = error.message;
    } finally {
      configuring = false;
      $("sync-save").disabled = false;
    }
  };
  $("open-root").onclick = protect(() => native("open", { path: "" }));
  $("maintenance").onclick = protect(async () => {
    await maintenance();
    toast("巡检完成");
  });
  $("note-actions").onclick = () => {
    if (!active) return;
    $("move-book").replaceChildren(
      ...catalog.books
        .filter((x) => x.id !== active.bookId)
        .map((x) => {
          const option = label(x.title, "option");
          option.value = x.id;
          return option;
        }),
    );
    $("action-dialog").showModal();
  };
  $("action-close").onclick = () => $("action-dialog").close();
  $("rename-note").onclick = protect(async () => {
    $("action-dialog").close();
    const title = await ask("重命名笔记", active.title);
    if (title) {
      await flush();
      await store.renameNote(active, title);
      active.title = title;
      await refresh();
      $("breadcrumb").textContent = title;
    }
  });
  $("move-note").onclick = protect(async () => {
    const book = catalog.books.find((x) => x.id === $("move-book").value);
    if (!book) return;
    await flush();
    active.path = await store.moveNote(active, book);
    active.bookId = book.id;
    bookId = book.id;
    $("action-dialog").close();
    await refresh();
    await selectNote(active);
  });
  $("delete-note").onclick = protect(async () => {
    await flush();
    if (
      !(await choose(
        "删除笔记",
        `“${active.title}”及附件将移入回收站，可随时恢复。`,
        "移入回收站",
        "取消",
      ))
    )
      return;
    await store.deleteNote(active);
    await clearSelection();
    $("action-dialog").close();
    await refresh();
  });
  $("trash").onclick = protect(showTrash);
  $("trash-close").onclick = leaveTrash;
  $("conflict-close").onclick = () => $("conflict-dialog").close();
  document.querySelectorAll("[data-window]").forEach(
    (button) =>
      (button.onclick = protect(async () => {
        if (button.dataset.window === "close") await flush();
        await native("window", { action: button.dataset.window });
      })),
  );
  $("titlebar").onmousedown = (event) => {
    if (event.button === 0 && !event.target.closest("button"))
      native("window", { action: "drag" }).catch(() => {});
  };
  $("titlebar").ondblclick = protect(async (event) => {
    if (!event.target.closest("button"))
      await native("window", { action: "maximize" });
  });
  window.notebookFlush = async () => {
    if (editingCode) throw new Error("请先保存或关闭代码编辑窗口");
    await flush();
  };
  window.notebookSync = protect(sync);
  window.notebookClose = protect(async () => {
    await window.notebookFlush();
    await native("window", { action: "close" });
  });
  window.notebookWake = protect(async () => {
    await maintenance();
    await sync();
  });
  window.addEventListener("blur", protect(flush));
  setInterval(protect(maintenance), 3600000);
  if (!settings.nativeTitlebar) setInterval(protect(sync), 300000);
  setInterval(
    protect(async () => {
      if (
        active &&
        !dirty &&
        !syncing &&
        !configuring &&
        !loadingNote &&
        !editingCode
      ) {
        const note = active,
          version = selectionVersion;
        const text = await io.read(`${note.path}/main.md`);
        if (
          version === selectionVersion &&
          active === note &&
          !dirty &&
          !editingCode &&
          text !== null &&
          text !== active.markdown
        ) {
          active.markdown = text;
          switching = true;
          editor.setValue(text, true);
          switching = false;
          store.setDraft(active.path, text);
          await refresh();
          await renderAttachments();
        }
      }
    }),
    10000,
  );
  setInterval(
    protect(async () => {
      if (active && !syncing && !configuring)
        await store.serial(() => store.reconcile(active.path, false));
    }),
    10000,
  );
  // WKWebView can restore focus onto a separator on first opening and treat it
  // as :focus-visible without any keyboard input. Require actual Tab navigation
  // before showing its keyboard marker, while keeping the separator focusable.
  let resizeKeyboardNavigation = false;
  const clearResizeKeyboardFocus = () => {
    resizeKeyboardNavigation = false;
    document
      .querySelectorAll(".column-resize[data-keyboard-focus]")
      .forEach((divider) => divider.removeAttribute("data-keyboard-focus"));
  };
  document.addEventListener(
    "keydown",
    (event) => {
      if (
        event.key === "Tab" &&
        !event.altKey &&
        !event.ctrlKey &&
        !event.metaKey
      )
        resizeKeyboardNavigation = true;
    },
    true,
  );
  document.addEventListener("pointerdown", clearResizeKeyboardFocus, true);
  window.addEventListener("blur", clearResizeKeyboardFocus);
  for (const [panel, key, initial, min, max] of [
    [$("books"), "book", 190, 150, 300],
    [$("notes"), "note", 270, 200, 400],
  ]) {
    const divider = document.createElement("div");
    divider.className = "column-resize";
    divider.tabIndex = 0;
    divider.setAttribute("role", "separator");
    divider.setAttribute(
      "aria-label",
      key === "book" ? "调整笔记本栏宽度" : "调整笔记列表宽度",
    );
    divider.setAttribute("aria-orientation", "vertical");
    panel.append(divider);
    const setWidth = (value) => {
      const width = Math.max(min, Math.min(max, innerWidth - 620, value));
      $("workspace").style.setProperty(`--${key}-width`, width + "px");
      localStorage.setItem("notebook-width-" + key, String(width));
      divider.setAttribute("aria-valuenow", String(width));
    };
    const saved = Number(localStorage.getItem("notebook-width-" + key));
    if (saved) setWidth(saved);
    divider.onfocus = () =>
      divider.toggleAttribute("data-keyboard-focus", resizeKeyboardNavigation);
    divider.onblur = () => divider.removeAttribute("data-keyboard-focus");
    divider.onpointerdown = (event) => {
      if (event.button !== 0) return;
      event.preventDefault();
      const start = event.clientX,
        width = panel.getBoundingClientRect().width;
      divider.setPointerCapture(event.pointerId);
      divider.onpointermove = (move) => setWidth(width + move.clientX - start);
      divider.onpointerup = () => (divider.onpointermove = null);
    };
    divider.onkeydown = (event) => {
      if (["ArrowLeft", "ArrowRight"].includes(event.key)) {
        event.preventDefault();
        divider.setAttribute("data-keyboard-focus", "");
        setWidth(
          panel.getBoundingClientRect().width +
            (event.key === "ArrowRight" ? 10 : -10),
        );
      }
    };
    divider.ondblclick = () => setWidth(initial);
  }
  icons();
  window.notebookNavigate = protect(navigate);
  await navigate("last");
  await native("ready");
  if (settings.platform === "web") {
    $("sync-dialog").querySelector("h2").textContent = "同步与离线存储";
    $("sync-dialog").querySelector(".inline").hidden = true;
    await protect(sync)();
    if (!active) await navigate("last");
  }
  await protect(maintenance)();
}
(async () => {
  if (isBrowser()) await installBrowserBridge();
  await startup();
})().catch((error) => {
  toast(error.message);
  $("save-status").textContent = "初始化失败";
});
