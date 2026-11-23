import test from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import {
  NotebookStore,
  references,
  attachmentPath,
  encode,
  DAY,
} from "../src/core.mjs";
globalThis.crypto ||= webcrypto;
import { MemoryIO, setup } from "./fixtures.mjs";
async function attachment(ctx) {
  const file = await ctx.store.importAttachment(
    ctx.note,
    "图片.png",
    encode("image"),
  );
  const markdown = `![图片](${file.path})\n`;
  await ctx.store.save(ctx.note, markdown);
  return file;
}

test("parses reference-style, escaped URLs, inline images and HTML", () => {
  const r = references(
    '[下载][a]\n\n[a]: <reference/report%20one.pdf>\n![x](./reference/x.png)\n<img src="reference/y.png">',
  );
  assert(r.has("reference/report one.pdf"));
  assert(r.has("reference/x.png"));
  assert(r.has("reference/y.png"));
});
test("rejects traversal, external links and invalid escapes", () => {
  for (const value of [
    "reference/../secret",
    "reference/%2e%2e/secret",
    "https://x/reference/a",
    "reference/a\\b",
    "reference/%ZZ",
  ])
    assert.equal(attachmentPath(value), null);
});
test("incomplete source edits retain attachments conservatively", () =>
  assert(references("![x](reference/photo.png").has("reference/photo.png")));
