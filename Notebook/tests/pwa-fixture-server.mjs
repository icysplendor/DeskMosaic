// Isolated fake DAV + service identity. Never contacts real Nextcloud or user data.
import { createNotesServer } from "../server/server.mjs";
import { Accounts } from "../server/accounts.mjs";
import http from "node:http";
import { readFile, rm } from "node:fs/promises";
const base = "http://127.0.0.1:4175",
  dir = "/tmp/notes-pwa-fixture",
  prefix = "/remote.php/dav/files/test/Notes/";
await rm(dir, { recursive: true, force: true });
const files = new Map(),
  reads = [];
let version = 0;
let fault = null;
function put(path, data) {
  files.set(path, {
    data: Buffer.isBuffer(data) ? data : Buffer.from(data),
    etag: '"' + ++version + '"',
  });
}
put(
  "books/mobile/book.json",
  JSON.stringify({ id: "mobile", title: "随身笔记", created: 0 }),
);
for (let n = 1; n <= 3; n++) {
  put(
    `books/mobile/n${n}/note.json`,
    JSON.stringify({
      id: "n" + n,
      title: "离线测试 " + n,
      created: 0,
      updated: n,
    }),
  );
  put(
    `books/mobile/n${n}/main.md`,
    `正文 ${n}\n\n这是可以离线编辑的笔记。\n\n\`\`\`bash\necho offline-${n}\n\`\`\`\n\n[文件.pdf](reference/file.pdf)\n`,
  );
  put(
    `books/mobile/n${n}/.attachments.json`,
    JSON.stringify({
      entries: {
        "file.pdf": {
          name: "文件.pdf",
          sha256: null,
          unusedSince: null,
          trashSince: null,
        },
      },
    }),
  );
  put(`books/mobile/n${n}/reference/file.pdf`, "%PDF-1.4 attachment " + n);
}
const escape = (value) =>
  value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const fetcher = async (url, options) => {
  const path = decodeURIComponent(new URL(url).pathname.slice(prefix.length)),
    file = files.get(path),
    h = options.headers;
  if (
    fault &&
    options.method === fault.method &&
    path === fault.path &&
    fault.remaining-- > 0
  ) {
    fault.conditions.push({ match: h["If-Match"], create: h["If-None-Match"] });
    return new Response("locked by test fixture", {
      status: fault.status,
      headers: { "Retry-After": "0" },
    });
  }
  if (options.method === "PROPFIND") {
    const folder = path.replace(/\/$/, ""),
      start = folder ? folder + "/" : "",
      entries = new Map([[start, true]]);
    for (const name of files.keys())
      if (name.startsWith(start)) {
        const rest = name.slice(start.length),
          part = rest.split("/")[0];
        entries.set(start + part, rest.includes("/"));
      }
    const body =
      '<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">' +
      [...entries]
        .map(
          ([name, dir]) =>
            `<d:response><d:href>${prefix + name.split("/").map(encodeURIComponent).join("/")}${dir && !name.endsWith("/") ? "/" : ""}</d:href><d:propstat><d:prop><d:resourcetype>${dir ? "<d:collection/>" : ""}</d:resourcetype><d:getetag>${escape(files.get(name)?.etag || "")}</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>`,
        )
        .join("") +
      "</d:multistatus>";
    return new Response(body, { status: 207 });
  }
  if (options.method === "MKCOL") return new Response("", { status: 201 });
  if (
    (h["If-Match"] && h["If-Match"] !== file?.etag) ||
    (h["If-None-Match"] === "*" && file)
  )
    return new Response("", { status: 412 });
  if (options.method === "GET") {
    reads.push(path);
    return new Response(file?.data || "", {
      status: file ? 200 : 404,
      headers: file
        ? {
            etag:
              options.headers["Accept-Encoding"] === "identity"
                ? file.etag
                : file.etag.replace(/"$/, '-gzip"'),
          }
        : {},
    });
  }
  if (options.method === "PUT") {
    put(path, options.body);
    return new Response(null, {
      status: file ? 204 : 201,
      headers: { etag: files.get(path).etag },
    });
  }
  if (options.method === "DELETE") {
    files.delete(path);
    return new Response(null, { status: 204 });
  }
  throw Error("unexpected method");
};
const accounts = new Accounts(dir + "/settings.enc.sqlite");
await accounts.setup(accounts.invite(), "test", "service-test");
accounts.close();
const server = createNotesServer({
  origin: base,
  davOrigin: "https://cloud.fixture",
  configFile: dir + "/settings.enc",
  secret: "fixture-secret-at-least-32-characters",
  secure: false,
  fetcher,
});
const initial = new Map(files);
const original = server.listeners("request")[0];
server.removeAllListeners("request");
server.on("request", async (req, res) => {
  if (req.url === "/_test/fault") {
    let data = "";
    for await (const chunk of req) data += chunk;
    fault = { ...JSON.parse(data), conditions: [] };
    res.end("ok");
    return;
  }
  if (req.url === "/_test/hold-lock") {
    put(
      ".notebook-lock.json",
      JSON.stringify({ owner: "another-device", expires: Date.now() + 60000 }),
    );
    res.end("ok");
    return;
  }
  if (req.url === "/_test/release-lock") {
    files.delete(".notebook-lock.json");
    res.end("ok");
    return;
  }
  if (req.url === "/_test/reset") {
    for (const suffix of ["", ".bak", ".sessions"])
      await rm(dir + "/settings.enc" + suffix, { force: true });
    files.clear();
    for (const [p, r] of initial) files.set(p, r);
    reads.length = 0;
    fault = null;
    res.end("ok");
    return;
  }
  if (req.url === "/_test/invite") {
    const db = new Accounts(dir + "/settings.enc.sqlite");
    const token = db.invite();
    db.close();
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify({ token }));
    return;
  }
  if (req.url === "/_test") {
    res.setHeader("Content-Type", "application/json");
    res.end(
      JSON.stringify({
        reads,
        fault,
        files: Object.fromEntries(
          [...files].map(([p, r]) => [p, r.data.toString()]),
        ),
      }),
    );
    return;
  }
  if (req.url === "/_test/attachment-change") {
    put("books/mobile/n1/reference/file.pdf", "%PDF-1.4 desktop newer bytes");
    res.end("ok");
    return;
  }
  if (req.url === "/_test/change") {
    put("books/mobile/n3/main.md", "电脑同时修改了这一篇。\n");
    res.end("ok");
    return;
  }
  original(req, res);
});
server.listen(4175, "127.0.0.1", () =>
  console.log("Isolated PWA fixture ready on 4175"),
);
