import test from "node:test";
import assert from "node:assert/strict";
import { NotebookStore, encode, decode, digest } from "../src/core.mjs";
import { WebDAVSync } from "../src/sync.mjs";
import { MemoryIO, setup } from "./fixtures.mjs";
class Remote {
  files = new Map();
  version = 0;
  async list() {
    return [...this.files.keys()];
  }
  async request(method, path, data, headers = {}) {
    const file = this.files.get(path),
      etag = file?.etag || "";
    if (method === "GET")
      return { status: file ? 200 : 404, data: file?.data || "", etag };
    if (method === "MKCOL") return { status: 201, data: "", etag: "" };
    if (
      (headers["If-Match"] && headers["If-Match"] !== etag) ||
      (headers["If-None-Match"] === "*" && file)
    )
      return { status: 412, data: "", etag };
    if (method === "DELETE") {
      this.files.delete(path);
      return { status: 204, data: "", etag: "" };
    }
    if (method === "PUT") {
      const etag = '"' + ++this.version + '"';
      this.files.set(path, { data, etag });
      return { status: file ? 204 : 201, data: "", etag };
    }
  }
}
const issues = (report) => report.items.flatMap((item) => item.errors);
test("first sync uploads plain Markdown and attachment, records checkpoint", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  const result = await sync.run();
  assert.equal(result.conflicts.length, 0);
  assert.equal(
    decode(remote.files.get(`${c.note.path}/main.md`).data),
    c.note.markdown,
  );
  assert((await c.store.json(".sync.json")).files[`${c.note.path}/main.md`]);
  assert(!remote.files.has(".notebook-lock.json"));
});
test("two-sided edits preserve local and remote conflict copies", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.store.save(c.note, "local changed");
  await remote.request(
    "PUT",
    `${c.note.path}/main.md`,
    encode("remote changed"),
  );
  const result = await sync.run();
  assert.equal(result.conflicts.length, 1);
  const conflict = result.conflicts[0];
  assert.equal(
    await c.io.read(`.conflicts/${conflict.id}/remote`),
    "remote changed",
  );
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "local changed");
  assert.equal((await sync.run()).conflicts.length, 1);
  await sync.resolve(conflict, false);
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "remote changed");
});
test("remote deletion quarantines unchanged local file", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await remote.request("DELETE", `${c.note.path}/main.md`);
  await sync.run();
  assert.equal(await c.io.read(`${c.note.path}/main.md`), null);
  assert(
    [...c.io.files.keys()].some(
      (x) => x.startsWith(".quarantine/") && x.endsWith("main.md"),
    ),
  );
});
test("local deletion propagates only against unchanged remote baseline", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.io.remove(`${c.note.path}/main.md`);
  await sync.run();
  assert(!remote.files.has(`${c.note.path}/main.md`));
});
test("active remote lease prevents simultaneous writers", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await remote.request(
    "PUT",
    ".notebook-lock.json",
    encode(JSON.stringify({ owner: "other", expires: Date.now() + 300000 })),
  );
  const original = remote.files.get(".notebook-lock.json");
  await assert.rejects(sync.run(), { code: "SYNC_BUSY" });
  assert.equal(remote.files.get(".notebook-lock.json"), original);
  assert.equal(await c.store.json(".sync.json"), null);
  await remote.request("DELETE", ".notebook-lock.json", null, {
    "If-Match": original.etag,
  });
  await sync.run();
  assert(!remote.files.has(".notebook-lock.json"));
});
test("ETag race preserves fresh conflict versions without updating checkpoint", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.store.save(c.note, "updated");
  const original = remote.request.bind(remote);
  let raced = false;
  remote.request = async (method, path, data, headers) => {
    if (!raced && method === "PUT" && path.endsWith("main.md")) {
      raced = true;
      await original("PUT", path, encode("another device won the race"));
    }
    return original(method, path, data, headers);
  };
  const result = await sync.run();
  assert.equal(result.conflicts.length, 1);
  assert.equal(
    await c.io.read(`.conflicts/${result.conflicts[0].id}/remote`),
    "another device won the race",
  );
  const state = await c.store.json(".sync.json");
  assert.notEqual(
    state.files[`${c.note.path}/main.md`].hash,
    await digest(encode("updated")),
  );
});