test("removing one of two references keeps attachment live", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, `[a](${f.path}) [b](${f.path})`);
  await c.store.save(c.note, `[b](${f.path})`);
  c.tick(60000);
  await c.store.maintenance();
  assert(await c.io.readBinary(`${c.note.path}/${f.path}`));
});
test("grace period then trash and undo restores original path", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "no attachment");
  c.tick(29000);
  await c.store.maintenance();
  assert(await c.io.readBinary(`${c.note.path}/${f.path}`));
  c.tick(2000);
  await c.store.maintenance();
  assert.equal(await c.io.readBinary(`${c.note.path}/${f.path}`), null);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
  await c.store.save(c.note, `[undo](${f.path})`);
  assert(await c.io.readBinary(`${c.note.path}/${f.path}`));
});
test("draft reference protects attachment during save debounce", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.store.setDraft(c.note.path, `[undo](${f.path})`);
  c.tick(60000);
  await c.store.maintenance();
  assert(await c.io.readBinary(`${c.note.path}/${f.path}`));
});
test("15 days measured from actual trash move, not import time", async () => {
  const c = await setup(),
    f = await attachment(c);
  c.tick(20 * DAY);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  await c.store.maintenance();
  c.tick(15 * DAY - 1);
  await c.store.maintenance();
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
  c.tick(2);
  await c.store.maintenance();
  assert.equal(
    await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`),
    null,
  );
});
test("offline sync and unresolved conflicts suspend permanent deletion", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  await c.store.maintenance();
  c.tick(16 * DAY);
  await c.store.maintenance(true);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
  c.store.state.conflicts = 1;
  await c.store.maintenance(false);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
});
test("restart recovers a journaled move without resetting retention", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  const original = c.io.move.bind(c.io);
  c.io.move = async (...args) => {
    await original(...args);
    throw new Error("crash after move");
  };
  await assert.rejects(c.store.maintenance());
  c.io.move = original;
  const recovered = new NotebookStore(c.io, () => 131000);
  await recovered.initialize();
  await recovered.maintenance();
  const meta = await recovered.json(`${c.note.path}/.attachments.json`);
  assert.equal(meta.entries[f.filename].trashSince, 131000);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
});
test("external edits are not overwritten, unsaved draft remains recoverable", async () => {
  const c = await setup();
  await c.io.write(`${c.note.path}/main.md`, "external edit");
  await assert.rejects(c.store.save(c.note, "my unsaved edit"), /其他程序/);
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "external edit");
  assert.equal(await c.io.read(`.drafts/${c.note.id}.md`), "my unsaved edit");
});
test("moving a note keeps Markdown and attachment together", async () => {
  const c = await setup(),
    f = await attachment(c);
  const book = await c.store.createBook("个人");
  book.path = "books/" + book.id;
  const path = await c.store.moveNote(c.note, book);
  assert(await c.io.readBinary(`${path}/${f.path}`));
  assert(await c.io.read(`${path}/main.md`));
});
test("deleted notes are restorable without losing attachments", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.deleteNote(c.note);
  const deleted = await c.store.deletedNotes();
  assert.equal(deleted.length, 1);
  await c.store.restoreNote(deleted[0]);
  assert(await c.io.readBinary(`${c.note.path}/${f.path}`));
});

test("explicit external resolution preserves both versions before adopting disk", async () => {
  const c = await setup();
  await c.io.write(`${c.note.path}/main.md`, "disk version");
  await assert.rejects(c.store.save(c.note, "window version"));
  const disk = await c.store.prepareExternalResolution(
    c.note,
    "window version",
    false,
  );
  assert.equal(disk, "disk version");
  const paths = [...c.io.files.keys()];
  assert(
    paths.some((x) => x.startsWith(".quarantine/") && x.endsWith("window.md")),
  );
  assert(
    paths.some((x) => x.startsWith(".quarantine/") && x.endsWith("disk.md")),
  );
  assert.equal(await c.io.read(`.drafts/${c.note.id}.md`), null);
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "disk version");
});

test("misplaced or duplicate attachments are reported without destructive guesses", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.io.write(`${c.note.path}/orphan.pdf`, "keep");
  await c.io.write(`${c.note.path}/.trash/${f.filename}`, "second copy");
  const report = await c.store.maintenance();
  assert(report[0].problems.some((x) => x.includes("根目录")));
  assert(report[0].problems.some((x) => x.includes("重复")));
  assert.equal(await c.io.read(`${c.note.path}/orphan.pdf`), "keep");
  assert(await c.io.readBinary(`${c.note.path}/reference/${f.filename}`));
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
});

test("persisted conflict checkpoint blocks purge even before state counter is updated", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  await c.store.maintenance();
  c.tick(16 * DAY);
  c.store.state.conflicts = 0;
  await c.store.put(".sync.json", {
    files: {},
    conflicts: [{ path: "main.md" }],
  });
  await c.store.maintenance(false);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
});

test("Quick Notes has the same stable identity on every library and survives reopening", async () => {
  const io = new MemoryIO();
  const store = new NotebookStore(io);
  await store.initialize();
  const book = await store.ensureQuickBook();
  const note = await store.createNote(book, "随手记");
  const file = await store.importAttachment(
    note,
    "附件.txt",
    encode("quick attachment"),
  );
  await store.save(note, `[附件](${file.path})`);
  await store.rememberPage(note, "quick-notes");
  const reopened = new NotebookStore(io);
  await reopened.initialize();
  await reopened.ensureQuickBook();
  const catalog = await reopened.catalog();
  assert.equal(catalog.books.length, 1);
  assert.equal(catalog.books[0].id, "quick-notes");
  assert.equal(catalog.notes[0].id, note.id);
  assert.equal(reopened.state.lastPage.noteId, note.id);
  assert.equal(await io.read(`${note.path}/${file.path}`), "quick attachment");
  const portable = await reopened.files();
  assert(portable.includes(`${note.path}/main.md`));
  assert(!portable.includes(".state.json"));
});

test("notebook delete and restore retain stable identities and attachment bytes", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.renameBook(c.book, "更名");
  await c.store.deleteBook(c.book);
  assert.equal((await c.store.catalog()).notes.length, 0);
  const [row] = await c.store.deletedBooks();
  assert.equal(await c.io.read(`${row.path}/${c.note.id}/${f.path}`), "image");
  await c.store.restoreBook(row);
  assert.equal((await c.store.catalog()).notes[0].id, c.note.id);
  assert.equal(
    (await c.store.catalog()).books.find((x) => x.id === c.book.id).title,
    "更名",
  );
  await assert.rejects(
    c.store.deleteBook(await c.store.ensureQuickBook()),
    /不能删除/,
  );
});

test("a separately deleted note can be restored after its notebook without creating an invisible orphan", async () => {
  const c = await setup();
  await c.store.deleteNote(c.note);
  const [note] = await c.store.deletedNotes();
  await c.store.deleteBook(c.book);
  await assert.rejects(c.store.restoreNote(note), /先恢复所属笔记本/);
  const [book] = await c.store.deletedBooks();
  await c.store.restoreBook(book);
  await c.store.restoreNote(note);
  assert.equal((await c.store.catalog()).notes[0].id, c.note.id);
});

test("permanent delete is limited to archived items and removes their attachments", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.deleteBook(c.book);
  const [row] = await c.store.deletedBooks();
  await c.store.purgeDeleted(row);
  assert.equal((await c.store.deletedBooks()).length, 0);
  assert.equal(await c.io.read(`${row.path}/${c.note.id}/${f.path}`), null);
  await assert.rejects(c.store.purgeDeleted({ path: c.book.path }), /只能/);
});
test("referenced attachments cannot be permanently purged", async () => {
  const c = await setup(),
    f = await attachment(c);
  await assert.rejects(c.store.purgeAttachment(c.note, f.filename), /回收站/);
});
test("manual attachment purge removes recycled bytes and metadata", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  await c.store.maintenance();
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
  await c.store.purgeAttachment(c.note, f.filename);
  assert.equal(
    await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`),
    null,
  );
  assert.equal(
    (await c.store.json(`${c.note.path}/.attachments.json`)).entries[
      f.filename
    ],
    undefined,
  );
});
test("manual purge rejects a recycled attachment referenced by an external edit", async () => {
  const c = await setup(),
    f = await attachment(c);
  await c.store.save(c.note, "removed");
  c.tick(31000);
  await c.store.maintenance();
  await c.io.write(`${c.note.path}/main.md`, `[restored](${f.path})`);
  await assert.rejects(c.store.purgeAttachment(c.note, f.filename), /引用/);
  assert(await c.io.readBinary(`${c.note.path}/.trash/${f.filename}`));
});

