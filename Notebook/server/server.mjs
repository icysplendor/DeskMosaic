import http from "node:http";
import { readFile, stat, mkdir, writeFile, rename } from "node:fs/promises";
import { resolve, extname, sep, dirname } from "node:path";
import {
  createCipheriv,
  createDecipheriv,
  createHash,
  randomBytes,
} from "node:crypto";
import { fileURLToPath } from "node:url";
import { Accounts } from "./accounts.mjs";
const limit = 64 * 1024 * 1024;
const fail = (status, message) => Object.assign(new Error(message), { status });
export function validateLibrary(raw, user, allowed) {
  let url;
  try {
    url = new URL(raw);
  } catch {
    throw fail(400, "请输入桌面端使用的完整 WebDAV 笔记文件夹地址");
  }
  const pieces = url.pathname
    .split("/")
    .filter(Boolean)
    .map(decodeURIComponent);
  if (
    url.origin !== allowed ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    pieces[0] !== "remote.php" ||
    pieces[1] !== "dav" ||
    pieces[2] !== "files" ||
    pieces[3] !== user ||
    pieces.length < 5 ||
    pieces.some(
      (x) =>
        x === "." ||
        x === ".." ||
        x.includes("/") ||
        x.includes("\\") ||
        /[\x00-\x1f]/.test(x),
    )
  )
    throw fail(400, "请选择该 Nextcloud 用户下的专用笔记文件夹");
  url.pathname = "/" + pieces.map(encodeURIComponent).join("/") + "/";
  return url.href;
}
export function validateRequest(input) {
  const { method, path = "", data = null, headers = {} } = input;
  if (
    !["GET", "PUT", "DELETE", "MKCOL", "PROPFIND"].includes(method) ||
    typeof path !== "string"
  )
    throw fail(400, "不支持的文件操作");
  const parts = path.replace(/\/$/, "").split("/");
  if (
    path &&
    (path.startsWith("/") ||
      parts.some(
        (x) =>
          !x ||
          x === ".." ||
          x === "." ||
          x.includes("\\") ||
          /[\x00-\x1f]/.test(x),
      ) ||
      !(
        /^(books|\.deleted|\.deleted-books)(\/|$)/.test(path) ||
        path === ".notebook-lock.json"
      ))
  )
    throw fail(400, "文件路径超出笔记目录");
  if (!path && !["PROPFIND"].includes(method))
    throw fail(400, "不允许修改笔记库根目录");
  if (
    headers === null ||
    typeof headers !== "object" ||
    Object.keys(headers).some(
      (x) => !["Depth", "If-Match", "If-None-Match"].includes(x),
    ) ||
    Object.values(headers).some(
      (x) => typeof x !== "string" || /[\r\n]/.test(x),
    )
  )
    throw fail(400, "请求条件无效");
  if (method === "PROPFIND" && !["0", "1"].includes(headers.Depth))
    throw fail(400, "目录扫描深度无效");
  if (
    (method === "PUT" &&
      !headers["If-Match"] &&
      headers["If-None-Match"] !== "*") ||
    (method === "DELETE" && !headers["If-Match"])
  )
    throw fail(400, "写入必须带版本条件");
  if (
    method === "DELETE" &&
    ((!path.includes("/") && path !== ".notebook-lock.json") ||
      path.endsWith("/"))
  )
    throw fail(400, "不允许递归删除在线目录");
  if (
    data !== null &&
    (method !== "PUT" ||
      typeof data !== "string" ||
      !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(
        data,
      ))
  )
    throw fail(400, "上传数据无效");
  return { method, path, data, headers };
}
async function body(req, max = limit * 1.4) {
  let size = 0;
  const chunks = [];
  for await (const chunk of req) {
    size += chunk.length;
    if (size > max) throw fail(413, "文件超过 64 MB");
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString());
  } catch {
    throw fail(400, "请求内容无效");
  }
}
const mime = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".webmanifest": "application/manifest+json",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".ttf": "font/ttf",
};
export function createNotesServer({
  assets = resolve(fileURLToPath(new URL("../NotebookWeb", import.meta.url))),
  secret = process.env.NOTES_SECRET,
  origin = process.env.NOTES_ORIGIN || "https://notes.example.com",
  davOrigin = process.env.NOTES_DAV_ORIGIN || "https://cloud.example.com",
  configFile = process.env.NOTES_CONFIG_FILE ||
    resolve("notes-data/settings.enc"),
  accountsFile = process.env.NOTES_ACCOUNTS_FILE || configFile + ".sqlite",
  secure = true,
  fetcher = fetch,
} = {}) {
  if (!secret || secret.length < 32)
    throw Error("NOTES_SECRET 至少需要 32 个随机字符");
  const key = createHash("sha256").update(secret).digest(),
    attempts = new Map();
  const accounts = new Accounts(accountsFile);
  const cookie = "notes_session";
  const seal = (s) => {
    const iv = randomBytes(12),
      c = createCipheriv("aes-256-gcm", key, iv),
      data = Buffer.concat([c.update(JSON.stringify(s)), c.final()]);
    return Buffer.concat([iv, c.getAuthTag(), data]).toString("base64url");
  };
  const unseal = (raw) => {
    const data = Buffer.from(raw || "", "base64url"),
      c = createDecipheriv("aes-256-gcm", key, data.subarray(0, 12));
    c.setAuthTag(data.subarray(12, 28));
    return JSON.parse(Buffer.concat([c.update(data.subarray(28)), c.final()]));
  };
  let sessionWrite = Promise.resolve();
  const sessionFile = configFile + ".sessions";
  const readSessions = async () => {
    try {
      return unseal(await readFile(sessionFile, "utf8"));
    } catch (e) {
      if (e.code === "ENOENT") return {};
      throw e;
    }
  };
  const updateSessions = (change) => {
    const task = sessionWrite
      .catch(() => {})
      .then(async () => {
        const records = await readSessions();
        for (const [id, r] of Object.entries(records))
          if (r.expires < Date.now()) delete records[id];
        change(records);
        await mkdir(dirname(sessionFile), { recursive: true, mode: 0o700 });
        await writeFile(sessionFile + ".tmp", seal(records), { mode: 0o600 });
        await rename(sessionFile + ".tmp", sessionFile);
      });
    sessionWrite = task;
    return task;
  };
  const session = async (req) => {
    try {
      const raw = (req.headers.cookie || "")
          .split(";")
          .map((x) => x.trim())
          .find((x) => x.startsWith(cookie + "="))
          ?.slice(cookie.length + 1),
        s = unseal(raw),
        records = await readSessions();
      if (
        s.expires < Date.now() ||
        s.user !== accounts.current()?.username ||
        s.authVersion !== accounts.current()?.version ||
        records[s.id]?.user !== s.user
      )
        throw Error();
      return s;
    } catch {
      throw fail(401, "请登录笔记服务；手机上的离线笔记仍保留");
    }
  };
  const config = async () => {
    try {
      return unseal(await readFile(configFile, "utf8"));
    } catch (e) {
      if (e.code === "ENOENT") return null;
      throw Error("服务器同步配置无法读取");
    }
  };
  const publicSession = async (s) => {
    const c = await config();
    return {
      serviceUser: s.user,
      expires: s.expires,
      account: c
        ? createHash("sha256")
            .update(c.url + "\0" + c.user)
            .digest("hex")
            .slice(0, 32)
        : null,
      url: c?.url || "",
      user: c?.user || "",
      syncEnabled: !!c,
    };
  };
  const dav = async (s, input) => {
    const { method, path, data, headers } = validateRequest(input);
    const target = s.url + path.split("/").map(encodeURIComponent).join("/");
    const response = await fetcher(target, {
      method,
      headers: {
        ...headers,
        // Compression changes Apache's ETag; conditional writes require the file ETag.
        "Accept-Encoding": "identity",
        Authorization:
          "Basic " + Buffer.from(s.user + ":" + s.password).toString("base64"),
        ...(method === "PUT"
          ? { "Content-Type": "application/octet-stream" }
          : {}),
      },
      body: data === null ? undefined : Buffer.from(data, "base64"),
      redirect: "error",
      signal: AbortSignal.timeout(60000),
    });
    const chunks = [];
    let bytes = 0;
    for await (const chunk of response.body || []) {
      bytes += chunk.length;
      if (bytes > limit) throw fail(413, "文件超过 64 MB");
      chunks.push(Buffer.from(chunk));
    }
    return {
      status: response.status,
      data: Buffer.concat(chunks).toString("base64"),
      etag: response.headers.get("etag") || "",
      retryAfter: response.headers.get("retry-after") || "",
    };
  };
  const server = http.createServer(async (req, res) => {
    const json = (value, status = 200) => {
      res.writeHead(status, {
        "Content-Type": "application/json; charset=utf-8",
        "Cache-Control": "no-store",
      });
      res.end(JSON.stringify(value));
    };
    try {
      res.setHeader("X-Content-Type-Options", "nosniff");
      res.setHeader("Referrer-Policy", "same-origin");
      res.setHeader("Content-Security-Policy", "frame-ancestors 'none'");
      const url = new URL(req.url, "http://local");
      if (url.pathname === "/health") {
        res.end("ok");
        return;
      }
      if (url.pathname.startsWith("/api/")) {
        // Keep account recovery outside the offline editor's cached navigation shell.
        if (url.pathname === "/api/account-setup" && req.method === "GET") {
          res.writeHead(200, {
            "Content-Type": "text/html; charset=utf-8",
            "Cache-Control": "no-store",
          });
          res.end(
            await readFile(new URL("account-setup.html", import.meta.url)),
          );
          return;
        }
        if (req.method === "POST" && req.headers.origin !== origin)
          throw fail(403, "请求来源无效");
        if (url.pathname === "/api/session" && req.method === "GET") {
          json(await publicSession(await session(req)));
          return;
        }
        if (url.pathname === "/api/auth-status" && req.method === "GET") {
          json({ setupRequired: !accounts.current() });
          return;
        }
        if (
          ["/api/login", "/api/setup"].includes(url.pathname) &&
          req.method === "POST"
        ) {
          const ip = req.socket.remoteAddress,
            now = Date.now();
          if (attempts.size > 10000)
            for (const [k, v] of attempts)
              if (now - v.since > 900000) attempts.delete(k);
          const previous = attempts.get(ip),
            record =
              previous && now - previous.since < 900000
                ? previous
                : { count: 0, since: now };
          attempts.set(ip, record);
          if (record.count >= 10)
            throw fail(429, "登录尝试过多，请 15 分钟后重试");
          record.count++;
          const input = await body(req, 16384),
            user = String(input.user || "").trim(),
            password = String(input.password || "");
          if (!user || !password || password.length > 512)
            throw fail(401, "用户名或密码错误");
          const identity =
            url.pathname === "/api/setup"
              ? await accounts.setup(String(input.token || ""), user, password)
              : await accounts.verify(user, password);
          if (!identity) throw fail(401, "用户名或密码错误");
          const s = {
            id: randomBytes(24).toString("base64url"),
            user,
            authVersion: identity.version,
            expires: now + 90 * 86400000,
          };
          await updateSessions((records) => {
            records[s.id] = { user, expires: s.expires };
          });
          attempts.delete(ip);
          res.setHeader(
            "Set-Cookie",
            `${cookie}=${seal(s)}; HttpOnly; SameSite=Strict; Path=/; Max-Age=7776000${secure ? "; Secure" : ""}`,
          );
          json(await publicSession(s));
          return;
        }
        if (url.pathname === "/api/logout" && req.method === "POST") {
          const s = await session(req);
          await updateSessions((records) => {
            delete records[s.id];
          });
          res.setHeader(
            "Set-Cookie",
            `${cookie}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0${secure ? "; Secure" : ""}`,
          );
          json({ ok: true });
          return;
        }
        if (url.pathname === "/api/configure" && req.method === "POST") {
          await session(req);
          const input = await body(req, 16384),
            previous = await config();
          const user = String(input.user || "").trim(),
            url = validateLibrary(String(input.url || ""), user, davOrigin);
          const password = String(
            input.password ||
              (previous?.url === url && previous.user === user
                ? previous.password
                : ""),
          );
          if (!password || password.length > 512)
            throw fail(400, "请输入 Nextcloud 应用密码");
          const next = { url, user, password };
          const result = await dav(next, {
            method: "PROPFIND",
            path: "",
            headers: { Depth: "0" },
          });
          if (result.status !== 207)
            throw fail(
              400,
              "WebDAV 校验失败，请使用桌面端相同的专用文件夹地址和应用密码",
            );
          await mkdir(dirname(configFile), { recursive: true, mode: 0o700 });
          if (previous)
            await writeFile(configFile + ".bak", seal(previous), {
              mode: 0o600,
            });
          await writeFile(configFile + ".tmp", seal(next), { mode: 0o600 });
          await rename(configFile + ".tmp", configFile);
          json(await publicSession(await session(req)));
          return;
        }
        if (url.pathname === "/api/dav" && req.method === "POST") {
          const s = await session(req),
            input = await body(req),
            state = await publicSession(s),
            c = await config();
          if (!c) throw fail(409, "请先配置 Nextcloud 同步");
          if (input.account !== state.account)
            throw fail(
              409,
              "服务器笔记库已切换，请重新打开应用；旧库离线内容仍保留",
            );
          json(await dav(c, input));
          return;
        }
        throw fail(404, "接口不存在");
      }
      if (!["GET", "HEAD"].includes(req.method))
        throw fail(405, "不支持的请求");
      const path = resolve(
        assets,
        "." +
          decodeURIComponent(
            url.pathname === "/" ? "/index.html" : url.pathname,
          ),
      );
      if (!path.startsWith(assets + sep)) throw fail(404, "页面不存在");
      const info = await stat(path);
      if (!info.isFile()) throw fail(404, "页面不存在");
      const data = await readFile(path);
      res.setHeader(
        "Content-Type",
        mime[extname(path)] || "application/octet-stream",
      );
      res.setHeader("Cache-Control", "no-cache");
      if (path.endsWith("/sw.js")) res.setHeader("Service-Worker-Allowed", "/");
      res.end(req.method === "HEAD" ? undefined : data);
    } catch (error) {
      json(
        { error: error.status ? error.message : "服务暂时不可用，请稍后重试" },
        error.status || (error.code === "ENOENT" ? 404 : 502),
      );
    }
  });
  server.on("close", () => accounts.close());
  return server;
}
if (process.argv[1] === fileURLToPath(import.meta.url))
  createNotesServer({
    secure: process.env.NOTES_COOKIE_SECURE !== "false",
  }).listen(
    Number(process.env.PORT) || 18083,
    process.env.HOST || "127.0.0.1",
    () => console.log("Notes service ready"),
  );