test("missing ETag aborts without touching either version", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.store.save(c.note, "local update");
  const original = remote.request.bind(remote);
  remote.request = async (...args) => {
    const result = await original(...args);
    if (args[0] === "GET" && args[1].endsWith("main.md")) result.etag = "";
    return result;
  };
  assert(issues(await sync.run()).some((x) => /ETag/.test(x.message)));
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "local update");
  assert.notEqual(
    decode(remote.files.get(`${c.note.path}/main.md`).data),
    "local update",
  );
});
test("retry after an interrupted checkpoint preserves all files", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote, undefined, undefined, {
      retries: 0,
    });
  const original = remote.request.bind(remote);
  let uploads = 0;
  remote.request = async (...args) => {
    if (args[0] === "PUT" && args[1].startsWith("books/") && ++uploads === 2)
      throw new Error("connection lost");
    return original(...args);
  };
  assert(issues(await sync.run()).some((x) => x.code === "SYNC_RETRY"));
  remote.request = original;
  await sync.run();
  assert.equal(
    decode(remote.files.get(`${c.note.path}/main.md`).data),
    c.note.markdown,
  );
  assert.equal((await c.store.json(".sync.json")).conflicts.length, 0);
});
test("remote-only file is downloaded and checkpointed", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await remote.request("PUT", "books/remote/main.md", encode("remote only"));
  await sync.run();
  assert.equal(await c.io.read("books/remote/main.md"), "remote only");
});
test("expired lease can be replaced conditionally", async () => {
  const c = await setup(),
    remote = new Remote(),
    sync = new WebDAVSync(c.store, remote);
  await remote.request(
    "PUT",
    ".notebook-lock.json",
    encode(JSON.stringify({ owner: "crashed", expires: Date.now() - 1000 })),
  );
  await sync.run();
  assert(!remote.files.has(".notebook-lock.json"));
});

test("Quick Notes merges across new devices as one ordinary notebook", async () => {
  const remote = new Remote();
  const first = new NotebookStore(new MemoryIO());
  await first.initialize();
  const book = await first.ensureQuickBook();
  const note = await first.createNote(book, "随手记");
  const file = await first.importAttachment(
    note,
    "附件.txt",
    encode("portable attachment"),
  );
  await first.save(note, `[附件](${file.path})`);
  await new WebDAVSync(first, remote).run();
  const second = new NotebookStore(new MemoryIO());
  await second.initialize();
  const result = await new WebDAVSync(second, remote).run();
  assert.equal(result.conflicts.length, 0);
  const catalog = await second.catalog();
  assert.equal(catalog.books.length, 1);
  assert.equal(catalog.books[0].id, "quick-notes");
  assert.equal(catalog.notes[0].id, note.id);
  assert.equal(
    await second.io.read(`${note.path}/${file.path}`),
    "portable attachment",
  );
});

