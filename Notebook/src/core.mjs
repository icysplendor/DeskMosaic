import MarkdownIt from "markdown-it";

const parser = new MarkdownIt({ html: true, linkify: false });
export const DAY = 86400000;
export function attachmentPath(value) {
  try {
    const path = decodeURIComponent(value.split(/[?#]/)[0]).replace(
      /^\.\//,
      "",
    );
    if (
      !path.startsWith("reference/") ||
      path.includes("\\") ||
      path.split("/").some((p) => p === ".." || !p)
    )
      return null;
    return path;
  } catch {
    return null;
  }
}
export function attachmentLink(name, filename, image = false) {
  const label = name.replace(/[\\[\]]/g, "\\$&");
  const path = encodeURIComponent(filename).replace(
    /[!'()*]/g,
    (char) => "%" + char.charCodeAt(0).toString(16).toUpperCase(),
  );
  return `${image ? "!" : ""}[${label}](reference/${path})`;
}
export function references(markdown) {
  const found = new Set();
  const visit = (tokens) => {
    for (const token of tokens) {
      for (const [key, value] of token.attrs || [])
        if (key === "href" || key === "src") {
          const path = attachmentPath(value);
          if (path) found.add(path);
        }
      if (token.type.startsWith("html"))
        for (const match of token.content.matchAll(
          /(?:src|href)\s*=\s*["']([^"']+)["']/gi,
        )) {
          const path = attachmentPath(match[1]);
          if (path) found.add(path);
        }
      if (token.children) visit(token.children);
    }
  };
  visit(parser.parse(markdown, {}));
  // Keep incomplete links during source editing safe. A temporary syntax error must
  // never make an attachment disappear. Code-only links are deliberately retained.
  for (const match of markdown.matchAll(/(?:\.\/)?reference\/[^\s<>"')\]]+/g)) {
    const path = attachmentPath(match[0]);
    if (path) found.add(path);
  }
  return found;
}
export const encode = (text) => btoa(unescape(encodeURIComponent(text)));
export const decode = (data) => decodeURIComponent(escape(atob(data)));
export const id = () => crypto.randomUUID();
export async function digest(data) {
  const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
    .map((x) => x.toString(16).padStart(2, "0"))
    .join("");
}
export class NotebookStore {
  constructor(io, clock = () => Date.now()) {
    this.io = io;
    this.clock = clock;
    this.tail = Promise.resolve();
    this.drafts = new Map();
    this.grace = 30000;
  }
  serial(work) {
    const next = this.tail.then(work);
    this.tail = next.catch(() => {});
    return next;
  }
  async json(path, fallback = null) {
    const text = await this.io.read(path);
    return text === null ? fallback : JSON.parse(text);
  }
  async put(path, value) {
    const text = JSON.stringify(value, null, 2);
    if ((await this.io.read(path)) !== text) await this.io.write(path, text);
  }
  async initialize() {
    await this.io.mkdir("books");
    this.state = await this.json(".state.json", {
      grace: 30000,
      lastSync: 0,
      conflicts: 0,
    });
    this.grace = Math.max(5000, Number(this.state.grace) || 30000);
    await this.ensureQuickBook();
    return this.catalog();
  }
  ensureQuickBook() {
    return this.serial(async () => {
      const path = "books/quick-notes";
      let book = await this.json(`${path}/book.json`);
      if (!book) {
        book = { id: "quick-notes", title: "快速笔记", created: 0 };
        await this.io.mkdir(path);
        await this.put(`${path}/book.json`, book);
      }
      return { ...book, path };
    });
  }
  rememberPage(note, filter) {
    return this.serial(async () => {
      this.state.lastPage = { noteId: note.id, bookId: note.bookId, filter };
      await this.put(".state.json", this.state);
    });
  }
  async catalog() {
    const books = [],
      notes = [];
    for (const folder of await this.io.list("books"))
      if (folder.dir) {
        const path = `books/${folder.name}`;
        const book = await this.json(`${path}/book.json`);
        if (!book) continue;
        books.push({ ...book, path });
        for (const item of await this.io.list(path))
          if (item.dir && !item.name.startsWith(".")) {
            const notePath = `${path}/${item.name}`;
            const note = await this.json(`${notePath}/note.json`);
            const markdown = await this.io.read(`${notePath}/main.md`);
            if (note && markdown !== null)
              notes.push({
                ...note,
                bookId: book.id,
                path: notePath,
                markdown,
              });
          }
      }
    return { books, notes };
  }
  createBook(title) {
    return this.serial(async () => {
      const book = { id: id(), title, created: this.clock() };
      const path = `books/${book.id}`;
      await this.io.mkdir(path);
      await this.put(`${path}/book.json`, book);
      return book;
    });
  }
  renameBook(book, title) {
    return this.serial(async () => {
      if (book.id === "quick-notes") throw new Error("快速笔记本名称固定");
      const meta = await this.json(`${book.path}/book.json`);
      if (!meta) throw new Error("笔记本已移动或删除，请刷新后重试");
      await this.put(`${book.path}/book.json`, { ...meta, title });
    });
  }
  deleteBook(book) {
    return this.serial(async () => {
      if (book.id === "quick-notes") throw new Error("快速笔记本不能删除");
      await this.io.mkdir(".deleted-books");
      await this.put(`${book.path}/deleted.json`, {
        original: book.path,
        title: book.title,
        at: this.clock(),
      });
      await this.io.move(book.path, `.deleted-books/${book.id}`);
      for (const path of this.drafts.keys())
        if (path.startsWith(book.path + "/")) this.drafts.delete(path);
    });
  }
  async deletedBooks() {
    const rows = [];
    for (const folder of await this.io.list(".deleted-books"))
      if (folder.dir) {
        const meta = await this.json(
          `.deleted-books/${folder.name}/deleted.json`,
        );
        if (meta) rows.push({ ...meta, path: `.deleted-books/${folder.name}` });
      }
    return rows;
  }
  restoreBook(row) {
    return this.serial(async () => {
      if ((await this.io.read(`${row.original}/book.json`)) !== null)
        throw new Error("原位置已存在笔记本，不能覆盖");
      await this.io.move(row.path, row.original);
      await this.io.remove(`${row.original}/deleted.json`);
    });
  }
  createNote(book, title) {
    return this.serial(async () => {
      const note = {
        id: id(),
        title,
        updated: this.clock(),
        created: this.clock(),
      };
      const path = `${book.path}/${note.id}`;
      await this.io.mkdir(`${path}/reference`);
      await this.io.mkdir(`${path}/.trash`);
      await this.io.write(`${path}/main.md`, "");
      await this.put(`${path}/note.json`, note);
      return { ...note, path, bookId: book.id, markdown: "" };
    });
  }
  setDraft(path, markdown) {
    this.drafts.set(path, markdown);
  }
  save(note, markdown) {
    this.setDraft(note.path, markdown);
    return this.serial(async () => {
      await this.io.write(`.drafts/${note.id}.md`, markdown);
      const current = await this.io.read(`${note.path}/main.md`);
      if (current !== note.markdown)
        throw new Error(
          "正文已被其他程序或同步修改；恢复稿已保存在 .drafts，请先比较版本",
        );
      await this.io.write(`${note.path}/main.md`, markdown);
      note.markdown = markdown;
      const meta = await this.json(`${note.path}/note.json`);
      await this.put(`${note.path}/note.json`, {
        ...meta,
        updated: this.clock(),
      });
      await this.reconcile(note.path, false);
      await this.io.remove(`.drafts/${note.id}.md`);
      return markdown;
    });
  }
  prepareExternalResolution(note, draft, keepLocal) {
    return this.serial(async () => {
      const disk = await this.io.read(`${note.path}/main.md`);
      if (disk === null)
        throw new Error("磁盘笔记已移动或删除，请先恢复原目录");
      const snapshot = `.quarantine/${id()}`;
      await this.io.write(`${snapshot}/window.md`, draft);
      await this.io.write(`${snapshot}/disk.md`, disk);
      note.markdown = disk;
      if (!keepLocal) {
        this.setDraft(note.path, disk);
        await this.io.remove(`.drafts/${note.id}.md`);
      }
      return disk;
    });
  }
  renameNote(note, title) {
    return this.serial(async () => {
      const meta = await this.json(`${note.path}/note.json`);
      await this.put(`${note.path}/note.json`, { ...meta, title });
    });
  }
  moveNote(note, book) {
    return this.serial(async () => {
      const dest = `${book.path}/${note.id}`;
      await this.io.move(note.path, dest);
      this.drafts.delete(note.path);
      return dest;
    });
  }
  deleteNote(note) {
    return this.serial(async () => {
      const trash = `.deleted/${note.id}`;
      await this.io.mkdir(".deleted");
      await this.put(`${note.path}/deleted.json`, {
        original: note.path,
        at: this.clock(),
        title: note.title,
      });
      await this.io.move(note.path, trash);
      this.drafts.delete(note.path);
    });
  }
  async deletedNotes() {
    const rows = [];
    for (const folder of await this.io.list(".deleted"))
      if (folder.dir) {
        const meta = await this.json(`.deleted/${folder.name}/deleted.json`);
        if (meta) rows.push({ ...meta, path: `.deleted/${folder.name}` });
      }
    return rows;
  }
  restoreNote(row) {
    return this.serial(async () => {
      if ((await this.io.read(`${row.original}/main.md`)) !== null)
        throw new Error("原位置已存在笔记，不能覆盖");
      const bookPath = row.original.slice(0, row.original.lastIndexOf("/"));
      if (!(await this.json(`${bookPath}/book.json`)))
        throw new Error("请先恢复所属笔记本，再恢复这篇笔记");
      await this.io.move(row.path, row.original);
      await this.io.remove(`${row.original}/deleted.json`);
    });
  }
  purgeDeleted(row) {
    return this.serial(async () => {
      if (!/^\.(deleted|deleted-books)\/[A-Za-z0-9_-]+$/.test(row.path))
        throw new Error("只能永久删除回收站中的笔记或笔记本");
      await this.io.purgeDeleted(row.path);
    });
  }
  purgeAttachment(note, filename) {
    return this.serial(async () => {
      const path = `${note.path}/.attachments.json`;
      const state = await this.json(path, { entries: {} });
      if (!state.entries[filename]?.trashSince)
        throw new Error("附件不在回收站中");
      if (await this.attachmentIsReferenced(note, filename))
        throw new Error("附件仍被正文或未保存内容引用，不能永久删除");
      await this.io.remove(`${note.path}/.trash/${filename}`);
      delete state.entries[filename];
      await this.put(path, state);
    });
  }
  async attachmentIsReferenced(note, filename) {
    const path = `reference/${filename}`;
    for (const text of [
      await this.io.read(`${note.path}/main.md`),
      await this.io.read(`.drafts/${note.id}.md`),
      this.drafts.get(note.path),
    ]) {
      if (text && references(text).has(path)) return true;
    }
    return false;
  }
  recycleAttachment(note, filename) {
    return this.serial(async () => {
      if (await this.attachmentIsReferenced(note, filename))
        throw new Error("附件仍被正文或未保存内容引用，不能回收");
      const path = `${note.path}/.attachments.json`;
      const state = await this.json(path, { entries: {} });
      const entry = state.entries[filename];
      if (!entry) throw new Error("附件记录不存在");
      if (entry.trashSince) return;
      entry.trashSince = this.clock();
      await this.put(path, state);
      await this.io.move(
        `${note.path}/reference/${filename}`,
        `${note.path}/.trash/${filename}`,
      );
    });
  }
  importAttachment(note, name, data) {
    return this.serial(async () => {
      const clean =
        name.replace(/[\\/:*?"<>|\x00-\x1f]/g, "_").slice(-120) || "attachment";
      const state = await this.json(`${note.path}/.attachments.json`, {
        entries: {},
      });
      const hash = await digest(data);
      for (const [existing, entry] of Object.entries(state.entries)) {
        if (entry.name !== clean || (entry.sha256 && entry.sha256 !== hash))
          continue;
        const location = entry.trashSince ? ".trash" : "reference";
        const content = await this.io.readBinary(
          `${note.path}/${location}/${existing}`,
        );
        if (content === null || (await digest(content)) !== hash) continue;
        if (entry.trashSince)
          await this.io.move(
            `${note.path}/.trash/${existing}`,
            `${note.path}/reference/${existing}`,
          );
        entry.sha256 = hash;
        entry.trashSince = null;
        entry.unusedSince = this.clock();
        await this.put(`${note.path}/.attachments.json`, state);
        return {
          filename: existing,
          name: clean,
          path: `reference/${existing}`,
        };
      }
      const dot = clean.lastIndexOf(".");
      const filename =
        dot > 0
          ? `${clean.slice(0, dot)}-${id()}${clean.slice(dot)}`
          : `${clean}-${id()}`;
      await this.io.writeBinary(`${note.path}/reference/${filename}`, data);
      state.entries[filename] = {
        sha256: hash,
        name: clean,
        unusedSince: this.clock(),
        trashSince: null,
      };
      await this.put(`${note.path}/.attachments.json`, state);
      return { filename, name: clean, path: `reference/${filename}` };
    });
  }
  async reconcile(path, purge = false) {
    const markdown = await this.io.read(`${path}/main.md`);
    if (markdown === null) return;
    const found = references(markdown);
    const note = await this.json(`${path}/note.json`);
    const recovery = note ? await this.io.read(`.drafts/${note.id}.md`) : null;
    if (recovery !== null)
      for (const ref of references(recovery)) found.add(ref);
    const draft = this.drafts.get(path);
    if (draft !== undefined)
      for (const ref of references(draft)) found.add(ref);
    const state = await this.json(`${path}/.attachments.json`, { entries: {} });
    const live = await this.io.list(`${path}/reference`),
      trash = await this.io.list(`${path}/.trash`);
    const liveNames = new Set(
      live.filter((x) => !x.dir && !x.name.endsWith(".bak")).map((x) => x.name),
    );
    const trashNames = new Set(
      trash
        .filter((x) => !x.dir && !x.name.endsWith(".bak"))
        .map((x) => x.name),
    );
    const problems = [];
    for (const item of await this.io.list(path)) {
      if (
        !item.dir &&
        !item.name.startsWith(".") &&
        !item.name.endsWith(".bak") &&
        !["main.md", "note.json", "deleted.json"].includes(item.name)
      ) {
        problems.push(`附件位于笔记根目录，已保留原文件：${item.name}`);
      }
    }

    for (const name of new Set([
      ...liveNames,
      ...trashNames,
      ...Object.keys(state.entries),
    ])) {
      const entry = (state.entries[name] ||= {
        name,
        unusedSince: null,
        trashSince: null,
      });
      const from = `${path}/reference/${name}`,
        to = `${path}/.trash/${name}`;
      // Mobile leaves uncached attachment moves to a client with the bytes.
      // Never download attachments merely because a background text save runs.
      if (this.io.deferReconcile?.(from) || this.io.deferReconcile?.(to)) {
        entry.unusedSince = found.has(`reference/${name}`)
          ? null
          : (entry.unusedSince ?? this.clock());
        continue;
      }
      if (liveNames.has(name) && trashNames.has(name))
        problems.push(`附件位置重复，已保留两份：${entry.name}`);
      if (found.has(`reference/${name}`)) {
        if (!liveNames.has(name) && trashNames.has(name)) {
          await this.io.move(to, from);
          liveNames.add(name);
          trashNames.delete(name);
        }
        if (!liveNames.has(name)) problems.push(`附件缺失：${entry.name}`);
        entry.unusedSince = null;
        entry.trashSince = null;
      } else if (liveNames.has(name)) {
        entry.unusedSince ??= this.clock();
        if (
          this.clock() - entry.unusedSince >= this.grace &&
          !trashNames.has(name)
        ) {
          // Journal the intention before moving; re-running after a crash is safe.
          entry.trashSince = this.clock();
          await this.put(`${path}/.attachments.json`, state);
          await this.io.move(from, to);
          trashNames.add(name);
          liveNames.delete(name);
        }
      } else if (trashNames.has(name)) {
        entry.trashSince ??= this.clock();
        if (purge && this.clock() - entry.trashSince >= 15 * DAY) {
          await this.io.remove(to);
          delete state.entries[name];
          trashNames.delete(name);
        }
      } else {
        // Remove orphaned metadata, not files or still-referenced missing entries.
        delete state.entries[name];
      }
    }
    for (const ref of found)
      if (!liveNames.has(ref.slice("reference/".length)))
        problems.push(`引用无法解析：${ref}`);
    await this.put(`${path}/.attachments.json`, state);
    return { state, problems };
  }
  restoreAttachment(note, filename) {
    return this.serial(async () => {
      const state = await this.json(`${note.path}/.attachments.json`, {
        entries: {},
      });
      const entry = state.entries[filename];
      if (!entry) throw new Error("附件记录不存在");
      const live = await this.io.list(`${note.path}/reference`);
      if (!live.some((x) => x.name === filename))
        await this.io.move(
          `${note.path}/.trash/${filename}`,
          `${note.path}/reference/${filename}`,
        );
      entry.trashSince = null;
      entry.unusedSince = this.clock();
      await this.put(`${note.path}/.attachments.json`, state);
      return attachmentLink(entry.name, filename);
    });
  }
  maintenance(syncEnabled = false) {
    return this.serial(async () => {
      const checkpoint = await this.json(".sync.json", { conflicts: [] });
      const purge =
        !checkpoint.conflicts.length &&
        !this.state.conflicts &&
        (!syncEnabled || this.clock() - this.state.lastSync < 3600000);
      const report = [];
      for (const note of (await this.catalog()).notes)
        report.push({ note, ...(await this.reconcile(note.path, purge)) });
      this.state.lastMaintenance = this.clock();
      await this.put(".state.json", this.state);
      return report;
    });
  }
  async files(path = "") {
    const output = [];
    for (const file of await this.io.list(path)) {
      if (
        file.name.endsWith(".bak") ||
        file.name === ".state.json" ||
        file.name === ".sync.json" ||
        file.name === ".conflicts" ||
        file.name === ".quarantine"
      )
        continue;
      const relative = path ? `${path}/${file.name}` : file.name;
      if (file.dir) output.push(...(await this.files(relative)));
      else output.push(relative);
    }
    return output;
  }
}
