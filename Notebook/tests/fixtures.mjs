import { webcrypto } from "node:crypto";
import { NotebookStore, encode } from "../src/core.mjs";
globalThis.crypto ||= webcrypto;
class MemoryIO {
  files = new Map();
  folders = new Set([""]);
  async mkdir(path) {
    let current = "";
    for (const p of path.split("/")) {
      current = current ? current + "/" + p : p;
      this.folders.add(current);
    }
  }
  async read(path) {
    return this.files.has(path)
      ? Buffer.from(this.files.get(path), "base64").toString()
      : null;
  }
  async readBinary(path) {
    return this.files.get(path) ?? null;
  }
  async write(path, text) {
    return this.writeBinary(path, encode(text));
  }
  async writeBinary(path, data) {
    await this.mkdir(path.split("/").slice(0, -1).join("/"));
    this.files.set(path, data);
  }
  async list(path) {
    const prefix = path ? path + "/" : "";
    const result = new Map();
    for (const [p, dir] of [
      ...[...this.files.keys()].map((p) => [p, false]),
      ...[...this.folders].map((p) => [p, true]),
    ]) {
      if (p.startsWith(prefix) && p !== path) {
        const remainder = p.slice(prefix.length);
        if (remainder && !remainder.includes("/"))
          result.set(remainder, { name: remainder, dir });
      }
    }
    return [...result.values()];
  }
  async remove(path) {
    this.files.delete(path);
  }
  async purgeDeleted(path) {
    for (const p of [...this.files.keys()])
      if (p.startsWith(path + "/")) this.files.delete(p);
    for (const p of [...this.folders])
      if (p === path || p.startsWith(path + "/")) this.folders.delete(p);
  }
  async move(path, to) {
    if (this.files.has(to) || this.folders.has(to))
      throw new Error("destination exists");
    if (this.files.has(path)) {
      await this.writeBinary(to, this.files.get(path));
      this.files.delete(path);
    } else {
      for (const p of [...this.files.keys()])
        if (p.startsWith(path + "/")) {
          await this.writeBinary(to + p.slice(path.length), this.files.get(p));
          this.files.delete(p);
        }
      for (const p of [...this.folders])
        if (p === path || p.startsWith(path + "/")) {
          this.folders.delete(p);
          this.folders.add(to + p.slice(path.length));
        }
    }
  }
}
async function setup() {
  const io = new MemoryIO();
  let now = 100000;
  const store = new NotebookStore(io, () => now);
  await store.initialize();
  const book = await store.createBook("工作");
  book.path = "books/" + book.id;
  const note = await store.createNote(book, "记录");
  return { io, store, note, book, tick: (n) => (now += n) };
}

export { MemoryIO, setup };