test("typing during download keeps disk, draft and online versions", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await r.request("PUT", path, encode("online changed"));
  const request = r.request.bind(r);
  r.request = async (...args) => {
    const value = await request(...args);
    if (args[0] === "GET" && args[1] === path)
      c.store.setDraft(c.note.path, "unsaved typing");
    return value;
  };
  const { conflicts } = await sync.run();
  assert.equal(conflicts.length, 1);
  assert.equal(await c.io.read(path), c.note.markdown);
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/draft.md`),
    "unsaved typing",
  );
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/remote`),
    "online changed",
  );
});
test("external edit during network wait is retained as a conflict", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await r.request("PUT", path, encode("online changed"));
  const request = r.request.bind(r);
  r.request = async (...args) => {
    const value = await request(...args);
    if (args[0] === "GET" && args[1] === path)
      await c.io.write(path, "external edit");
    return value;
  };
  assert.equal((await sync.run()).conflicts.length, 1);
  assert.equal(await c.io.read(path), "external edit");
});
test("offline edit against deletion preserves local data", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.save(c.note, "offline edit");
  await r.request("DELETE", path);
  const { conflicts } = await sync.run();
  assert.equal(conflicts.length, 1);
  assert.equal(await c.io.read(path), "offline edit");
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/local`),
    "offline edit",
  );
});
test("manual merge retains both originals and publishes merged Markdown", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.save(c.note, "local paragraph");
  await r.request("PUT", path, encode("online paragraph"));
  const { conflicts } = await sync.run();
  await sync.resolve(conflicts[0], true, "local paragraph\nonline paragraph\n");
  assert.equal(
    decode(r.files.get(path).data),
    "local paragraph\nonline paragraph\n",
  );
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/local`),
    "local paragraph",
  );
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/remote`),
    "online paragraph",
  );
});
test("recycle uploads precede deletions and another device can restore a notebook", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  await c.store.deleteBook(c.book);
  const request = r.request.bind(r),
    ops = [];
  r.request = async (...args) => {
    ops.push(args);
    return request(...args);
  };
  await sync.run();
  const puts = ops
    .map((x, i) =>
      x[0] === "PUT" && x[1].startsWith(".deleted-books/") ? i : -1,
    )
    .filter((x) => x >= 0);
  const deletes = ops
    .map((x, i) => (x[0] === "DELETE" && x[1].startsWith("books/") ? i : -1))
    .filter((x) => x >= 0);
  assert(Math.max(...puts) < Math.min(...deletes));
  const other = new NotebookStore(new MemoryIO());
  await other.initialize();
  await new WebDAVSync(other, r).run();
  const [row] = await other.deletedBooks();
  assert(row);
  await other.restoreBook(row);
  assert.equal((await other.catalog()).notes[0].id, c.note.id);
});
test("independent offline edits on two devices preserve both versions", async () => {
  const a = await setup(),
    r = new Remote();
  await new WebDAVSync(a.store, r).run();
  const b = new NotebookStore(new MemoryIO());
  await b.initialize();
  const sync = new WebDAVSync(b, r);
  await sync.run();
  const note = (await b.catalog()).notes[0];
  await a.store.save(a.note, "device A");
  await b.save(note, "device B");
  await new WebDAVSync(a.store, r).run();
  const { conflicts } = await sync.run();
  assert.equal(conflicts.length, 1);
  assert.equal(await b.io.read(`${note.path}/main.md`), "device B");
  assert.equal(
    await b.io.read(`.conflicts/${conflicts[0].id}/remote`),
    "device A",
  );
});

test("expired own lease stops rather than reviving a stale writer", async () => {
  const c = await setup(),
    sync = new WebDAVSync(c.store, new Remote()),
    lock = await sync.lock();
  lock.expires = Date.now() - 1;
  await assert.rejects(sync.renew(lock), /过期/);
});
test("deleting a remotely edited notebook retains all live remote metadata", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  await c.store.deleteBook(c.book);
  await r.request(
    "PUT",
    `${c.note.path}/main.md`,
    encode("other device edited"),
  );
  const { conflicts } = await sync.run();
  assert.equal(conflicts.length, 1);
  assert(r.files.has(`${c.book.path}/book.json`));
  assert(r.files.has(`${c.note.path}/note.json`));
  assert.equal(
    decode(r.files.get(`${c.note.path}/main.md`).data),
    "other device edited",
  );
});

test("a third-device edit refreshes conflict snapshots without accepting a stale merge", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.save(c.note, "device B");
  await r.request("PUT", path, encode("device A"));
  const { conflicts } = await sync.run();
  await r.request("PUT", path, encode("device C"));
  await assert.rejects(
    sync.resolve(conflicts[0], true, "stale merge"),
    /重新打开/,
  );
  assert.equal(await c.io.read(path), "device B");
  assert.equal(decode(r.files.get(path).data), "device C");
  assert.equal(
    await c.io.read(`.conflicts/${conflicts[0].id}/remote`),
    "device C",
  );
  assert(
    (await c.io.list(`.conflicts/${conflicts[0].id}`)).some((x) =>
      x.name.startsWith("remote-"),
    ),
  );
  await assert.rejects(
    sync.resolve(conflicts[0], true, "stale merge"),
    /重新打开/,
  );
  const updated = (await c.store.json(".sync.json")).conflicts[0];
  await sync.resolve(updated, true, "reviewed merge");
  assert.equal(decode(r.files.get(path).data), "reviewed merge");
});

test("re-reading conflict versions only refreshes snapshots and preserves both originals", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  const path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.save(c.note, "local edit");
  await r.request("PUT", path, encode("old online edit"));
  const conflict = (await sync.run()).conflicts[0];
  await r.request("PUT", path, encode("new online edit"));
  const calls = [],
    request = r.request.bind(r);
  r.request = async (...args) => {
    calls.push(args.slice(0, 2));
    return request(...args);
  };
  const reviewed = await sync.review(conflict);
  assert.notEqual(reviewed.remoteHash, conflict.remoteHash);
  assert.deepEqual(calls, [["GET", path]]);
  assert.equal(await c.io.read(path), "local edit");
  assert.equal(
    await c.io.read(`.conflicts/${conflict.id}/local`),
    "local edit",
  );
  assert.equal(
    await c.io.read(`.conflicts/${conflict.id}/remote`),
    "new online edit",
  );
  const archived = (await c.io.list(`.conflicts/${conflict.id}`)).find((x) =>
    x.name.startsWith("remote-"),
  );
  assert.equal(
    await c.io.read(`.conflicts/${conflict.id}/${archived.name}`),
    "old online edit",
  );
  await sync.review(reviewed);
  assert.equal(
    (await c.io.list(`.conflicts/${conflict.id}`)).filter((x) =>
      x.name.startsWith("remote-"),
    ).length,
    1,
  );
  await sync.resolve(reviewed, false);
  assert.equal(await c.io.read(path), "new online edit");
  assert.equal(await sync.review(reviewed), null);
});

test("failed conflict re-read leaves the local file and comparison snapshots intact", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  const path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.save(c.note, "local edit");
  await r.request("PUT", path, encode("online edit"));
  const conflict = (await sync.run()).conflicts[0],
    before = await c.io.read(".sync.json");
  r.request = async () => ({ status: 403, data: "", etag: "" });
  await assert.rejects(sync.review(conflict), /403/);
  assert.equal(await c.io.read(path), "local edit");
  assert.equal(await c.io.read(".sync.json"), before);
  assert.equal(
    await c.io.read(`.conflicts/${conflict.id}/remote`),
    "online edit",
  );
});

class VersionedRemote extends Remote {
  calls = [];
  async list() {
    return [...this.files].map(([path, value]) => ({ path, etag: value.etag }));
  }
  async request(...args) {
    this.calls.push(args);
    return super.request(...args);
  }
}
test("unchanged ETags skip file downloads and per-file lease writes", async () => {
  const c = await setup(),
    remote = new VersionedRemote();
  const sync = new WebDAVSync(c.store, remote);
  await sync.run();
  remote.calls = [];
  const result = await sync.run();
  assert(result.skipped > 0);
  assert.equal(
    remote.calls.filter(
      ([method, path]) => method === "GET" && path.startsWith("books/"),
    ).length,
    0,
  );
  assert.equal(
    remote.calls.filter(
      ([method, path]) => method === "PUT" && path === ".notebook-lock.json",
    ).length,
    1,
  );
});
test("ETag optimization retains two-device conflict detection and remote deletions", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.store.save(c.note, "local update");
  await remote.request(
    "PUT",
    `${c.note.path}/main.md`,
    encode("remote update"),
  );
  assert.equal((await sync.run()).conflicts.length, 1);
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "local update");
  const conflict = (await c.store.json(".sync.json")).conflicts[0];
  await sync.resolve(conflict, false);
  c.store.setDraft(c.note.path, await c.io.read(`${c.note.path}/main.md`));
  await remote.request("DELETE", `${c.note.path}/main.md`);
  await sync.run();
  assert.equal(await c.io.read(`${c.note.path}/main.md`), null);
  assert(
    [...c.io.files.keys()].some((path) => path.startsWith(".quarantine/")),
  );
});
test("a changed local file cannot be skipped based on a matching remote ETag", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  await c.store.save(c.note, "new local content");
  await sync.run();
  assert.equal(
    decode(remote.files.get(`${c.note.path}/main.md`).data),
    "new local content",
  );
});
test("folder creation is shared between uploads but resets each sync", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  const created = remote.calls
    .filter(([method]) => method === "MKCOL")
    .map(([, path]) => path);
  assert.equal(created.length, new Set(created).size);
  remote.calls = [];
  await c.io.write(`${c.note.path}/reference/new.txt`, "new attachment");
  await sync.run();
  assert(
    remote.calls.some(
      ([method, path]) => method === "MKCOL" && path === "books/",
    ),
  );
});
test("missing listing ETags falls back to content comparison", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  await sync.run();
  remote.calls = [];
  remote.list = async () => [...remote.files.keys()];
  await sync.run();
  assert(
    remote.calls.some(
      ([method, path]) => method === "GET" && path.endsWith("main.md"),
    ),
  );
});
test("periodic lease refresh remains conditional and stale owners stop", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  const lock = await sync.lock();
  lock.expires = Date.now() + 30000;
  await sync.renew(lock);
  assert(lock.expires > Date.now() + 240000);
  await remote.request(
    "PUT",
    lock.path,
    encode(JSON.stringify({ owner: "other" })),
  );
  await assert.rejects(sync.renew(lock, true), /失效/);
});

test("large unchanged libraries do not generate per-file remote requests", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  for (let i = 0; i < 100; i++)
    await c.io.write(`${c.note.path}/reference/${i}.bin`, `bytes ${i}`);
  await sync.run();
  remote.calls = [];
  const result = await sync.run();
  assert(result.skipped >= 100);
  assert.equal(remote.calls.length, 3); // Acquire, verify, release; listing is separate.
});
test("remote-only download preserves a local file created during lease validation", async () => {
  const c = await setup(),
    remote = new VersionedRemote(),
    sync = new WebDAVSync(c.store, remote);
  const path = "books/new/main.md";
  await remote.request("PUT", path, encode("online original"));
  const request = remote.request.bind(remote);
  let downloaded = false;
  remote.request = async (...args) => {
    const response = await request(...args);
    if (args[0] === "GET" && args[1] === path) downloaded = true;
    if (downloaded && args[0] === "GET" && args[1] === ".notebook-lock.json")
      await c.io.write(path, "external new file");
    return response;
  };
  const result = await sync.run();
  assert(result.conflicts.some((x) => x.path === path));
  assert.equal(await c.io.read(path), "external new file");
});

const fast = { sleep: async () => {}, random: () => 0 };
test("423 upload retries retain version conditions; exhaustion keeps checkpoint and local edit", async () => {
  const c = await setup(),
    r = new Remote(),
    path = `${c.note.path}/main.md`;
  const sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  await sync.run();
  await c.store.save(c.note, "pending local edit");
  const original = r.request.bind(r),
    conditions = [];
  let locked = 2;
  r.request = async (...args) => {
    if (args[0] === "PUT" && args[1] === path) {
      conditions.push(args[3]);
      if (locked-- > 0) return { status: 423 };
    }
    return original(...args);
  };
  await sync.run();
  assert.equal(decode(r.files.get(path).data), "pending local edit");
  assert.equal(conditions.length, 3);
  assert(conditions.every((h) => h["If-Match"] === conditions[0]["If-Match"]));
  await c.store.save(c.note, "still local");
  locked = Infinity;
  const checkpoint = (await c.store.json(".sync.json")).files[path];
  assert(
    issues(await sync.run()).some(
      (x) => x.code === "DAV_LOCKED" && x.status === 423,
    ),
  );
  assert.deepEqual((await c.store.json(".sync.json")).files[path], checkpoint);
  assert.equal(await c.io.read(path), "still local");
  assert.equal((await c.store.json(".sync.json")).conflicts.length, 0);
});
test("committed upload with lost reply is reconciled without a false conflict", async () => {
  const c = await setup(),
    r = new Remote(),
    path = `${c.note.path}/main.md`;
  const sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  await sync.run();
  await c.store.save(c.note, "committed once");
  const original = r.request.bind(r);
  let lost = false;
  r.request = async (...args) => {
    const result = await original(...args);
    if (!lost && args[0] === "PUT" && args[1] === path) {
      lost = true;
      throw Error("lost reply");
    }
    return result;
  };
  assert.equal((await sync.run()).conflicts.length, 0);
  assert.equal(
    (await c.store.json(".sync.json")).files[path].hash,
    await digest(encode("committed once")),
  );
});
test("lost lock creation and renewal replies recognize the same owner", async () => {
  const c = await setup(),
    r = new Remote(),
    original = r.request.bind(r);
  const sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  let lost = false;
  r.request = async (...args) => {
    const result = await original(...args);
    if (!lost && args[0] === "PUT" && args[1] === ".notebook-lock.json") {
      lost = true;
      throw Error("lost lock response");
    }
    return result;
  };
  const lock = await sync.lock();
  lock.expires = Date.now() + 30000;
  lost = false;
  await sync.renew(lock);
  assert.equal(lock.etag, r.files.get(lock.path).etag);
  assert(lock.expires > Date.now() + 240000);
});
test("takeover during backoff stops writing and never releases the new owner's lock", async () => {
  const c = await setup(),
    r = new Remote(),
    path = `${c.note.path}/main.md`,
    original = r.request.bind(r);
  let puts = 0;
  const sync = new WebDAVSync(c.store, r, undefined, undefined, {
    ...fast,
    sleep: async () => {
      await original(
        "PUT",
        ".notebook-lock.json",
        encode(
          JSON.stringify({ owner: "new-owner", expires: Date.now() + 300000 }),
        ),
      );
    },
  });
  await sync.run();
  await c.store.save(c.note, "do not overwrite");
  r.request = async (...args) => {
    if (args[0] === "PUT" && args[1] === path) {
      puts++;
      return { status: 423 };
    }
    return original(...args);
  };
  await assert.rejects(sync.run(), /失效/);
  assert.equal(puts, 1);
  assert.equal(
    JSON.parse(decode(r.files.get(".notebook-lock.json").data)).owner,
    "new-owner",
  );
});
test("release retries temporary locks and reports a persistent failure", async () => {
  const c = await setup(),
    r = new Remote(),
    original = r.request.bind(r);
  const sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  let blocked = 2;
  r.request = async (...args) => {
    if (
      args[0] === "DELETE" &&
      args[1] === ".notebook-lock.json" &&
      blocked-- > 0
    )
      return { status: 423 };
    return original(...args);
  };
  assert.equal((await sync.run()).warning, null);
  assert(!r.files.has(".notebook-lock.json"));
  blocked = Infinity;
  assert.match((await sync.run()).warning, /同步锁暂未释放/);
  assert(r.files.has(".notebook-lock.json"));
});
test("unresolved conflicts do not block unrelated notes or create duplicate conflicts", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  const other = await c.store.createNote(c.book, "other");
  await sync.run();
  await c.store.save(c.note, "local conflicting");
  await r.request(
    "PUT",
    `${c.note.path}/main.md`,
    encode("remote conflicting"),
  );
  assert.equal((await sync.run()).conflicts.length, 1);
  await c.store.save(other, "independent update");
  assert.equal((await sync.run()).conflicts.length, 1);
  assert.equal(
    decode(r.files.get(`${other.path}/main.md`).data),
    "independent update",
  );
  assert.equal(await c.io.read(`${c.note.path}/main.md`), "local conflicting");
});
test("suddenly empty remote or local inventory cannot propagate mass deletions", async () => {
  for (const side of ["remote", "local"]) {
    const c = await setup(),
      r = new Remote(),
      sync = new WebDAVSync(c.store, r);
    await sync.run();
    const checkpoint = await c.store.json(".sync.json");
    if (side === "remote") r.files.clear();
    else
      for (const path of [...c.io.files.keys()])
        if (path.startsWith("books/")) c.io.files.delete(path);
    await assert.rejects(sync.run(), { code: "SYNC_DELETION_GUARD" });
    assert.deepEqual(await c.store.json(".sync.json"), checkpoint);
    if (side === "remote")
      assert.equal(await c.io.read(`${c.note.path}/main.md`), c.note.markdown);
    else assert(r.files.has(`${c.note.path}/main.md`));
  }
});
test("90 percent disappearance is blocked while normal recycle moves still work", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  for (let i = 0; i < 12; i++) await c.store.createNote(c.book, "note " + i);
  await sync.run();
  const saved = new Map(r.files);
  for (const path of r.files.keys())
    if (path.startsWith(c.book.path + "/") && !path.endsWith("book.json"))
      r.files.delete(path);
  await assert.rejects(sync.run(), { code: "SYNC_DELETION_GUARD" });
  r.files = saved;
  await c.store.deleteBook(c.book);
  assert.equal((await sync.run()).conflicts.length, 0);
  assert(
    [...r.files.keys()].some((path) => path.startsWith(".deleted-books/")),
  );
});
test("late DELETE race retains note and book metadata alongside the fresh body", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    path = `${c.note.path}/main.md`;
  await sync.run();
  await c.store.deleteBook(c.book);
  const original = r.request.bind(r);
  let raced = false;
  r.request = async (...args) => {
    if (!raced && args[0] === "DELETE" && args[1] === path) {
      raced = true;
      await original("PUT", path, encode("late online edit"));
    }
    return original(...args);
  };
  assert((await sync.run()).conflicts.some((c) => c.path === path));
  assert(r.files.has(`${c.book.path}/book.json`));
  assert(r.files.has(`${c.note.path}/note.json`));
  assert.equal(decode(r.files.get(path).data), "late online edit");
});
test("malformed leases are never overwritten as expired leases", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await r.request("PUT", ".notebook-lock.json", encode('{"owner":"someone"}'));
  const original = r.files.get(".notebook-lock.json");
  await assert.rejects(sync.run(), /锁信息无效/);
  assert.equal(r.files.get(".notebook-lock.json"), original);
});

test("a persistently locked note does not block independent edits or advance its checkpoint", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, {
      sleep: async () => {},
    });
  const other = await c.store.createNote(c.book, "independent");
  await sync.run();
  const path = `${c.note.path}/main.md`,
    before = await c.store.json(".sync.json"),
    lastSync = c.store.state.lastSync,
    original = r.request.bind(r);
  await c.store.save(c.note, "waiting for Text to unlock");
  await c.store.save(other, "another note must still sync");
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === path ? { status: 423 } : original(...args);
  const report = await sync.run();
  assert.equal(report.failed, 1);
  assert(issues(report).some((x) => x.code === "DAV_LOCKED"));
  assert(report.items.find((x) => x.id === other.id).complete);
  assert.equal(
    decode(r.files.get(`${other.path}/main.md`).data),
    "another note must still sync",
  );
  assert.equal(await c.io.read(path), "waiting for Text to unlock");
  assert.deepEqual(
    (await c.store.json(".sync.json")).files[path],
    before.files[path],
  );
  assert.equal(c.store.state.lastSync, lastSync);
  assert.deepEqual((await c.store.json(".sync-diagnostics.json")).pending, [
    { path, status: 423 },
  ]);
  assert(!r.files.has(".notebook-lock.json"));
  r.request = original;
  await sync.run();
  assert.equal(decode(r.files.get(path).data), "waiting for Text to unlock");
});

test("a locked body deletion keeps its online note and notebook metadata", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, {
      sleep: async () => {},
    });
  await sync.run();
  const path = `${c.note.path}/main.md`,
    original = r.request.bind(r);
  await c.store.deleteBook(c.book);
  r.request = async (...args) =>
    args[0] === "DELETE" && args[1] === path
      ? { status: 423 }
      : original(...args);
  assert(issues(await sync.run()).some((x) => x.code === "DAV_LOCKED"));
  assert(r.files.has(path));
  assert(r.files.has(`${c.note.path}/note.json`));
  assert(r.files.has(`${c.book.path}/book.json`));
  assert(
    [...r.files.keys()].some(
      (p) => p.startsWith(".deleted-books/") && p.endsWith("/main.md"),
    ),
  );
});

test("an attachment failure fails its whole note while another complete note publishes", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  const other = await c.store.createNote(c.book, "complete note");
  const bad = await c.store.importAttachment(
    c.note,
    "bad.pdf",
    encode("locked attachment"),
  );
  const good = await c.store.importAttachment(
    other,
    "good.txt",
    encode("good attachment"),
  );
  await c.store.save(c.note, `[bad](${bad.path})`);
  await c.store.save(other, `[good](${good.path})`);
  const original = r.request.bind(r),
    badPath = `${c.note.path}/${bad.path}`;
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === badPath
      ? { status: 403 }
      : original(...args);
  const report = await sync.run();
  assert.equal(report.failed, 1);
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(report.items.find((x) => x.id === other.id).complete);
  assert(!r.files.has(`${c.note.path}/main.md`));
  assert(!r.files.has(`${c.note.path}/note.json`));
  for (const path of (await c.store.files()).filter((x) =>
    x.startsWith(other.path + "/"),
  ))
    assert.equal(r.files.get(path)?.data, await c.io.readBinary(path), path);
  assert.equal(issues(report)[0].status, 403);
  assert(!issues(report)[0].retryable);
});

test("several item errors are reported while an unrelated update and deletion finish", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  const denied = await c.store.createNote(c.book, "denied"),
    good = await c.store.createNote(c.book, "good"),
    removed = await c.store.createNote(c.book, "removed");
  await sync.run();
  await c.store.save(c.note, "locked update");
  await c.store.save(denied, "denied update");
  await c.store.save(good, "good update");
  await c.store.deleteNote(removed);
  const original = r.request.bind(r);
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === `${c.note.path}/main.md`
      ? { status: 423 }
      : args[0] === "PUT" && args[1] === `${denied.path}/main.md`
        ? { status: 403 }
        : original(...args);
  const report = await sync.run();
  assert.equal(report.failed, 2);
  assert.deepEqual(
    new Set(issues(report).map((x) => x.status)),
    new Set([423, 403]),
  );
  assert.equal(decode(r.files.get(`${good.path}/main.md`).data), "good update");
  assert(report.items.find((x) => x.id === good.id).complete);
  assert(report.items.find((x) => x.id === removed.id).complete);
  assert(!r.files.has(`${removed.path}/main.md`));
  assert(r.files.has(`.deleted/${removed.id}/main.md`));
  assert.equal((await c.store.json(".sync-report.json")).failed, 2);
});

test("a failed move is one pending item and keeps its complete old location", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  const file = await c.store.importAttachment(
    c.note,
    "file.pdf",
    encode("original bytes"),
  );
  await c.store.save(c.note, `[file](${file.path})`);
  await sync.run();
  const book = await c.store.ensureQuickBook(),
    dest = await c.store.moveNote(c.note, book),
    original = r.request.bind(r);
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === `${dest}/${file.path}`
      ? { status: 507 }
      : original(...args);
  const report = await sync.run();
  assert.equal(report.items.filter((x) => x.id === c.note.id).length, 1);
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  for (const suffix of ["main.md", "note.json", file.path])
    assert(r.files.has(`${c.note.path}/${suffix}`));
  assert(!r.files.has(`${dest}/main.md`));
  r.request = original;
  assert.equal((await sync.run()).failed, 0);
  assert(r.files.has(`${dest}/main.md`));
  assert(!r.files.has(`${c.note.path}/main.md`));
});

async function promptLocalOperation(operation) {
  let timer;
  try {
    return await Promise.race([
      operation(),
      new Promise(
        (_, reject) =>
          (timer = setTimeout(
            () => reject(Error("local operation blocked by network")),
            500,
          )),
      ),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
test("a stalled network request does not block saving and creating local notes", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    other = await c.store.createNote(c.book, "other");
  await sync.run();
  let enter, release;
  const entered = new Promise((resolve) => (enter = resolve)),
    gate = new Promise((resolve) => (release = resolve)),
    original = r.request.bind(r);
  r.request = async (...args) => {
    if (args[0] === "GET" && args[1] === `${c.note.path}/main.md`) {
      enter();
      await gate;
    }
    return original(...args);
  };
  const running = sync.run();
  await entered;
  try {
    await promptLocalOperation(() =>
      c.store.save(other, "saved while network waits"),
    );
    const newNote = await promptLocalOperation(() =>
      c.store.createNote(c.book, "new while waiting"),
    );
    assert((await c.store.catalog()).notes.some((x) => x.id === newNote.id));
    assert.equal(
      await c.io.read(`${other.path}/main.md`),
      "saved while network waits",
    );
  } finally {
    release();
  }
  await running;
});

test("typing and saving during upload keeps the latest local text and marks the note pending", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  await c.store.save(c.note, "upload snapshot");
  let enter, release;
  const entered = new Promise((resolve) => (enter = resolve)),
    gate = new Promise((resolve) => (release = resolve)),
    original = r.request.bind(r),
    path = `${c.note.path}/main.md`;
  r.request = async (...args) => {
    if (args[0] === "PUT" && args[1] === path) {
      enter();
      await gate;
    }
    return original(...args);
  };
  const running = sync.run();
  await entered;
  try {
    await promptLocalOperation(() =>
      c.store.save(c.note, "newer local typing"),
    );
  } finally {
    release();
  }
  const report = await running;
  assert.equal(await c.io.read(path), "newer local typing");
  assert.equal(decode(r.files.get(path).data), "upload snapshot");
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(issues(report).some((x) => x.code === "LOCAL_CHANGED"));
  r.request = original;
  assert.equal((await sync.run()).failed, 0);
  assert.equal(decode(r.files.get(path).data), "newer local typing");
});

test("a denied new notebook does not publish invisible children or block another notebook", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast),
    book = await c.store.ensureQuickBook(),
    other = await c.store.createNote(book, "visible");
  const original = r.request.bind(r);
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === `${c.book.path}/book.json`
      ? { status: 403 }
      : original(...args);
  const report = await sync.run();
  assert(!r.files.has(`${c.note.path}/main.md`));
  assert(
    report.items
      .find((x) => x.id === c.note.id)
      .errors.some((x) => x.code === "SYNC_DEPENDENCY"),
  );
  assert(report.items.find((x) => x.id === other.id).complete);
  assert(r.files.has(`${other.path}/note.json`));
});

test("an unreadable remote subtree is not a deletion and other items still finish", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    other = await c.store.createNote(c.book, "other");
  await sync.run();
  await c.store.save(other, "independent edit");
  const previous = await c.store.json(".sync.json"),
    list = r.list.bind(r);
  r.list = async () => {
    const paths = (await list()).filter(
      (x) => !x.startsWith(c.note.path + "/"),
    );
    paths.errors = [
      {
        path: c.note.path + "/",
        code: "DAV_ERROR",
        status: 403,
        message: "folder denied",
      },
    ];
    return paths;
  };
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(report.items.find((x) => x.id === other.id).complete);
  assert.equal(await c.io.read(`${c.note.path}/main.md`), c.note.markdown);
  assert.deepEqual(
    (await c.store.json(".sync.json")).files[`${c.note.path}/main.md`],
    previous.files[`${c.note.path}/main.md`],
  );
});

test("conflict resolution does not block unrelated local edits while waiting for DAV", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r),
    other = await c.store.createNote(c.book, "other");
  await sync.run();
  await c.store.save(c.note, "local");
  await r.request("PUT", `${c.note.path}/main.md`, encode("remote"));
  const conflict = (await sync.run()).conflicts[0],
    original = r.request.bind(r);
  let enter, release;
  const entered = new Promise((resolve) => (enter = resolve)),
    gate = new Promise((resolve) => (release = resolve));
  r.request = async (...args) => {
    if (args[0] === "GET" && args[1] === conflict.path) {
      enter();
      await gate;
    }
    return original(...args);
  };
  const resolving = sync.resolve(conflict, false);
  await entered;
  try {
    await promptLocalOperation(() =>
      c.store.save(other, "independent save during resolution"),
    );
  } finally {
    release();
  }
  await resolving;
  assert.equal(
    await c.io.read(`${other.path}/main.md`),
    "independent save during resolution",
  );
});

test("a notebook recycle marker belongs to its notebook and moves complete notes", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  await c.store.deleteBook(c.book);
  const report = await sync.run();
  assert.equal(report.failed, 0);
  assert(!report.items.some((x) => x.id === "deleted.json"));
  assert(
    report.items
      .find((x) => x.id === c.book.id)
      .paths.includes(`.deleted-books/${c.book.id}/deleted.json`),
  );
  const note = report.items.find((x) => x.id === c.note.id);
  assert(note.complete);
  assert(r.files.has(`.deleted-books/${c.book.id}/${c.note.id}/main.md`));
  assert(!r.files.has(`${c.note.path}/main.md`));
});

test("a late note metadata deletion failure retains the old notebook metadata", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  await sync.run();
  await c.store.deleteBook(c.book);
  const original = r.request.bind(r);
  r.request = async (...args) =>
    args[0] === "DELETE" && args[1] === `${c.note.path}/note.json`
      ? { status: 403 }
      : original(...args);
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(!report.items.find((x) => x.id === c.book.id).complete);
  assert(r.files.has(`${c.book.path}/book.json`));
  assert(r.files.has(`${c.note.path}/note.json`));
  assert(r.files.has(`.deleted-books/${c.book.id}/${c.note.id}/main.md`));
  r.request = original;
  assert.equal((await sync.run()).failed, 0);
  assert(!r.files.has(`${c.book.path}/book.json`));
});

test("moving into a denied new notebook keeps the old complete note online", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r, undefined, undefined, fast);
  await sync.run();
  const created = await c.store.createBook("new destination"),
    book = { ...created, path: `books/${created.id}` };
  const dest = await c.store.moveNote(c.note, book),
    original = r.request.bind(r);
  r.request = async (...args) =>
    args[0] === "PUT" && args[1] === `${book.path}/book.json`
      ? { status: 403 }
      : original(...args);
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(!r.files.has(`${dest}/main.md`));
  assert(r.files.has(`${c.note.path}/main.md`));
  assert(r.files.has(`${c.note.path}/note.json`));
});

test("a failed uncached attachment directory makes its note incomplete", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  const list = r.list.bind(r);
  r.list = async () => {
    const result = await list();
    result.errors = [
      {
        path: `${c.note.path}/reference/`,
        message: "attachment directory denied",
        status: 403,
        code: "DAV_ERROR",
      },
    ];
    return result;
  };
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert.equal(report.failed, 1);
});

test("a note created during a network wait is pending instead of falsely complete", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  let newNote;
  const original = r.request.bind(r);
  r.request = async (...args) => {
    if (!newNote && args[0] === "GET" && args[1] === `${c.note.path}/main.md`)
      newNote = await c.store.createNote(c.book, "created during sync");
    return original(...args);
  };
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === newNote.id).complete);
  assert(issues(report).some((x) => x.code === "LOCAL_CHANGED"));
  r.request = original;
  assert.equal((await sync.run()).failed, 0);
  assert(r.files.has(`${newNote.path}/note.json`));
});

test("a note moved during a network wait is uploaded before its old copy is deleted", async () => {
  const c = await setup(),
    r = new Remote(),
    sync = new WebDAVSync(c.store, r);
  await sync.run();
  const book = await c.store.ensureQuickBook();
  let dest;
  const original = r.request.bind(r);
  r.request = async (...args) => {
    if (!dest && args[0] === "GET" && args[1] === `${c.note.path}/main.md`)
      dest = await c.store.moveNote(c.note, book);
    return original(...args);
  };
  const report = await sync.run();
  assert(!report.items.find((x) => x.id === c.note.id).complete);
  assert(r.files.has(`${c.note.path}/main.md`));
  assert(r.files.has(`${c.note.path}/note.json`));
  r.request = original;
  assert.equal((await sync.run()).failed, 0);
  assert(r.files.has(`${dest}/main.md`));
  assert(!r.files.has(`${c.note.path}/main.md`));
});
