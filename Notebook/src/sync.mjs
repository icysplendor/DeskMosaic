import { encode, decode, digest, id } from "./core.mjs";
import { createDAVRequest } from "./dav-client.mjs";
import { syncItem, syncIssue } from "./sync-items.mjs";
const accepted = (path) =>
  /^(books|\.deleted|\.deleted-books)\//.test(path) &&
  !path.split("/").some((p) => !p || p === ".." || p === ".") &&
  !path.endsWith(".bak");
const busy = () =>
  Object.assign(new Error("另一台设备正在同步，将自动等待重试"), {
    code: "SYNC_BUSY",
  });
export class WebDAVSync {
  constructor(
    store,
    remote,
    progress = () => {},
    include = () => true,
    options = {},
  ) {
    this.store = store;
    this.remote = remote;
    this.progress = progress;
    this.include = (path) => accepted(path) && include(path);
    this.folders = new Set();
    this.trace = [];
    this.send = createDAVRequest((...args) => this.remote.request(...args), {
      ...options,
      beforeAttempt: async ({ method, path, attempt }) => {
        if (this.lease && path !== this.lease.path) {
          if (Date.now() >= this.lease.expires)
            throw Object.assign(
              new Error("同步锁已过期，本轮已停止，将重新同步"),
              { code: "SYNC_LEASE_LOST" },
            );
          if (attempt && ["PUT", "DELETE", "MKCOL"].includes(method))
            await this.renew(this.lease, true);
        }
      },
      onRetry: (event) => this.progress(event),
      onResponse: (event) => {
        this.trace.push(event);
        if (this.trace.length > 40) this.trace.shift();
      },
    });
  }
  async request(method, path, data = null, headers = {}) {
    return this.send(method, path, data, headers);
  }
  exclusive(work) {
    // Network waits serialize sync passes, not local editing or file actions.
    const next = (this.store.syncTail || Promise.resolve()).then(work);
    this.store.syncTail = next.catch(() => {});
    return next;
  }
  async parents(path) {
    const pieces = path.split("/");
    pieces.pop();
    let folder = "";
    for (const piece of pieces) {
      folder += piece + "/";
      if (this.folders.has(folder)) continue;
      const response = await this.request("MKCOL", folder);
      if (![201, 405].includes(response.status))
        throw new Error(`在线目录创建失败 (${response.status})`);
      this.folders.add(folder);
    }
  }
  async lock() {
    const path = ".notebook-lock.json",
      owner = id();
    let result = await this.request(
      "PUT",
      path,
      encode(JSON.stringify({ owner, expires: Date.now() + 300000 })),
      { "If-None-Match": "*" },
    );
    if (result.status === 412) {
      const current = await this.request("GET", path);
      if (current.status !== 200 || !current.etag) throw busy();
      if (/^W\//i.test(current.etag))
        throw new Error("服务器只提供弱 ETag，无法安全取得同步锁");
      let record;
      try {
        record = JSON.parse(decode(current.data));
      } catch {
        throw new Error("在线同步锁损坏，请检查服务器");
      }
      if (typeof record.owner !== "string" || !Number.isFinite(record.expires))
        throw new Error("在线同步锁信息无效，已停止同步且未覆盖锁文件");
      // A successful PUT can lose its response. A retry returns 412, but its
      // UUID still identifies our lease; it must not block us for five minutes.
      if (record.owner === owner && record.expires > Date.now())
        return { path, owner, etag: current.etag, expires: record.expires };
      if (record.expires > Date.now()) throw busy();
      result = await this.request(
        "PUT",
        path,
        encode(JSON.stringify({ owner, expires: Date.now() + 300000 })),
        { "If-Match": current.etag },
      );
    }
    if (result.status === 412) throw busy();
    if (![200, 201, 204].includes(result.status))
      throw new Error(`无法取得同步锁 (${result.status})`);
    const current = await this.request("GET", path);
    if (
      current.status !== 200 ||
      !current.etag ||
      /^W\//i.test(current.etag) ||
      JSON.parse(decode(current.data)).owner !== owner ||
      !(JSON.parse(decode(current.data)).expires > Date.now())
    )
      throw new Error("同步锁校验失败");
    return {
      path,
      owner,
      etag: current.etag,
      expires: JSON.parse(decode(current.data)).expires,
    };
  }
  async release(lock) {
    try {
      const result = await this.request("DELETE", lock.path, null, {
        "If-Match": lock.etag,
      });
      if (![200, 204, 404, 412].includes(result.status))
        throw new Error("同步锁释放失败");
    } catch (error) {
      this.warning =
        "文件处理已完成，但同步锁暂未释放；其他设备需等待锁到期后重试";
      this.trace.push({ phase: "release", status: error.status || 0 });
    } finally {
      this.lease = null;
    }
  }
  async reconcileRace(baseline, path, previous) {
    const latest = await this.request("GET", path);
    if (![200, 404].includes(latest.status))
      throw new Error("无法重新读取在线版本");
    if (latest.status === 200 && (!latest.etag || /^W\//i.test(latest.etag)))
      throw new Error("服务器未提供有效 ETag，已停止同步");
    return this.store.serial(async () => {
      const remote = latest.status === 200 ? latest.data : null;
      const local = await this.store.io.readBinary(path);
      const rh = remote === null ? null : await digest(remote);
      const lh = local === null ? null : await digest(local);
      const draft = path.endsWith("/main.md")
        ? this.store.drafts.get(path.slice(0, -8))
        : undefined;
      if (
        lh === rh &&
        (draft === undefined || (local !== null && draft === decode(local)))
      ) {
        if (lh) baseline.files[path] = { hash: lh, etag: latest.etag };
        else delete baseline.files[path];
      } else if (
        latest.status === previous.status &&
        latest.etag === previous.etag
      ) {
        throw new Error(
          `服务器拒绝文件版本条件，已保留本地文件（${path}，412）`,
        );
      } else await this.conflict(baseline, path, local, remote, rh, draft);
    });
  }
  async applyRemote(
    baseline,
    path,
    local,
    remote,
    remoteHash,
    newFile = false,
  ) {
    const s = this.store;
    await s.serial(async () => {
      const current = await s.io.readBinary(path);
      const draft = path.endsWith("/main.md")
        ? s.drafts.get(path.slice(0, -8))
        : undefined;
      if (
        current !== local ||
        (draft !== undefined &&
          (newFile || current === null || draft !== decode(current)))
      ) {
        await this.conflict(baseline, path, current, remote, remoteHash, draft);
        return;
      }
      if (local !== null)
        await s.io.writeBinary(`.quarantine/${id()}/${path}`, local);
      if (remote === null) {
        await s.io.remove(path);
        delete baseline.files[path];
      } else {
        await s.io.writeBinary(path, remote);
        baseline.files[path] = { hash: remoteHash };
      }
    });
  }
  guardDeletions(baseline, localPaths, remotePaths, unavailable = []) {
    const available = (path) =>
      this.include(path) &&
      !unavailable.some((prefix) => path.startsWith(prefix));
    const local = new Set(localPaths.filter(available)),
      remote = new Set(remotePaths.filter(available));
    const known = Object.keys(baseline.files).filter(available);
    if (
      known.length &&
      ((!remote.size && local.size) || (!local.size && remote.size))
    )
      throw Object.assign(
        new Error(
          "同步删除保护：笔记库一侧突然为空，已停止同步，未删除文件。请检查本地目录和网盘目录",
        ),
        { code: "SYNC_DELETION_GUARD" },
      );
    // Stable suffixes recognize notebook/note moves into another book or trash.
    const key = (path) => path.split("/").slice(-2).join("/");
    for (const [present, other] of [
      [local, remote],
      [remote, local],
    ]) {
      const keys = new Set([...other].map(key));
      const live = known.filter(
        (path) => path.startsWith("books/") && present.has(path),
      );
      const missing = live.filter(
        (path) => !other.has(path) && !keys.has(key(path)),
      );
      if (missing.length >= 10 && missing.length / live.length >= 0.9)
        throw Object.assign(
          new Error(
            `同步删除保护：检测到 ${missing.length} 个文件异常消失，已停止同步且保留现有文件`,
          ),
          { code: "SYNC_DELETION_GUARD" },
        );
    }
  }
  async renew(lock, force = false) {
    try {
      if (Date.now() >= lock.expires)
        throw new Error("同步锁已过期，已停止本轮同步，请重试");
      // Renew periodically instead of making two network requests for each file.
      if (lock.expires - Date.now() > 60000) {
        if (!force) return;
        // Before a mutation, verify ownership without rewriting a fresh lease.
        const current = await this.request("GET", lock.path);
        if (
          current.status !== 200 ||
          current.etag !== lock.etag ||
          JSON.parse(decode(current.data)).owner !== lock.owner ||
          Date.now() >= lock.expires
        )
          throw new Error("同步锁已失效，已停止写入");
        return;
      }
      const result = await this.request(
        "PUT",
        lock.path,
        encode(
          JSON.stringify({ owner: lock.owner, expires: Date.now() + 300000 }),
        ),
        { "If-Match": lock.etag },
      );
      if (![200, 201, 204, 412].includes(result.status))
        throw new Error("同步锁已失效，已停止写入");
      const current = await this.request("GET", lock.path);
      if (
        current.status !== 200 ||
        JSON.parse(decode(current.data)).owner !== lock.owner ||
        !(JSON.parse(decode(current.data)).expires > Date.now())
      )
        throw new Error("同步锁已失效");
      if (!current.etag || /^W\//i.test(current.etag))
        throw new Error("服务器未提供 ETag，无法安全续期");
      lock.etag = current.etag;
      lock.expires = JSON.parse(decode(current.data)).expires;
    } catch (error) {
      error.code ||= "SYNC_LEASE_LOST";
      throw error;
    }
  }
  run() {
    return this.exclusive(async () => {
      const s = this.store;
      const baseline = await s.serial(() =>
        s.json(".sync.json", { files: {}, conflicts: [] }),
      );
      this.trace = [];
      this.warning = null;
      const lock = await this.lock();
      this.lease = lock;
      this.folders.clear();
      let processed = 0,
        skipped = 0;
      const items = new Map();
      const failed = (path, error) => {
        const item = items.get(syncItem(path).key);
        if (!item) return;
        const issue = syncIssue(error, path);
        if (
          !item.errors.some(
            (x) => x.path === issue.path && x.code === issue.code,
          )
        )
          item.errors.push(issue);
      };
      let completion;
      try {
        const listing = await this.remote.list(this.request.bind(this));
        const versions = new Map(
          listing.map((item) =>
            typeof item === "string"
              ? [item, ""]
              : [item.path, item.etag || ""],
          ),
        );
        const remotePaths = [...versions.keys()].filter(this.include);
        const localPaths = (await s.serial(() => s.files())).filter(
          this.include,
        );
        this.guardDeletions(
          baseline,
          localPaths,
          remotePaths,
          (listing.errors || []).map((x) => x.path),
        );
        const localSet = new Set(localPaths);
        const paths = new Set([
          ...remotePaths,
          ...localPaths,
          ...(s.io.trackedPaths?.() || []).filter(this.include),
          ...Object.keys(baseline.files).filter(this.include),
        ]);
        for (const path of paths) {
          const unit = syncItem(path);
          if (!items.has(unit.key))
            items.set(unit.key, { ...unit, paths: [], errors: [] });
          items.get(unit.key).paths.push(path);
        }
        for (const error of listing.errors || []) {
          const affected = [...paths].filter((path) =>
            path.startsWith(error.path),
          );
          for (const path of affected) failed(path, error);
          // A lazy, uncached attachment subtree still belongs to its note.
          const knownItem = items.has(syncItem(error.path).key);
          if (knownItem) failed(error.path, error);
          if (!affected.length && !knownItem) {
            const key = `scope:${error.path}`;
            items.set(key, {
              key,
              id: error.path,
              type: "scope",
              paths: [],
              errors: [syncIssue(error, error.path)],
            });
          }
        }
        await s.serial(async () => {
          for (const item of items.values()) {
            const metadata = item.paths.find(
              (path) =>
                path.endsWith(
                  item.type === "book" ? "/book.json" : "/note.json",
                ) && localSet.has(path),
            );
            if (!metadata) continue;
            try {
              item.title = (await s.json(metadata))?.title;
            } catch {}
          }
        });
        for (const conflict of baseline.conflicts)
          failed(
            conflict.path,
            Object.assign(new Error("双方版本有冲突，请比较并选择版本"), {
              code: "SYNC_CONFLICT",
            }),
          );
        // Import every lazily downloaded common version before inspecting a
        // deletion group. A concurrent attachment edit protects its metadata too.
        for (const path of paths)
          if (!baseline.files[path]) {
            const common = s.io.commonBase?.(path);
            if (common) baseline.files[path] = common;
          }
        // Upload new locations (including recycle bins) before propagating deletions.
        // Keep metadata discoverable if a late body edit rejects its deletion.
        const deleteRank = (path) =>
          path.endsWith("/main.md")
            ? 0
            : /\/(reference|\.trash)\//.test(path)
              ? 1
              : path.endsWith("/book.json")
                ? 4
                : path.endsWith("/note.json")
                  ? 3
                  : 2;
        const uploadRank = (path) =>
          path.endsWith("/book.json")
            ? -1
            : path.endsWith("/note.json")
              ? 3
              : path.endsWith("/main.md")
                ? 2
                : 0;
        const ordered = [...paths].sort(
          (a, b) =>
            Number(!localSet.has(a)) - Number(!localSet.has(b)) ||
            (!localSet.has(a)
              ? deleteRank(a) - deleteRank(b)
              : uploadRank(a) - uploadRank(b)),
        );
        const deletionChecked = new Set();
        for (const [index, path] of ordered.entries()) {
          this.progress({ current: index + 1, total: ordered.length });
          await this.renew(lock);
          const unit = syncItem(path);
          const item = items.get(unit.key);
          if (item.errors.length) continue;
          // A failed new notebook cannot publish children that are invisible
          // without its metadata. Existing notebooks can still sync their notes.
          if (unit.type === "note" && unit.bookId) {
            const parent = items.get(`book:${unit.bookId}`);
            const metadata =
              path.split("/").slice(0, 2).join("/") + "/book.json";
            if (
              parent?.errors.length &&
              !(versions.has(metadata) && localSet.has(metadata))
            ) {
              failed(
                path,
                Object.assign(
                  new Error("所属笔记本尚未同步完成，保留本机内容等待重试"),
                  { code: "SYNC_DEPENDENCY" },
                ),
              );
              continue;
            }
          }
          if (
            !localSet.has(path) &&
            item.type === "book" &&
            [...items.values()].some(
              (x) =>
                x.type === "note" &&
                x.errors.length &&
                x.paths.some((p) =>
                  p.startsWith(path.slice(0, path.lastIndexOf("/")) + "/"),
                ),
            )
          ) {
            failed(
              path,
              Object.assign(
                new Error("笔记本内仍有未完成项目，暂不删除笔记本元数据"),
                { code: "SYNC_DEPENDENCY" },
              ),
            );
            continue;
          }
          try {
            if (baseline.conflicts.some((conflict) => conflict.path === path))
              continue;
            const known = baseline.files[path];
            // ETags are an optimization only when both sides still match the
            // checkpoint. Changed files always follow the full conflict protocol.
            if (
              known?.etag &&
              !/^W\//i.test(known.etag) &&
              versions.get(path) === known.etag &&
              localSet.has(path)
            ) {
              const current = await s.io.readBinary(path);
              if (current !== null && (await digest(current)) === known.hash) {
                processed++;
                skipped++;
                continue;
              }
            }
            if (!localSet.has(path) && !deletionChecked.has(item.key)) {
              deletionChecked.add(item.key);
              // Detect edits anywhere in the deleted set before removing metadata
              // or attachments from a remotely edited notebook/note.
              for (const candidate of ordered.filter(
                (x) =>
                  !localSet.has(x) &&
                  baseline.files[x] &&
                  syncItem(x).key === item.key,
              )) {
                await this.renew(lock);
                const check = await this.request("GET", candidate);
                if (![200, 404].includes(check.status))
                  throw new Error("删除前在线版本检查失败");
                if (
                  check.status === 200 &&
                  (await digest(check.data)) !== baseline.files[candidate].hash
                )
                  // Record the actual conflict before deciding on any deletion.
                  await this.conflict(
                    baseline,
                    candidate,
                    null,
                    check.data,
                    await digest(check.data),
                  );
              }
              if (
                baseline.conflicts.some(
                  (c) => syncItem(c.path).key === item.key,
                )
              ) {
                failed(
                  path,
                  Object.assign(
                    new Error("移动或删除与在线修改冲突，已保留双方副本"),
                    { code: "SYNC_CONFLICT" },
                  ),
                );
                await s.serial(() => s.put(".sync.json", baseline));
                continue;
              }
            }
            await this.renew(lock);
            const result = await this.request("GET", path);
            if (result.status !== 200 && result.status !== 404)
              throw new Error(`读取在线文件失败 (${result.status})`);
            const remote = result.status === 200 ? result.data : null;
            if (remote !== null && (!result.etag || /^W\//i.test(result.etag)))
              throw new Error("服务器未提供 ETag，已停止同步以保护文件");
            // Re-read after the network wait: external editors may have changed disk.
            const local = await s.serial(() => s.io.readBinary(path));
            const lh = local === null ? null : await digest(local),
              rh = remote === null ? null : await digest(remote);
            const base = baseline.files[path];
            const draft = path.endsWith("/main.md")
              ? s.drafts.get(path.slice(0, -8))
              : undefined;
            if (
              lh !== rh &&
              draft !== undefined &&
              (local === null || draft !== decode(local))
            ) {
              await this.conflict(baseline, path, local, remote, rh, draft);
            } else if (lh === rh) {
              if (lh) baseline.files[path] = { hash: lh };
              else delete baseline.files[path];
            } else if (!base && local === null) {
              await this.renew(lock, true);
              await this.applyRemote(baseline, path, local, remote, rh, true);
            } else if (
              (!base && remote === null) ||
              (base && rh === base.hash)
            ) {
              if (local === null) {
                if (item.errors.length) continue;
                const newLocation = await s.serial(async () => {
                  for (const candidate of (await s.files()).filter(
                    this.include,
                  )) {
                    if (
                      syncItem(candidate).key !== item.key ||
                      paths.has(candidate)
                    )
                      continue;
                    const bytes = await s.io.readBinary(candidate);
                    if (
                      bytes !== null &&
                      (await digest(bytes)) !== baseline.files[candidate]?.hash
                    )
                      return true;
                  }
                  return false;
                });
                if (newLocation) {
                  failed(
                    path,
                    Object.assign(
                      new Error(
                        "同步期间笔记已移动，新位置尚未上传，暂不删除在线原件",
                      ),
                      { code: "LOCAL_CHANGED" },
                    ),
                  );
                  continue;
                }
                await this.renew(lock, true);
                const deleted = await this.request("DELETE", path, null, {
                  "If-Match": result.etag,
                });
                if (deleted.status === 412) {
                  await this.reconcileRace(baseline, path, result);
                  if (
                    baseline.conflicts.some(
                      (c) => syncItem(c.path).key === item.key,
                    )
                  )
                    failed(
                      path,
                      Object.assign(
                        new Error("删除时在线版本变化，已保留双方副本"),
                        { code: "SYNC_CONFLICT" },
                      ),
                    );
                } else if (![200, 204, 404].includes(deleted.status))
                  throw new Error(`在线删除失败 (${deleted.status})`);
                else delete baseline.files[path];
              } else {
                await this.parents(path);
                await this.renew(lock, true);
                const written = await this.request(
                  "PUT",
                  path,
                  local,
                  remote === null
                    ? { "If-None-Match": "*" }
                    : { "If-Match": result.etag },
                );
                if (written.status === 412)
                  await this.reconcileRace(baseline, path, result);
                else if (![200, 201, 204].includes(written.status))
                  throw new Error(`在线文件已变化 (${written.status})`);
                else
                  baseline.files[path] = {
                    hash: lh,
                    ...(written.etag && !/^W\//i.test(written.etag)
                      ? { etag: written.etag }
                      : {}),
                  };
              }
            } else if (base && lh === base.hash) {
              await this.renew(lock, true);
              await this.applyRemote(baseline, path, local, remote, rh);
            } else {
              await this.conflict(baseline, path, local, remote, rh);
            }
            if (
              result.status === 200 &&
              baseline.files[path]?.hash === rh &&
              !baseline.conflicts.some((conflict) => conflict.path === path)
            )
              baseline.files[path].etag = result.etag;
            // Each successful file has its own checkpoint; retries after interruption
            // don't mistake already-applied changes for new deletions.
            await s.serial(() => s.put(".sync.json", baseline));
            if (
              baseline.conflicts.some((c) => syncItem(c.path).key === item.key)
            )
              failed(
                path,
                Object.assign(new Error("双方版本有冲突，请比较并选择版本"), {
                  code: "SYNC_CONFLICT",
                }),
              );
            processed++;
          } catch (error) {
            if (
              error.path === lock.path ||
              error.code === "SYNC_LEASE_LOST" ||
              error.status === 401
            )
              throw error;
            // A collaborative editor may hold one file indefinitely. Keep its
            // checkpoint intact and process other notes, but stop dependent
            // deletions so metadata cannot disappear around the locked file.
            failed(path, error);
            if (error.code === "SYNC_RETRY" && !error.status)
              await this.renew(lock, true); // Stop if the whole server is unreachable.
          }
        }
        // An upload may have used a valid snapshot while the user continued
        // editing. That checkpoint is safe, but the whole note is still pending.
        await s.serial(async () => {
          const currentPaths = await s.files();
          for (const path of new Set([
            ...paths,
            ...currentPaths.filter(this.include),
          ])) {
            const unit = syncItem(path);
            if (!items.has(unit.key))
              items.set(unit.key, { ...unit, paths: [path], errors: [] });
            const item = items.get(unit.key);
            if (item.errors.length) continue;
            const data = await s.io.readBinary(path);
            const hash = data === null ? null : await digest(data);
            const draft = path.endsWith("/main.md")
              ? s.drafts.get(path.slice(0, -8))
              : undefined;
            if (
              hash !== (baseline.files[path]?.hash || null) ||
              (draft !== undefined && (data === null || draft !== decode(data)))
            )
              failed(
                path,
                Object.assign(
                  new Error(
                    "同步期间笔记继续修改，新内容已保留，等待下一轮同步",
                  ),
                  { code: "LOCAL_CHANGED" },
                ),
              );
          }
        });
        s.state.conflicts = baseline.conflicts.length;
        const results = [...items.values()];
        completion = {
          processed,
          skipped,
          conflicts: baseline.conflicts,
          items: results.map((item) => ({
            ...item,
            complete: !item.errors.length,
          })),
          completed: results.filter((x) => !x.errors.length).length,
          failed: results.filter((x) => x.errors.length).length,
          at: Date.now(),
        };
        if (!s.state.conflicts && !completion.failed)
          s.state.lastSync = completion.at;
        await s.serial(async () => {
          await s.put(".state.json", s.state);
          await s.put(".sync-report.json", completion);
        });
        return completion;
      } finally {
        await this.release(lock);
        if (completion) {
          completion.warning = this.warning;
          await s.serial(() => s.put(".sync-report.json", completion));
        }
        await s
          .serial(() =>
            s.put(".sync-diagnostics.json", {
              at: Date.now(),
              warning: this.warning,
              pending: [...items.values()]
                .flatMap((x) => x.errors)
                .map(({ path, status }) => ({ path, status })),
              requests: this.trace,
            }),
          )
          .catch(() => {});
      }
    });
  }
  async conflict(baseline, path, local, remote, remoteHash, draft) {
    const conflict = { id: id(), path, remoteHash, created: Date.now() };
    if (remote !== null)
      await this.store.io.writeBinary(
        `.conflicts/${conflict.id}/remote`,
        remote,
      );
    if (local !== null)
      await this.store.io.writeBinary(`.conflicts/${conflict.id}/local`, local);
    if (draft !== undefined) {
      await this.store.io.write(`.conflicts/${conflict.id}/draft.md`, draft);
      conflict.hasDraft = true;
    }
    baseline.conflicts.push(conflict);
  }
  async updateConflictRemote(state, record, remote) {
    const hash = remote.status === 404 ? null : await digest(remote.data);
    if (hash === record.remoteHash) return false;
    const original = await this.store.io.readBinary(
      `.conflicts/${record.id}/remote`,
    );
    if (original !== null)
      await this.store.io.writeBinary(
        `.conflicts/${record.id}/remote-${id()}`,
        original,
      );
    if (remote.status === 200)
      await this.store.io.writeBinary(
        `.conflicts/${record.id}/remote`,
        remote.data,
      );
    else await this.store.io.remove(`.conflicts/${record.id}/remote`);
    record.remoteHash = hash;
    await this.store.put(".sync.json", state);
    return true;
  }
  review(conflict) {
    return this.exclusive(async () => {
      const remote = await this.request("GET", conflict.path);
      if (![200, 404].includes(remote.status))
        throw new Error(`无法读取在线冲突版本（${remote.status}）`);
      return this.store.serial(async () => {
        const state = await this.store.json(".sync.json");
        const record = state.conflicts.find((x) => x.id === conflict.id);
        if (!record) return null;
        await this.updateConflictRemote(state, record, remote);
        return { ...record };
      });
    });
  }
  resolve(conflict, keepLocal, mergedMarkdown) {
    return this.exclusive(async () => {
      const s = this.store,
        state = await s.serial(() => s.json(".sync.json"));
      const initial = await s.serial(() => s.io.readBinary(conflict.path));
      const initialDraft = s.drafts.get(conflict.path.slice(0, -8));
      const checkLocal = async () => {
        if (
          (await s.io.readBinary(conflict.path)) !== initial ||
          s.drafts.get(conflict.path.slice(0, -8)) !== initialDraft
        )
          throw new Error(
            "处理期间本地内容继续修改，已保留新内容，请重新比较后处理",
          );
      };
      const record = state.conflicts.find((x) => x.id === conflict.id);
      if (!record || record.remoteHash !== conflict.remoteHash)
        throw new Error("冲突对照已更新，请重新打开后处理");
      const lock = await this.lock();
      this.lease = lock;
      this.folders.clear();
      try {
        const remote = await this.request("GET", conflict.path);
        if (![200, 404].includes(remote.status))
          throw new Error("无法读取在线冲突版本");
        const hash = remote.status === 404 ? null : await digest(remote.data);
        if (hash !== conflict.remoteHash) {
          await s.serial(() =>
            this.updateConflictRemote(state, record, remote),
          );
          throw new Error(
            "在线版本再次变化，新旧版本均已保留。请重新打开冲突对照后处理",
          );
        }
        if (
          remote.status === 200 &&
          (!remote.etag || /^W\//i.test(remote.etag))
        )
          throw new Error("服务器未提供 ETag，无法安全解决冲突");
        await this.renew(lock, true);
        if (keepLocal) {
          if (mergedMarkdown !== undefined) {
            await s.serial(async () => {
              await checkLocal();
              if (!conflict.path.endsWith("/main.md"))
                throw new Error("仅正文支持手动合并");
              const before = await s.io.readBinary(conflict.path);
              if (before !== null)
                await s.io.writeBinary(
                  `.quarantine/${id()}/${conflict.path}`,
                  before,
                );
              await s.io.write(conflict.path, mergedMarkdown);
            });
          }
          const local = await s.io.readBinary(conflict.path);
          if (local !== null) await this.parents(conflict.path);
          await this.renew(lock, true);
          const result =
            local === null && remote.status === 404
              ? { status: 404 }
              : local === null
                ? await this.request("DELETE", conflict.path, null, {
                    "If-Match": remote.etag,
                  })
                : await this.request(
                    "PUT",
                    conflict.path,
                    local,
                    remote.status === 404
                      ? { "If-None-Match": "*" }
                      : { "If-Match": remote.etag },
                  );
          if (![200, 201, 204, 404].includes(result.status))
            throw new Error(
              result.status === 412
                ? "在线版本再次变化，请重新读取版本并比较后处理（412）"
                : `处理冲突失败（${result.status}）`,
            );
          if (local !== null)
            state.files[conflict.path] = { hash: await digest(local) };
          else delete state.files[conflict.path];
        } else {
          await s.serial(async () => {
            await checkLocal();
            const local = await s.io.readBinary(conflict.path);
            if (local !== null)
              await s.io.writeBinary(
                `.quarantine/${id()}/${conflict.path}`,
                local,
              );
            if (remote.status === 404) {
              await s.io.remove(conflict.path);
              delete state.files[conflict.path];
            } else {
              await s.io.writeBinary(conflict.path, remote.data);
              state.files[conflict.path] = { hash };
            }
          });
        }
        state.conflicts = state.conflicts.filter((x) => x.id !== conflict.id);
        await s.serial(async () => {
          await s.put(".sync.json", state);
          s.state.conflicts = state.conflicts.length;
          await s.put(".state.json", s.state);
        });
      } finally {
        await this.release(lock);
      }
    });
  }
}
