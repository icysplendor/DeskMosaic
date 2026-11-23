// Development-only native bridge simulation; isolated from the real notebook.
import http from "node:http";
import {
  readFile,
  writeFile,
  mkdir,
  readdir,
  rename,
  rm,
  stat,
} from "node:fs/promises";
import { resolve, dirname, extname } from "node:path";
const assets = resolve("NotebookWeb"),
  root = process.env.NOTEBOOK_PREVIEW_ROOT || "/tmp/notebook-ui-test-library";
await mkdir(root, { recursive: true });
const settings = {
  platform: "windows",
  theme: "Light",
  syncEnabled: false,
  url: "",
  user: "",
};
const safe = (p) => {
  const result = resolve(root, p || "");
  if (result !== root && !result.startsWith(root + "/"))
    throw Error("outside root");
  return result;
};
const read = async (p, binary = false) => {
  try {
    const data = await readFile(safe(p));
    return binary ? data.toString("base64") : data.toString();
  } catch (e) {
    if (e.code === "ENOENT") return null;
    throw e;
  }
};
http
  .createServer(async (req, res) => {
    try {
      if (req.url === "/_bridge") {
        let body = "";
        for await (const chunk of req) body += chunk;
        const { op, args: a } = JSON.parse(body);
        let result = null;
        if (op === "settings") result = settings;
        else if (op === "theme") {
          settings.theme = a.value;
          result = settings;
        } else if (op === "read" || op === "readBinary")
          result = await read(a.path, op === "readBinary");
        else if (op === "write") {
          await mkdir(dirname(safe(a.path)), { recursive: true });
          await writeFile(safe(a.path), Buffer.from(a.data, "base64"));
        } else if (op === "mkdir")
          await mkdir(safe(a.path), { recursive: true });
        else if (op === "list") {
          try {
            result = await Promise.all(
              (await readdir(safe(a.path), { withFileTypes: true })).map(
                async (e) => ({
                  name: e.name,
                  dir: e.isDirectory(),
                  modified: (
                    await stat(safe((a.path ? a.path + "/" : "") + e.name))
                  ).mtimeMs,
                }),
              ),
            );
          } catch (e) {
            if (e.code === "ENOENT") result = [];
            else throw e;
          }
        } else if (op === "move") {
          await mkdir(dirname(safe(a.to)), { recursive: true });
          await rename(safe(a.path), safe(a.to));
        } else if (op === "remove") await rm(safe(a.path), { force: true });
        else if (op === "purgeDeleted") {
          if (!/^\.(deleted|deleted-books)\/[A-Za-z0-9_-]+$/.test(a.path))
            throw new Error("invalid purge path");
          await rm(safe(a.path), { recursive: true, force: true });
        } else if (op === "readClipboard")
          result = await readFile("/tmp/notebook-ui-clipboard", "utf8").catch(
            () => "",
          );
        else if (op === "clipboard")
          await writeFile("/tmp/notebook-ui-clipboard", a.text);
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ result }));
        return;
      }
      const pathname = new URL(req.url, "http://local").pathname;
      const path = resolve(assets, "." + decodeURIComponent(pathname));
      if (!path.startsWith(assets + "/")) throw Error("outside assets");
      const data = await readFile(path);
      res.setHeader(
        "Content-Type",
        {
          ".html": "text/html",
          ".js": "text/javascript",
          ".css": "text/css",
          ".png": "image/png",
          ".svg": "image/svg+xml",
          ".woff2": "font/woff2",
        }[extname(path)] || "application/octet-stream",
      );
      res.end(data);
    } catch (e) {
      res.statusCode = 500;
      res.end(JSON.stringify({ error: e.message }));
    }
  })
  .listen(Number(process.env.NOTEBOOK_PREVIEW_PORT || 4173), "127.0.0.1", () =>
    process.stdout.write(
      `Notebook UI preview on port ${process.env.NOTEBOOK_PREVIEW_PORT || 4173}\n`,
    ),
  );
