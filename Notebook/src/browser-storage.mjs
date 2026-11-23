import { encode, decode, digest } from "./core.mjs";
export const notebookPath = (path) =>
  /^(books|\.deleted|\.deleted-books)\//.test(path);
export const textPath = (path) =>
  notebookPath(path) &&
  !/\/(reference|\.trash)\//.test(path) &&
  /\.(md|json)$/.test(path);
const safe = (path) => {
  if (
    typeof path !== "string" ||
    path.startsWith("/") ||
    path.includes("\\") ||
    path.split("/").some((x) => x === "." || x === "..") ||
    /[\x00-\x1f]/.test(path)
  )
    throw Error("笔记路径无效");
  return path.replace(/\/$/, "");
};
const parents = (path) =>
  path
    .split("/")
    .slice(0, -1)
    .map((_, i) =>
      path
        .split("/")
        .slice(0, i + 1)
        .join("/"),
    );
export class BrowserFiles {
  constructor(db, request) {
    this.db = db;
    this.request = request;
    this.entries = new Map();
  }
  static async open(account, request) {
    const db = await new Promise((resolve, reject) => {
      const r = indexedDB.open("assistant-notes-" + account, 1);
      r.onupgradeneeded = () =>
        r.result.createObjectStore("files", { keyPath: "path" });
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
    const io = new BrowserFiles(db, request);
    const records = await new Promise((resolve, reject) => {
      const r = db.transaction("files").objectStore("files").getAll();
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
    for (const record of records) io.entries.set(record.path, record);
    return io;
  }
  async commit(writes = [], deletes = []) {
    // Publish in-memory changes only after the durable transaction completes.
    await new Promise((resolve, reject) => {
      const tx = this.db.transaction("files", "readwrite"),
        table = tx.objectStore("files");
      for (const record of writes) table.put(record);
      for (const path of deletes) table.delete(path);
      tx.oncomplete = resolve;
      tx.onabort = () =>
        reject(tx.error || Error("手机存储写入失败，正文尚未保存"));
      tx.onerror = () => {};
    });
    for (const record of writes) this.entries.set(record.path, record);
    for (const path of deletes) this.entries.delete(path);
    window.dispatchEvent(new Event("notebook-storage"));
  }
  directories(path) {
    return parents(path)
      .filter((p) => !this.entries.has(p))
      .map((path) => ({ path, dir: true }));
  }
  wanted(path) {
    const r = this.entries.get(path);
    return textPath(path) || (!!r && !r.dir && (r.deleted || r.data !== null));
  }
  async inventory(list) {
    const seen = new Set(list.map((x) => x.path)),
      writes = [],
      deletes = [];
    for (const item of list) {
      const path = safe(item.path);
      if (textPath(path)) continue;
      const previous = this.entries.get(path);
      if (previous) {
        if (
          previous.remote &&
          previous.data === null &&
          previous.etag !== item.etag
        )
          writes.push({ ...previous, etag: item.etag });
        continue;
      }
      writes.push(...this.directories(path), {
        path,
        dir: false,
        data: null,
        etag: item.etag,
        remote: true,
      });
    }
    for (const [path, r] of this.entries)
      if (
        r.remote &&
        r.data === null &&
        !seen.has(path) &&
        !(list.errors || []).some((error) => path.startsWith(error.path))
      )
        deletes.push(path);
    await this.commit(writes, deletes);
  }
  deferReconcile(path) {
    return this.entries.get(path)?.data === null;
  }
  async materialize(path) {
    const data = await this.readBinary(path);
    if (data === null)
      throw Error("此操作包含未下载的附件，请联网后重试；正文仍保留在本机");
    return data;
  }
  async readBinary(path) {
    path = safe(path);
    let r = this.entries.get(path);
    if (!r || r.deleted || r.dir) return null;
    if (r.data !== null) return r.data;
    if (!navigator.onLine) return null;
    const result = await this.request(
      "GET",
      path,
      null,
      r.etag ? { "If-Match": r.etag } : {},
    );
    if (result.status !== 200 || !result.etag || /^W\//i.test(result.etag))
      throw Error("附件在线版本已变化，请先同步再打开");
    // Keep the download's common version beside its bytes. The sync engine
    // imports it on its own serial queue, so image loads cannot overwrite a checkpoint.
    const current = this.entries.get(path);
    if (current !== r) throw Error("附件位置已变化，请重新打开");
    await this.commit([
      {
        ...r,
        data: result.data,
        etag: result.etag,
        common: { hash: await digest(result.data), etag: result.etag },
      },
    ]);
    return result.data;
  }
  async read(path) {
    const data = await this.readBinary(path);
    return data === null ? null : decode(data);
  }
  async write(path, text) {
    return this.writeBinary(path, encode(text));
  }
  async writeBinary(path, data) {
    path = safe(path);
    if (typeof data !== "string") throw Error("文件内容无效");
    await this.commit([
      ...this.directories(path),
      {
        path,
        dir: false,
        data,
        common: this.entries.get(path)?.common,
        dirty: notebookPath(path),
      },
    ]);
  }
  async mkdir(path) {
    path = safe(path);
    if (path && !this.entries.has(path))
      await this.commit([...this.directories(path), { path, dir: true }]);
  }
  async list(path = "") {
    path = safe(path);
    const prefix = path ? path + "/" : "";
    return [...this.entries.values()]
      .filter(
        (r) =>
          !r.deleted &&
          r.path.startsWith(prefix) &&
          !r.path.slice(prefix.length).includes("/") &&
          r.path !== path,
      )
      .map((r) => ({
        name: r.path.slice(prefix.length),
        dir: r.dir,
        modified: 0,
      }));
  }
  async remove(path) {
    path = safe(path);
    const r = this.entries.get(path);
    if (!r) return;
    // A missing cached body is not a deletion. Materialize explicitly first.
    if (r.data === null) await this.materialize(path);
    await this.commit([
      {
        path,
        dir: false,
        deleted: true,
        common: this.entries.get(path)?.common,
        dirty: notebookPath(path),
      },
    ]);
  }
  async move(path, to) {
    path = safe(path);
    to = safe(to);
    const group = [...this.entries.values()].filter(
      (r) => !r.deleted && (r.path === path || r.path.startsWith(path + "/")),
    );
    if (!group.length) throw Error("文件已不存在");
    if (
      group.some(
        (r) =>
          this.entries.has(to + r.path.slice(path.length)) &&
          !this.entries.get(to + r.path.slice(path.length)).deleted,
      )
    )
      throw Error("目标文件已存在，已保留原内容");
    // Preflight every attachment before changing any paths. Offline text-only moves work.
    for (const r of group) if (r.data === null) await this.materialize(r.path);
    const writes = [...this.directories(to)];
    for (const original of group) {
      const r = this.entries.get(original.path),
        dest = to + r.path.slice(path.length);
      writes.push({
        ...r,
        path: dest,
        dirty: notebookPath(dest),
        remote: false,
        common: undefined,
      });
      if (r.dir) continue;
      writes.push({
        path: r.path,
        dir: false,
        deleted: true,
        dirty: notebookPath(r.path),
        common: r.common,
      });
    }
    await this.commit(
      writes,
      group.filter((r) => r.dir).map((r) => r.path),
    );
  }
  async purgeDeleted(path) {
    if (!/^\.(deleted|deleted-books)\/[A-Za-z0-9_-]+$/.test(path))
      throw Error("回收目录无效");
    const group = [...this.entries.values()].filter(
      (r) => !r.deleted && r.path.startsWith(path + "/"),
    );
    for (const r of group) if (r.data === null) await this.materialize(r.path);
    await this.commit(
      group
        .filter((r) => !r.dir)
        .map((r) => ({
          path: r.path,
          dir: false,
          deleted: true,
          dirty: true,
          common: this.entries.get(r.path)?.common,
        })),
      [path, ...group.filter((r) => r.dir).map((r) => r.path)],
    );
  }
  async pendingCount() {
    const checkpoint = await this.read(".sync.json"),
      base = checkpoint ? JSON.parse(checkpoint).files : {};
    let count = 0;
    for (const r of this.entries.values())
      if (r.dirty && !r.dir && notebookPath(r.path)) {
        if (
          r.deleted
            ? !!base[r.path]
            : (await digest(r.data)) !== base[r.path]?.hash
        )
          count++;
      }
    return count;
  }
  async finishSync() {
    const checkpoint = JSON.parse(
        (await this.read(".sync.json")) || '{"files":{}}',
      ),
      writes = [],
      deletes = [];
    for (const r of this.entries.values())
      if (r.dirty && !r.dir) {
        if (r.deleted && !checkpoint.files[r.path]) deletes.push(r.path);
        else if (
          !r.deleted &&
          (await digest(r.data)) === checkpoint.files[r.path]?.hash
        )
          writes.push({ ...r, dirty: false });
      }
    if (writes.length || deletes.length) await this.commit(writes, deletes);
  }
}