test("attachment links escape Chinese names, brackets and parentheses portably", async () => {
  const { attachmentLink, references } = await import("../src/core.mjs");
  const filename = "投标[版本] (+扫描版) 100%.pdf";
  const link = attachmentLink(filename, filename);
  assert(link.includes("%28") && link.includes("%29"));
  assert.deepEqual([...references(link)], ["reference/" + filename]);
});

test("repeat imports reuse identical bytes, recover recycled files, and keep changed content", async () => {
  const ctx = await setup();
  const first = await ctx.store.importAttachment(
    ctx.note,
    "report.pdf",
    encode("original"),
  );
  const again = await ctx.store.importAttachment(
    ctx.note,
    "report.pdf",
    encode("original"),
  );
  assert.equal(first.filename, again.filename);
  assert.equal((await ctx.io.list(`${ctx.note.path}/reference`)).length, 1);
  await ctx.store.recycleAttachment(ctx.note, first.filename);
  assert.equal(
    await ctx.io.readBinary(`${ctx.note.path}/reference/${first.filename}`),
    null,
  );
  const restored = await ctx.store.importAttachment(
    ctx.note,
    "report.pdf",
    encode("original"),
  );
  assert.equal(restored.filename, first.filename);
  assert.equal((await ctx.io.list(`${ctx.note.path}/.trash`)).length, 0);
  const different = await ctx.store.importAttachment(
    ctx.note,
    "report.pdf",
    encode("new content"),
  );
  assert.notEqual(different.filename, first.filename);
  assert.equal(
    await ctx.io.readBinary(`${ctx.note.path}/reference/${first.filename}`),
    encode("original"),
  );
});
test("direct recycle and permanent deletion respect current and recovery drafts", async () => {
  const ctx = await setup();
  const file = await attachment(ctx);
  await assert.rejects(
    ctx.store.recycleAttachment(ctx.note, file.filename),
    /引用/,
  );
  await ctx.store.save(ctx.note, "");
  ctx.store.setDraft(ctx.note.path, `[x](${file.path})`);
  await assert.rejects(
    ctx.store.recycleAttachment(ctx.note, file.filename),
    /引用/,
  );
  ctx.store.setDraft(ctx.note.path, "");
  await ctx.store.recycleAttachment(ctx.note, file.filename);
  await ctx.io.write(`.drafts/${ctx.note.id}.md`, `[x](${file.path})`);
  await assert.rejects(
    ctx.store.purgeAttachment(ctx.note, file.filename),
    /引用/,
  );
  await ctx.io.remove(`.drafts/${ctx.note.id}.md`);
  await ctx.store.purgeAttachment(ctx.note, file.filename);
  assert.equal(
    await ctx.io.readBinary(`${ctx.note.path}/.trash/${file.filename}`),
    null,
  );
});
test("reconcile removes ghost attachment entries but retains referenced missing-file diagnostics", async () => {
  const ctx = await setup();
  const file = await attachment(ctx);
  await ctx.io.remove(`${ctx.note.path}/${file.path}`);
  const missing = await ctx.store.reconcile(ctx.note.path);
  assert(missing.state.entries[file.filename]);
  assert(missing.problems.some((x) => x.includes("缺失")));
  await ctx.store.save(ctx.note, "");
  const cleaned = await ctx.store.reconcile(ctx.note.path);
  assert.equal(Object.keys(cleaned.state.entries).length, 0);
});
